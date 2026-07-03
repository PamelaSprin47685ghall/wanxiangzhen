module Wanxiangzhen.Shell.CoordinatorLifecycle

open Fable.Core
open Fable.Core.JsInterop
open Wanxiangzhen.Kernel.Task
open Wanxiangzhen.Kernel.Dag
open Wanxiangzhen.Kernel.SquadEvent
open Wanxiangzhen.Kernel.FfDecision
open Wanxiangzhen.Kernel.SquadUpdateIdAssign
open Wanxiangzhen.Kernel.SquadConfig
open Wanxiangzhen.Shell.Dyn
open Wanxiangzhen.Shell.GitShell
open Wanxiangzhen.Shell.SerialQueue
open Wanxiangzhen.Shell.HttpServer
open Wanxiangzhen.Shell.HttpCodec
open Wanxiangzhen.Shell.EventCodec
open Wanxiangzhen.Shell.ConfigReader
open Wanxiangzhen.Shell.SessionIo
open Wanxiangzhen.Shell.SquadEventLogRuntime
open Wanxiangzhen.Shell.SlaveSpawn
open Wanxiangzhen.Shell.PidMonitor
open Wanxiangzhen.Shell.SymlinkShell
open Wanxiangzhen.Shell.Yaml
open Wanxiangzhen.Shell.CoordinatorRuntime
open Wanxiangzhen.Shell.CoordinatorOps

let handleSquadKill (rt: CoordinatorRuntime) (optSessionId: string option) : JS.Promise<unit> =
    promise {
        let targetDagOpt =
            match optSessionId with
            | Some sid when sid <> rt.Dag.SessionId ->
                rt.Sessions.TryFind sid
            | _ -> Some rt.Dag
        match targetDagOpt with
        | None -> ()
        | Some targetDag ->
            let toKill =
                targetDag.Tasks |> Map.toList |> List.map snd
                |> List.filter (fun t -> t.Status = Running || t.Status = Submitted)
            let targetSessionId =
                match optSessionId with
                | Some sid when sid <> rt.Dag.SessionId -> sid
                | _ -> rt.Dag.SessionId
            for t in toKill do
                t.SlavePid |> Option.iter (safeKillPid rt.Deps)
            let! appendOk = commitEvent rt (SquadCancelled targetSessionId)
            match appendOk with
            | Error err -> rt.InjectError <- Some (sprintf "squad_cancelled append failed for %s: %s" targetSessionId err)
            | Ok () ->
                let updated = foldEvent targetDag (SquadCancelled targetSessionId)
                if targetSessionId = rt.Dag.SessionId then
                    rt.Dag <- updated
                else
                    rt.Sessions <- rt.Sessions.Add(targetSessionId, updated)
                schedulerTick rt |> Promise.start
    }

