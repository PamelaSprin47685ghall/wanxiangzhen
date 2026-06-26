module Wanxiangzhen.Shell.CoordinatorOps

open Fable.Core
open Fable.Core.JsInterop
open Wanxiangzhen.Kernel.Task
open Wanxiangzhen.Kernel.Dag
open Wanxiangzhen.Kernel.SquadEvent
open Wanxiangzhen.Kernel.Scheduler
open Wanxiangzhen.Kernel.SquadConfig
open Wanxiangzhen.Kernel.SquadPrompts
open Wanxiangzhen.Kernel.FfDecision
open Wanxiangzhen.Shell.Dyn
open Wanxiangzhen.Shell.GitShell
open Wanxiangzhen.Shell.SerialQueue
open Wanxiangzhen.Shell.HttpServer
open Wanxiangzhen.Shell.HttpCodec
open Wanxiangzhen.Shell.EventCodec
open Wanxiangzhen.Shell.ConfigReader
open Wanxiangzhen.Shell.SessionIo
open Wanxiangzhen.Shell.SlaveSpawn
open Wanxiangzhen.Shell.PidMonitor
open Wanxiangzhen.Shell.SymlinkShell
open Wanxiangzhen.Shell.Yaml
open Wanxiangzhen.Shell.CoordinatorRuntime

let private extractTaskId (path: string) (suffix: string) : string =
    let prefix = "/task/"
    let suf = "/" + suffix
    if path.StartsWith prefix && path.EndsWith suf then
        path.Substring(prefix.Length, path.Length - prefix.Length - suf.Length)
    else ""

let handleSlaveExit (rt: CoordinatorRuntime) (taskId: string) : JS.Promise<unit> =
    promise {
        match findTask taskId rt.Dag with
        | None -> return ()
        | Some task when isTerminal task.Status -> return ()
        | Some task ->
            let now = nowUtc ()
            rt.Dag <- rt.Dag |> updateTask taskId (fun t -> withStatus t Done now)
            injectEventFire rt { mkEvent TaskDone rt.Dag.SessionId with TaskId = Some taskId; Merged = Some false }
            cleanupTask rt task
    }

let private startTask (rt: CoordinatorRuntime) (taskId: string) : JS.Promise<unit> =
    promise {
        match findTask taskId rt.Dag with
        | None -> return ()
        | Some task ->
            let parent =
                let lastSlash = rt.ProjectRoot.LastIndexOf '/'
                rt.ProjectRoot.Substring(0, lastSlash + 1)
            let wtPath = parent + "worktree-" + taskId
            let worktreeOk =
                try
                    worktreeAdd rt.ProjectRoot taskId wtPath rt.MasterBranch
                    true
                with _ -> false
            if not worktreeOk then return ()
            createSymlinks wtPath rt.ProjectRoot rt.Config.SharedDirs
            let prompt = buildSlavePrompt taskId task.Title task.Description rt.MasterBranch false
            let slaveEnv = createObj []
            assignInto slaveEnv (get nodeProcess "env") |> ignore
            setKey slaveEnv "SQUAD_COORDINATOR_URL" (box rt.CoordinatorUrl)
            setKey slaveEnv "SQUAD_TASK_ID" (box taskId)
            setKey slaveEnv "SQUAD_WORKTREE_PATH" (box wtPath)
            setKey slaveEnv "SQUAD_MASTER_BRANCH" (box rt.MasterBranch)
            setKey slaveEnv "SQUAD_TOKEN" (box rt.Token)
            spawnSlave rt.Config.Terminal wtPath slaveEnv prompt
            let now = nowUtc ()
            rt.Dag <- rt.Dag |> updateTask taskId (fun t ->
                { t with Status = Running
                         WorktreePath = Some wtPath
                         BranchName = Some taskId
                         UpdatedAt = now })
            injectEventFire rt { mkEvent TaskStarted rt.Dag.SessionId
                                 with TaskId = Some taskId; WorktreePath = Some wtPath
                                      BranchName = Some taskId }
    }

let private schedulerTick (rt: CoordinatorRuntime) : JS.Promise<unit> =
    if rt.Scheduling then Promise.lift ()
    else
        rt.Scheduling <- true
        promise {
            try
                let decision = decide rt.Dag rt.Config.MaxConcurrent
                for tid in decision.TasksToStart do
                    do! startTask rt tid
            finally
                rt.Scheduling <- false
        }

