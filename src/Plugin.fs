module Plugin
open System
open System.IO
open System.Text
open System.Text.Json
open Fable.Core
open Fable.Core.JsInterop
open Kernel
open Shell.GitExecutor
open ShellConfigReader
open Shell.SlaveSpawn
open Shell.PidMonitor
open Shell.EventCodec
open Shell.CoordinatorState
open Shell.PromiseCompat
open Shell.PromiseQueue
open Shell.HttpServer
open Shell.SessionInject
open Shell.NodeInterop
open Shell.Clock

type IOpenCodeClient =
    abstract member Prompt: sessionId:string * agent:string * parts:(string * string) list -> System.Threading.Tasks.Task
    abstract member Messages: sessionId:string -> System.Threading.Tasks.Task<string list>
    abstract member Command: sessionId:string * command:string * arguments:string -> System.Threading.Tasks.Task

type PluginInput = {
    client: IOpenCodeClient
    worktree: string
    directory: string
}
type PluginTool = { description: string; toolArgs: obj; execute: (obj -> System.Threading.Tasks.Task<obj>) }

type Hooks = {
    tool: Map<string, PluginTool>
    config: (obj -> unit)
    commandExecuteBefore: (PluginInput -> JS.Promise<unit>)
    event: (PluginInput -> unit)
    dispose: (unit -> unit)
}

type SquadMode = Coordinator | Slave

type ISessionClient =
    abstract member Prompt: sessionId:string * parts:(string * string) list -> System.Threading.Tasks.Task
    abstract member Messages: sessionId:string -> System.Threading.Tasks.Task<string list>

type SessionClientAdapter(client: IOpenCodeClient) =
    interface ISessionClient with
        member x.Prompt(sessionId, parts) = client.Prompt(sessionId, "wanxiangzhen", parts)
        member x.Messages(sessionId) = client.Messages(sessionId)

let detectMode () : SquadMode =
    match Environment.GetEnvironmentVariable("SQUAD_COORDINATOR_URL") with
    | null -> Coordinator
    | url when String.IsNullOrWhiteSpace(url) -> Coordinator
    | _ -> Slave

let private jsonOpt = JsonSerializerOptions(WriteIndented = false, PropertyNameCaseInsensitive = true)
let private serialize (o: obj) = JsonSerializer.Serialize(o, jsonOpt)
let private deserialize<'T> (s: string) = JsonSerializer.Deserialize<'T>(s, jsonOpt)

let private tryGetSessionId (input: PluginInput) : string option =
    try
        let t = input.GetType()
        let p = t.GetProperty("sessionId")
        if not (isNull p) then Some (p.GetValue(input) :?> string) else None
    with _ -> None

let private tryGetCommand (input: PluginInput) : string option =
    try
        let t = input.GetType()
        let p = t.GetProperty("command")
        if not (isNull p) then Some (p.GetValue(input) :?> string) else None
    with _ -> None

let private tryGetCommandArgs (input: PluginInput) : string option =
    try
        let t = input.GetType()
        let p = t.GetProperty("arguments")
        if not (isNull p) then Some (p.GetValue(input) :?> string) else None
    with _ -> None

// ─── coordinator handlers ──────────────────────────────────────────

let private mkSubmitHandler (state: CoordinatorState) (gitExec: IGitExecutor) (masterBranch: string) (projectRoot: string) (gitQueue: SerialQueue) : RouteHandler =
    fun (taskId, body, _token) ->
        promise {
            let parsed = try Some (deserialize<{| commitSha: string |}>(body)) with _ -> None
            match parsed with
            | None -> return errJson "bad_request"
            | Some p ->
                let tid = TaskId taskId
                match state.TryGetTask(tid) with
                | Some t when t.status <> TaskStatus.Running ->
                    return serialize {| result = "not_submittable"; currentStatus = sprintf "%A" t.status |}
                | Some t ->
                    let branchName = defaultArg t.branchName taskId
                    let! resultTag, sha = gitQueue.Enqueue(fun () -> 
                        promise { return gitExec.FastForward projectRoot masterBranch tid branchName p.commitSha })
                    match resultTag with
                    | "merged" ->
                        state.OnSubmitResult(tid, SubmitResult.Merged sha, sha) |> ignore
                        return serialize {| result = "merged"; masterSha = sha |}
                    | "rebase_needed" ->
                        state.OnSubmitResult(tid, SubmitResult.RebaseNeeded(sha, ""), sha) |> ignore
                        return serialize {| result = "rebase_needed"; masterSha = sha; message = "Please rebase onto latest masterBranch" |}
                    | _ -> return serialize {| result = resultTag; message = sha |}
                | None -> return errJson "task_not_found"
        }