let handleSquadUpdate (rt: CoordinatorRuntime) (args: obj) : JS.Promise<string> =
    promise {
        let eventsRaw = get args "events"
        if isNullish eventsRaw || not (isArray eventsRaw) then
            return formatSquadUpdateOutcome (InvalidInput "events must be a non-empty array.")
        else
            let rawEvents = (eventsRaw :?> obj array) |> Array.toList

            // Validation pass 1: every tasks_created entry must have a non-empty tasks[] array
            let invalidTasksArray =
                rawEvents |> List.tryFind (fun e ->
                    str e "type" = "tasks_created" &&
                    let t = get e "tasks"
                    isNullish t || not (isArray t))
            match invalidTasksArray with
            | Some _ ->
                return formatSquadUpdateOutcome (InvalidInput "tasks_created must have a non-empty tasks array.")
            | None ->

            // Validation pass 2: every task inside tasks[] must have non-empty title and description
            let invalidTask =
                rawEvents |> List.tryPick (fun e ->
                    if str e "type" <> "tasks_created" then None
                    else
                        let tasksRaw = get e "tasks"
                        let tasks = tasksRaw :?> obj array
                        tasks |> Array.tryFind (fun t ->
                            str t "title" = "" || str t "description" = "") |> Option.map Some
                    |> Option.bind (fun ot -> ot |> Option.map (fun _ -> e)))
            match invalidTask with
            | Some badEv ->
                let tasksRaw = get badEv "tasks"
                let tasks = tasksRaw :?> obj array
                let badT = tasks |> Array.find (fun t -> str t "title" = "" || str t "description" = "")
                let badId = let v = get badT "taskId" in if isNullish v then "<no-id>" else string v
                return formatSquadUpdateOutcome (InvalidInput (sprintf "task '%s' must have non-empty title and description." badId))
            | None ->

            // Aggregate: extract task objects from nested tasks[] arrays
            let aggregated =
                rawEvents |> List.fold (fun (acc, hasCancelled) e ->
                    if str e "type" = "tasks_created" then
                        let tasksRaw = get e "tasks"
                        let tasks = tasksRaw :?> obj array |> Array.toList
                        let extracted = tasks |> List.map (fun t ->
                            (get t "taskId" |> fun v -> if isNullish v then None else Some (string v)),
                             str t "title", str t "description",
                             let dr = get t "dependsOn"
                             if isNullish dr || not (isArray dr) then [] else (dr :?> obj array) |> Array.map string |> Array.toList)
                        (acc @ extracted, hasCancelled)
                    else
                        (acc, true)) ([], false)

            let allRawTasks = fst aggregated
            let hasCancelled = snd aggregated

            let existingTaskIds = rt.Dag.Tasks |> Map.toList |> List.map fst |> Set.ofList
            let idGen = {
                Generate = generateTaskId
                RefExists = fun cand -> rt.Deps.ShowRefExists rt.ProjectRoot cand
            }
            match assignTaskIds existingTaskIds allRawTasks idGen with
            | Error () -> return formatSquadUpdateOutcome IdExhausted
            | Ok assigned ->
                return!
                    (promise {
                        let newIds = assigned |> List.map (fun (id, _, _, _) -> id) |> Set.ofList
                        let allIds = Set.union existingTaskIds newIds
                        let dangling =
                            assigned |> List.collect (fun (id, _, _, deps) ->
                                deps |> List.filter (fun d -> not (Set.contains d allIds))
                                     |> List.map (fun d -> id, d))
                        if dangling <> [] then
                            return formatSquadUpdateOutcome (DependencyErrors dangling)
                        else
                            let depsList = assigned |> List.map (fun (id, _, _, deps) -> id, deps)
                            let existingDeps = rt.Dag.Tasks |> Map.toList |> List.map (fun (id, t) -> id, t.DependsOn)
                            match detectCycle (existingDeps @ depsList) with
                            | Some cycle -> return formatSquadUpdateOutcome (CycleDetected cycle)
                            | None ->
                                let createdTasks = assigned |> List.map (fun (tid, title, desc, deps) -> tid, title, desc, deps)
                                let! appendOk =
                                    if createdTasks = [] then Promise.lift (Ok ())
                                    else commitEvent rt (TasksCreated (rt.Dag.SessionId, createdTasks))
                                match appendOk with
                                | Error _ -> return "Error: event log append failed."
                                | Ok () ->
                                    if createdTasks <> [] then
                                        let now = rt.Deps.Now ()
                                        for (tid, title, desc, deps) in assigned do
                                            let task = Wanxiangzhen.Kernel.Task.create tid title desc deps now
                                            rt.Dag <- rt.Dag |> addTask task
                                    if hasCancelled then do! handleSquadKill rt None
                                    let resultText =
                                        if hasCancelled && createdTasks = [] then
                                            sprintf "Squad session %s cancelled." rt.Dag.SessionId
                                        else
                                            sprintf "%d task(s) created, scheduler notified." (List.length createdTasks)
                                    schedulerTick rt |> Promise.start |> ignore
                                    return resultText
                    })
    }