let handleSubmit (rt: CoordinatorRuntime) (taskId: string) (reportedSha: string) : JS.Promise<HttpResponse> =
    match findTask taskId rt.Dag with
    | None ->
        Promise.lift { StatusCode = 404; Body = encodeResult "task_not_found" }
    | Some task when task.Status <> Running ->
        Promise.lift { StatusCode = 200
                       Body = encodeFfResponseBody (NotSubmittable (statusToString task.Status)) }
    | Some _ ->
        promise {
            let now = nowUtc ()
            rt.Dag <- rt.Dag |> updateTask taskId (fun t -> withStatus t Submitted now)
            injectEventFire rt { mkEvent TaskSubmitted rt.Dag.SessionId
                                 with TaskId = Some taskId; CommitSha = Some reportedSha }
            let! result =
                rt.GitQueue.Enqueue(fun () ->
                    promise {
                        let branchSha = revParseRef rt.ProjectRoot taskId
                        if branchSha <> reportedSha then return StaleCommit
                        else
                            let cur = revParseBranch rt.ProjectRoot
                            if cur <> rt.MasterBranch then
                                return CoordinatorNotReady "not_on_master"
                            elif not (statusIsClean rt.ProjectRoot) then
                                return CoordinatorNotReady "dirty"
                            elif mergeBaseIsAncestor rt.ProjectRoot rt.MasterBranch taskId then
                                let sha = mergeFfOnly rt.ProjectRoot taskId
                                return Merged sha
                            else
                                let sha = revParseRef rt.ProjectRoot rt.MasterBranch
                                return RebaseNeeded sha })
            match result with
            | Merged sha ->
                let n2 = nowUtc ()
                rt.Dag <- rt.Dag |> updateTask taskId (fun t ->
                    { t with Status = TaskStatus.Merged; MergedSha = Some sha; UpdatedAt = n2 })
                injectEventFire rt { mkEvent TaskMerged rt.Dag.SessionId
                                     with TaskId = Some taskId; MasterSha = Some sha }
                match findTask taskId rt.Dag with
                | Some t ->
                    match t.SlavePid with
                    | Some pid ->
                        killPid pid (box "SIGTERM")
                        do! waitForPidDeath pid 5
                    | None -> ()
                    cleanupTask rt t
                | None -> ()
                do! schedulerTick rt
            | _ ->
                let n2 = nowUtc ()
                rt.Dag <- rt.Dag |> updateTask taskId (fun t -> withStatus t Running n2)
                do! schedulerTick rt
            return { StatusCode = 200; Body = encodeFfResponseBody result }
        }

let routeHandler (rt: CoordinatorRuntime) : RouteHandler =
    fun method path bodyStr ->
        promise {
            let body = if bodyStr = "" then box null else bodyStr |> box
            match method, path with
            | "POST", p when p.EndsWith "/submit" ->
                let tid = extractTaskId p "submit"
                let sha = decodeSubmitBody body |> Option.defaultValue ""
                return! handleSubmit rt tid sha
            | "POST", p when p.EndsWith "/register" ->
                let tid = extractTaskId p "register"
                match decodeRegisterBody body with
                | Some pid ->
                    rt.Dag <- rt.Dag |> updateTask tid (fun t -> { t with SlavePid = Some pid })
                    return { StatusCode = 200; Body = encodeResult "registered" }
                | None -> return { StatusCode = 400; Body = encodeResult "bad_request" }
            | "POST", p when p.EndsWith "/done" ->
                let tid = extractTaskId p "done"
                do! handleSlaveExit rt tid
                return { StatusCode = 200; Body = encodeResult "acknowledged" }
            | "GET", "/state" ->
                return { StatusCode = 200; Body = encodeStateSnapshot rt.Dag }
            | "GET", p when p.StartsWith "/task/" ->
                let tid = p.Substring 6
                match findTask tid rt.Dag with
                | None -> return { StatusCode = 404; Body = encodeResult "task_not_found" }
                | Some t -> return { StatusCode = 200; Body = encodeTaskDetail t }
            | _ -> return { StatusCode = 404; Body = encodeResult "not_found" }
        }

let private startPidPolling (rt: CoordinatorRuntime) : unit =
    rt.PidPollHandle <-
        Some (startPolling 2000 (fun () ->
            promise {
                let toCheck =
                    rt.Dag.Tasks |> Map.toList |> List.map snd
                    |> List.filter (fun t ->
                        (t.Status = Running || t.Status = Submitted) && t.SlavePid.IsSome)
                for t in toCheck do
                    match t.SlavePid with
                    | Some pid when not (isPidAlive pid) -> do! handleSlaveExit rt t.Id
                    | _ -> ()
            } |> Promise.start))

let replayFromHistory (rt: CoordinatorRuntime) : JS.Promise<unit> =
    promise {
        if rt.MasterSessionId = "" then return ()
        else
            let! texts = readAllTexts rt.Client rt.MasterSessionId ""
            let events = texts |> List.choose decodeEvent
            rt.Dag <- foldEvents events rt.Dag
            for t in rt.Dag.Tasks |> Map.toList |> List.map snd do
                if t.Status = Submitted || t.Status = Running then
                    match t.BranchName with
                    | Some b when mergeBaseIsAncestor rt.ProjectRoot rt.MasterBranch b ->
                        let sha = revParseRef rt.ProjectRoot rt.MasterBranch
                        rt.Dag <- rt.Dag |> updateTask t.Id (fun x ->
                            { x with Status = TaskStatus.Merged; MergedSha = Some sha })
                    | _ -> ()
    }