let private mkTaskHandler (state: CoordinatorState) : (string -> JS.Promise<string>) =
    fun taskId ->
        promise {
            let tid = TaskId taskId
            match state.TryGetTask(tid) with
            | Some t ->
                return serialize {| id = (match t.id with TaskId s -> s); title = t.title; description = t.description;
                                    dependsOn = t.dependsOn |> List.map (fun (TaskId s) -> s);
                                    status = sprintf "%A" t.status |}
            | None -> return jsonResponse 404 "{\"result\":\"task_not_found\"}"
        }

let private mkRegisterHandler (state: CoordinatorState) : (string * string -> JS.Promise<string>) =
    fun (taskId, body) ->
        promise {
            let parsed = try Some (deserialize<{| pid: int |}>(body)) with _ -> None
            match parsed with
            | Some p -> state.RegisterPid(TaskId taskId, p.pid); return serialize {| result = "registered" |}
            | None -> return errJson "bad_request"
        }

let private mkDoneHandler (state: CoordinatorState) : (string -> JS.Promise<string>) =
    fun taskId ->
        promise {
            state.OnSlaveExit(TaskId taskId) |> ignore
            return serialize {| result = "acknowledged" |}
        }

let private mkStateHandler (state: CoordinatorState) : (unit -> JS.Promise<string>) =
    fun () ->
        promise {
            let dag = state.Dag
            let tasks =
                dag.tasks |> Map.toSeq
                |> Seq.map (fun (TaskId id, t) ->
                    {| id = id; title = t.title; status = sprintf "%A" t.status;
                       dependsOn = t.dependsOn |> List.map (fun (TaskId s) -> s); slavePid = t.slavePid |})
                |> Seq.toArray
            return serialize {| sessions = [| {| sessionId = dag.sessionId; tasks = tasks |} |] |}
        }

let private genTaskId () : TaskId =
    let hex = Random.Shared.Next(0x10000).ToString("x4")
    TaskId $"squad-{hex}"

let private parseEvents (raw: string) (state: CoordinatorState) : Result<EventPayload list * Task list, string> =
    try
        use doc = JsonDocument.Parse(raw, JsonDocumentOptions(AllowTrailingCommas = true))
        let root = doc.RootElement
        let mutable events: EventPayload list = []
        let mutable createdTasks: Task list = []
        let mutable errors: string list = []
        let sid = state.Dag.sessionId
        if sid = "" then Error "No squad session active" else
        let arr = root.GetProperty("events").EnumerateArray() |> Seq.toList
        for el in arr do
            let typ = el.GetProperty("type").GetString()
            if typ = "task_created" then
                let title = el.GetProperty("title").GetString()
                let desc = el.GetProperty("description").GetString()
                let taskId =
                    try
                        let idStr = el.GetProperty("taskId").GetString()
                        if idStr <> "" then TaskId idStr else genTaskId()
                    with _ -> genTaskId()
                let deps =
                    try
                        let a2 = el.GetProperty("dependsOn").EnumerateArray()
                        [ for d in a2 do TaskId (d.GetString()) ]
                    with _ -> []
                let dag = state.Dag
                let knownIds = dag.tasks |> Map.toSeq |> Seq.map fst |> Set.ofSeq
                let missing = deps |> List.filter (fun d -> not (knownIds.Contains d) && not (createdTasks |> List.exists (fun t -> t.id = d)))
                if not missing.IsEmpty then
                    errors <- sprintf "Task %A depends on unknown task %A" taskId (missing.Head) :: errors
                else
                    let evt = EventPayload.taskCreated sid taskId title desc deps
                    let task = {
                        id = taskId; title = title; description = desc; dependsOn = deps
                        status = TaskStatus.Pending; worktreePath = None; branchName = None
                        slavePid = None; lastHeartbeatAt = None; mergedSha = None
                        createdAt = DateTime.UtcNow.ToString("o"); updatedAt = DateTime.UtcNow.ToString("o")
                    }
                    events <- evt :: events
                    createdTasks <- task :: createdTasks
        if not errors.IsEmpty then Error (String.Join("; ", errors))
        else
            let eventsRev = List.rev events
            let createdTasksRev = List.rev createdTasks
            // TopoSort check on temporary DAG before accepting events
            let tempDag = foldAll eventsRev state.Dag utcNowIso
            match topoSort tempDag with
            | Error cycleErr -> Error cycleErr
            | Ok _ -> Ok (eventsRev, createdTasksRev)
    with ex -> Error $"Failed to parse squad_update events: {ex.Message}"