let createWithDeps (client: obj) (directory: string) (config: SquadConfig) (masterBranch: string) (gitError: string option) (deps: CoordinatorDeps) : JS.Promise<CoordinatorRuntime> =
    promise {
        let token = System.String([| for _ in 0..31 -> "0123456789abcdef".[int (JS.Math.random() * 16.0)] |])
        let rtRef = ref None
        let! server = startServer token (fun m p b -> promise { match rtRef.Value with None -> return { StatusCode=503; Body=box {| result="not_ready" |} } | Some r -> return! routeHandler r m p b })
        let runtime = { Dag=empty "" ""; Sessions=Map.empty; Config=config; MasterBranch=masterBranch; ProjectRoot=directory; MasterSessionId=""; Client=client; Token=token; CoordinatorUrl=server.Url; GitQueue=SerialQueue(); InjectQueue=SerialQueue(); Server=server; Scheduling=false; PidPollHandle=None; GitError=gitError; InjectError=None; Deps=deps }
        rtRef.Value <- Some runtime
        startPidPolling runtime
        return runtime
    }

let create (client: obj) (directory: string) : JS.Promise<CoordinatorRuntime> =
    promise {
        let config = readConfig directory
        let mb, gitError =
            match config.MasterBranch with
            | Some b -> b, None
            | None ->
                try
                    if isDetached directory then
                        "master", Some "Detached HEAD detected. Please configure squad.masterBranch in AGENTS.md frontmatter."
                    else
                        revParseBranch directory, None
                with ex ->
                    "master", Some (string ex.Message)
        let depsRef = ref {
            PromptSession        = fun _ _ _ -> Promise.lift ()
            ReadAllSquadEvents   = readAllSquadEvents
            AppendSquadEvent     = appendSquadEvent
            TryWorktreeAdd       = fun _ _ _ _ -> Ok ""
            TryWorktreeRemoveForce = fun _ _ -> Ok ""
            TryBranchDeleteForce = fun _ _ -> Ok ""
            ShowRefExists        = fun _ _ -> false
            RevParseHead         = fun _ -> ""
            RevParseRef          = fun _ _ -> ""
            RevParseBranch       = fun _ -> ""
            IsDetached           = fun _ -> false
            StatusIsClean        = fun _ -> true
            MergeBaseIsAncestor  = fun _ _ _ -> false
            MergeFfOnly          = fun _ _ -> ""
            CreateSymlinks       = fun _ _ _ -> ()
            SpawnSlave           = fun _ _ _ _ -> ()
            IsPidAlive           = fun _ -> false
            KillPid              = fun _ _ -> ()
            WaitForPidDeath      = fun _ _ -> Promise.lift ()
            StartPolling         = fun _ _ -> box null
            StopPolling          = fun _ -> ()
            Now                  = fun () -> System.DateTime.UtcNow.ToString("o") }
        let deps = {
            PromptSession         = promptSession
            ReadAllSquadEvents    = readAllSquadEvents
            AppendSquadEvent      = appendSquadEvent
            TryWorktreeAdd        = tryWorktreeAdd
            TryWorktreeRemoveForce = tryWorktreeRemoveForce
            TryBranchDeleteForce  = tryBranchDeleteForce
            ShowRefExists         = showRefExists
            RevParseHead          = revParseHead
            RevParseRef           = revParseRef
            RevParseBranch        = revParseBranch
            IsDetached            = isDetached
            StatusIsClean         = statusIsClean
            MergeBaseIsAncestor   = mergeBaseIsAncestor
            MergeFfOnly           = mergeFfOnly
            CreateSymlinks        = createSymlinks
            SpawnSlave            = spawnSlave
            IsPidAlive            = isPidAlive
            KillPid               = killPid
            WaitForPidDeath       = fun pid r -> waitForPidDeath depsRef.Value pid r
            StartPolling          = startPolling
            StopPolling           = stopPolling
            Now                   = fun () -> System.DateTime.UtcNow.ToString("o") }
        depsRef.Value <- deps
        return! createWithDeps client directory config mb gitError deps
    }