let handleSquadKill (rt: CoordinatorRuntime) : JS.Promise<unit> =
    promise {
        let toKill =
            rt.Dag.Tasks |> Map.toList |> List.map snd
            |> List.filter (fun t -> t.Status = Running || t.Status = Submitted)
        for t in toKill do
            t.SlavePid |> Option.iter (fun pid ->
                try killPid pid (box "SIGTERM") with _ -> ())
            let now = nowUtc ()
            rt.Dag <- rt.Dag |> updateTask t.Id (fun x -> withStatus x Cancelled now)
        injectEventFire rt { mkEvent SquadCancelled rt.Dag.SessionId
                             with TaskId = None }
    }

let buildSquadPrompt (requirement: string) (sessionId: string) : string =
    buildDecompositionPrompt requirement sessionId

let injectSquadCommand (rt: CoordinatorRuntime) (requirement: string) (sessionId: string)
                       : JS.Promise<unit> =
    let prompt = buildSquadPrompt requirement sessionId
    injectEventFire rt { mkEvent SquadCreated sessionId with Description = Some requirement }
    promptSession rt.Client sessionId prompt |> Promise.start |> ignore
    Promise.lift ()

let handleSquadUpdate (rt: CoordinatorRuntime) (args: obj) : string =
    let eventsRaw = get args "events"
    if isNullish eventsRaw || not (isArray eventsRaw) then
        "Error: events must be a non-empty array."
    else
        let inputsWithIds =
            (eventsRaw :?> obj array) |> Array.toList
            |> List.map (fun e ->
                let ty = str e "type"
                let taskId = let v = get e "taskId" in if isNullish v then None else Some (string v)
                let title = str e "title"
                let desc = str e "description"
                let depsRaw = get e "dependsOn"
                let deps =
                    if isNullish depsRaw || not (isArray depsRaw) then []
                    else (depsRaw :?> obj array) |> Array.map string |> Array.toList
                let tid = taskId |> Option.defaultValue (generateTaskId ())
                ty, tid, title, desc, deps)
        let created = inputsWithIds |> List.filter (fun (ty, _, _, _, _) -> ty = "task_created")
        let depsList = created |> List.map (fun (_, tid, _, _, deps) -> tid, deps)
        let existingIds = rt.Dag.Tasks |> Map.toList |> List.map fst |> Set.ofList
        let newIds = depsList |> List.map fst |> Set.ofList
        let allIds = Set.union existingIds newIds
        let dangling =
            depsList |> List.collect (fun (id, deps) ->
                deps |> List.filter (fun d -> not (Set.contains d allIds))
                     |> List.map (fun d -> id + " dependsOn unknown " + d))
        if dangling <> [] then
            sprintf "Dependency error: %s. Fix dependencies." (dangling |> String.concat "; ")
        else
            match detectCycle depsList with
            | Some cycle ->
                sprintf "Dependency cycle detected: %s. Please re-decompose without cycles."
                    (cycle |> String.concat " → ")
            | None ->
                let now = nowUtc ()
                for (ty, tid, title, desc, deps) in inputsWithIds do
                    if ty = "task_created" then
                        let task = create tid title desc deps now
                        rt.Dag <- rt.Dag |> addTask task
                        injectEventFire rt { mkEvent TaskCreated rt.Dag.SessionId
                                             with TaskId = Some tid; Title = Some title
                                                  Description = Some desc; DependsOn = Some deps }
                    elif ty = "squad_cancelled" then
                        handleSquadKill rt |> Promise.start
                schedulerTick rt |> Promise.start
                sprintf "%d tasks created, scheduler notified." created.Length

let create (client: obj) (directory: string) : JS.Promise<CoordinatorRuntime> =
    promise {
        let config = readConfig directory
        let mb =
            match config.MasterBranch with
            | Some b -> b
            | None ->
                if isDetached directory then "master"
                else revParseBranch directory
        let token =
            let hex = "0123456789abcdef"
            System.String([| for _ in 0..31 -> hex[int (JS.Math.random () * 16.0)] |])
        let mutable rtOpt : CoordinatorRuntime option = None
        let! server =
            startServer token (fun m p b ->
                promise {
                    let r = rtOpt |> Option.defaultValue (failwith "CoordinatorRuntime not yet initialized")
                    let handler = routeHandler r
                    return! handler m p b
                })
        let runtime = {
            Dag = empty "" ""
            Config = config
            MasterBranch = mb
            ProjectRoot = directory
            MasterSessionId = ""
            Client = client
            Token = token
            CoordinatorUrl = server.Url
            GitQueue = SerialQueue ()
            InjectQueue = SerialQueue ()
            Server = server
            Scheduling = false
            PidPollHandle = None
        }
        rtOpt <- Some runtime
        startPidPolling runtime
        return runtime
    }