let private detectVibeFs (projectRoot: string) : bool =
    try
        let agentsPath = Path.Combine(projectRoot, "AGENTS.md")
        if File.Exists agentsPath then
            let text = File.ReadAllText(agentsPath)
            text.Contains("wanxiangshu") || text.Contains("vibe-fs")
        else false
    with _ -> false

// ─── 主入口 ───────────────────────────────────────────────────────

let pluginForCoordinator (input: PluginInput) : Hooks =
    let gitExec = GitExecutor() :> IGitExecutor
    let projectRoot = input.worktree
    let masterBranch =
        try gitExec.ResolveMasterBranch(projectRoot)
        with _ -> "main"
    let config = readSquadConfig projectRoot
    let cfg = { config with masterBranch = if config.masterBranch = "" then masterBranch else config.masterBranch }
    let token = Guid.NewGuid().ToString("N").Substring(0, 32)
    let gitQueue = SerialQueue()
    let injectQueue = SerialQueue()
    let initialDag = { sessionId = ""; tasks = Map.empty; rootRequirement = "" }
    let state = CoordinatorState(initialDag, cfg, gitQueue, injectQueue)
    let masterSessionIdRef = ref None
    let replayDone = ref false

    let server = createServer token 0
    server.SetSubmitHandler(mkSubmitHandler state gitExec cfg.masterBranch projectRoot gitQueue)
    server.SetTaskHandler(mkTaskHandler state)
    server.SetRegisterHandler(mkRegisterHandler state)
    server.SetDoneHandler(mkDoneHandler state)
    server.SetStateHandler(mkStateHandler state)
    server.Start()
    let coordinatorUrl = sprintf "http://127.0.0.1:%d" server.Port
    state.SetCoordinatorUrl(coordinatorUrl)
    state.SetToken(token)
    let vibeFsDetected = detectVibeFs projectRoot
    printfn "[wanxiangzhen] 万象术 detected: %b" vibeFsDetected

    // PID health monitor (replaces non-existent child-exit hook, DEV_TALK 轮次 5.2)
    let mutable pidMonitorDisposable : System.IDisposable option = None
    pidMonitorDisposable <-
        Some (startMonitor
            (fun () -> state.GetAllTasks() |> Seq.map (fun (t, id) -> id, t.slavePid) |> List.ofSeq)
            (fun taskId ->
                let events = state.OnSlaveExit(taskId)
                events |> List.iter (fun evt ->
                    let prose = buildEventProse evt.squadEvent None
                    injectQueue.Enqueue(fun () ->
                        promise {
                            match !masterSessionIdRef with
                            | Some sid ->
                                let! _ = fromTaskUnit (input.client.Prompt(sid, "wanxiangzhen", [("text", encodeEvent evt prose)]))
                                return ()
                            | None -> return ()
                        }) |> ignore
                )
            )
            (cfg.maxConcurrent * 2000))

    // tryReplayOnce: replay DAG from history after session capture
    let tryReplayOnce () =
        if not !replayDone then
            match !masterSessionIdRef with
            | Some sid ->
                promise {
                    let! msgs = fromTask (input.client.Messages sid)
                    let events = 
                        msgs 
                        |> List.choose (fun msg -> 
                            match parseFrontMatter msg with
                            | null -> None
                            | fm -> eventPayloadFromMap fm)
                    if not events.IsEmpty then
                        state.ReplayFromHistory sid events
                        // Git reconcile: correct task status based on actual merge state
                        state.GitReconcile(fun (TaskId tid) ->
                            if gitExec.IsAncestor cfg.masterBranch tid projectRoot then
                                Some (gitExec.GetBranchSha tid projectRoot)
                            else None)
                        replayDone := true
                    return ()
                } |> ignore
            | None -> ()

    let tryCaptureSession (input: PluginInput) =
        match !masterSessionIdRef with
        | None ->
            match tryGetSessionId input with
            | Some sid when sid <> "" ->
                masterSessionIdRef := Some sid
                state.MasterSessionId <- Some sid
            | _ -> ()
        | _ -> ()

    let commandHandler (input: PluginInput) : JS.Promise<unit> =
        tryCaptureSession input
        tryReplayOnce ()
        let sid = defaultArg !masterSessionIdRef "unknown"
        let cmd = tryGetCommand input |> Option.defaultValue ""
        let args = tryGetCommandArgs input |> Option.defaultValue ""
        if cmd = "squad" then
            // Generate squad session ID
            let sessionId = 
                if state.Dag.sessionId = "" then Guid.NewGuid().ToString("N")
                else state.Dag.sessionId
            // Create and apply squad_created event
            let squadCreatedEvt = EventPayload.squadCreated sessionId args
            let dag1 = state.ApplyDag [squadCreatedEvt]
            // Inject the squad_created event into master session
            let prose = buildEventProse squadCreatedEvt.squadEvent None
            let eventMsg = encodeEvent squadCreatedEvt prose
            injectQueue.Enqueue(fun () ->
                promise {
                    let! _ = fromTaskUnit (input.client.Prompt(sid, "wanxiangzhen", [("text", eventMsg)]))
                    return ()
                }) |> ignore
            // Inject decomposition prompt for LLM
            let decompositionPrompt = $"""---
squad_event: squad_command
session_id: {sessionId}
command: create
requirement: {args}
---

/squad 需求已接收：{args}
请调用 squad_update 工具提交任务拆解。"""
            injectQueue.Enqueue(fun () ->
                promise {
                    let! _ = fromTaskUnit (input.client.Prompt(sid, "wanxiangzhen", [("text", decompositionPrompt)]))
                    return ()
                }) |> ignore
            promise { return () }
        else if cmd = "squad-kill" then
            // Create and apply squad_cancelled event
            let cancelEvt = { 
                squadEvent = SquadCancelled
                sessionId = state.Dag.sessionId
                taskId = None
                title = None
                description = None
                dependsOn = []
                masterSha = None
                worktreePath = None
                branchName = None
                slavePid = None
                merged = None
                requirement = None
            }
            let events = state.ApplyDag [cancelEvt]
            // Inject the squad_cancelled event
            let prose = buildEventProse SquadCancelled None
            let eventMsg = encodeEvent cancelEvt prose
            injectQueue.Enqueue(fun () ->
                promise {
                    let! _ = fromTaskUnit (input.client.Prompt(sid, "wanxiangzhen", [("text", eventMsg)]))
                    return ()
                }) |> ignore
            // Kill slave processes for running/submitted tasks (do not delete worktrees)
            for (task, taskId) in state.GetAllTasks() do
                match task.status with
                | TaskStatus.Running | TaskStatus.Submitted ->
                    match task.slavePid with
                    | Some pid -> nodeProcessKill pid "SIGTERM" |> ignore
                    | None -> ()
                | _ -> ()
            promise { return () }
        else
            promise { return () }

    let eventHandler (input: PluginInput) =
        tryCaptureSession input
        tryReplayOnce ()

    let dispose () =
        try server.Stop() with _ -> ()
        pidMonitorDisposable |> Option.iter (fun d -> try d.Dispose() with _ -> ())

    let squadUpdateTool : PluginTool = {
        description = "Submit task decomposition for squad session. Call after analyzing the user requirement to create tasks."
        toolArgs = Unchecked.defaultof<obj>
        execute = fun (raw: obj) ->
            let resultJson =
                try
                    let rawStr = match raw with :? string as s -> s | _ -> serialize raw
                    match parseEvents rawStr state with
                    | Error e -> sprintf "{\"result\":\"error\",\"message\":\"%s\"}" e
                    | Ok (events, tasks) ->
                        // Apply created events to DAG
                        let dag2 = state.ApplyDag(events)
                        let tickIn = { dag = dag2; config = cfg }
                        let tickOut = tick tickIn
                        // Apply tick events using ApplyDag (consistent)
                        state.ApplyDag(tickOut.events) |> ignore
                        for evt in tickOut.events do
                            let prose = buildEventProse evt.squadEvent None
                            injectQueue.Enqueue(fun () ->
                                promise {
                                    let! _ = fromTaskUnit (input.client.Prompt(state.Dag.sessionId, "wanxiangzhen", [("text", prose)]))
                                    return ()
                                }) |> ignore
                        let mutable spawnErrors = []
                        for task in tickOut.toStart do
                            try
                                let wtPath = gitExec.CreateWorktree task.id cfg.masterBranch projectRoot
                                let branchName = match task.id with Kernel.TaskId s -> s
                                let envList =
                                    [ "SQUAD_COORDINATOR_URL", coordinatorUrl
                                      "SQUAD_TASK_ID", (match task.id with Kernel.TaskId s -> s)
                                      "SQUAD_WORKTREE_PATH", wtPath
                                      "SQUAD_MASTER_BRANCH", cfg.masterBranch
                                      "SQUAD_TOKEN", token ]
                                    @ (if vibeFsDetected then [ "SQUAD_VIBEFS", "1" ] else [])
                                let envDict = System.Collections.Generic.Dictionary<string,string>()
                                for (k,v) in envList do envDict.Add(k, v)
                                let initialPrompt = buildSlavePrompt task vibeFsDetected
                                let spawnResult = spawnSlave envDict cfg.terminal wtPath initialPrompt
                                match spawnResult.childPid with
                                | Some pid -> state.RegisterPid(task.id, pid)
                                | None -> ()
                            with ex -> spawnErrors <- ex.Message :: spawnErrors
                        if spawnErrors.IsEmpty then
                            sprintf "{\"result\":\"ok\",\"message\":\"%d tasks created, scheduler notified.\"}" tasks.Length
                        else
                            sprintf "{\"result\":\"ok\",\"message\":\"%d tasks created but %d spawn errors: %s\"}" tasks.Length spawnErrors.Length (String.Join(", ", spawnErrors))
                with ex -> sprintf "{\"result\":\"error\",\"message\":\"%s\"}" ex.Message
            System.Threading.Tasks.Task.FromResult(box resultJson)
        }

    let configHandler (_cfgObj: obj) = ()

    { tool = Map.ofList [("squad_update", squadUpdateTool)]
      config = configHandler
      commandExecuteBefore = commandHandler
      event = eventHandler
      dispose = dispose }

// ─── Slave 模式 ────────────────────────────────────────────────────

let pluginForSlave (input: PluginInput) : Hooks =
    let coordinatorUrl = Environment.GetEnvironmentVariable("SQUAD_COORDINATOR_URL")
    let taskId = Environment.GetEnvironmentVariable("SQUAD_TASK_ID")
    let token = Environment.GetEnvironmentVariable("SQUAD_TOKEN")

    let httpCall (method: string) (path: string) (body: string) : string =
        try
            match method with
            | "GET" -> Shell.NodeInterop.httpGet coordinatorUrl token path
            | _ -> Shell.NodeInterop.httpPost coordinatorUrl token path body
        with _ -> "{\"result\":\"coordinator_unreachable\"}"

    let register () =
        let _ = httpCall "POST" $"/task/{taskId}/register" (serialize {| pid = currentPid() |})
        ()

    let submitToSquad (_: obj) : System.Threading.Tasks.Task<obj> =
        task {
            try
                let commitSha =
                    try NodeInterop.execGit input.worktree "rev-parse HEAD" |> Trim
                    with _ -> "unknown"
                let result = httpCall "POST" $"/task/{taskId}/submit" (serialize {| commitSha = commitSha |})
                return box result
            with ex ->
                let err = "{\"result\":\"error\",\"message\":\"" + ex.Message.Replace("\"","\\\"") + "\"}"
                return box err
        }

    let querySquad (args: obj) : System.Threading.Tasks.Task<obj> =
        task {
            let query = match args with :? string as s -> s | _ -> "state"
            try
                let path = if query = "state" then "/state" else $"/task/{query}"
                let result = httpCall "GET" path ""
                return box result
            with ex ->
                let err = "{\"result\":\"error\",\"message\":\"" + ex.Message.Replace("\"","\\\"") + "\"}"
                return box err
        }

    let dispose () =
        try
            let _ = httpCall "POST" $"/task/{taskId}/done" "{}"
            ()
        with _ -> ()

    register()
    let submitTool : PluginTool = { description = "Submit work to squad coordinator for fast-forward merge. Call after git commit."; toolArgs = Unchecked.defaultof<obj>; execute = submitToSquad }
    let queryTool : PluginTool = { description = "Query squad DAG state or specific task details."; toolArgs = Unchecked.defaultof<obj>; execute = querySquad }
    { tool = Map.ofList [("submit_to_squad", submitTool); ("query_squad", queryTool)]
      config = ignore; commandExecuteBefore = (fun _ -> Promise.lift ()); event = ignore; dispose = dispose }

/// The opencode plugin entry point. Exported as default for the host loader.
[<ExportDefault>]
let plugin (ctx: obj) : JS.Promise<obj> = pluginFor ctx

let pluginFor (input: PluginInput) : Hooks =
    match detectMode() with Coordinator -> pluginForCoordinator input | Slave -> pluginForSlave input
