module Wanxiangzhen.Shell.CoordinatorLifecycle

open Fable.Core
open Fable.Core.JsInterop
open Wanxiangzhen.Kernel.Task
open Wanxiangzhen.Kernel.Dag
open Wanxiangzhen.Kernel.SquadEvent
open Wanxiangzhen.Kernel.FfDecision
open Wanxiangzhen.Kernel.SquadConfig
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
open Wanxiangzhen.Shell.CoordinatorOps

let replayFromHistory (rt: CoordinatorRuntime) : JS.Promise<unit> =
    promise {
        if rt.MasterSessionId = "" then return ()
        else
            let! texts = rt.Deps.ReadAllTexts rt.Client rt.MasterSessionId ""
            let events = texts |> List.collect decodeEvents
            let mutable currentDag = empty "" ""
            let mutable sessions = Map.empty
            for ev in events do
                match ev with
                | SquadCreated (sid, req) ->
                    if currentDag.SessionId <> "" && not currentDag.Tasks.IsEmpty then
                        sessions <- sessions.Add(currentDag.SessionId, currentDag)
                    currentDag <- empty sid req
                | _ ->
                    currentDag <- foldEvent currentDag ev
            
            let reconciledTasks =
                currentDag.Tasks |> Map.map (fun _ t ->
                    if t.Status = Submitted || t.Status = Running then
                        match rt.GitError with
                        | Some _ ->
                            if t.Status = Submitted then
                                withReconciledStatus t TaskStatus.Running (rt.Deps.Now ())
                            else t
                        | None ->
                            match t.BranchName with
                            | Some b when rt.Deps.MergeBaseIsAncestor rt.ProjectRoot rt.MasterBranch b ->
                                let sha = rt.Deps.RevParseRef rt.ProjectRoot rt.MasterBranch
                                { (withReconciledStatus t TaskStatus.Merged (rt.Deps.Now ())) with MergedSha = Some sha }
                            | _ ->
                                if t.Status = Submitted then
                                    withReconciledStatus t TaskStatus.Running (rt.Deps.Now ())
                                else t
                    else t)
            
            rt.Dag <- { currentDag with Tasks = reconciledTasks }
            rt.Sessions <- sessions

            let orphans =
                rt.Dag.Tasks |> Map.toList |> List.map snd
                |> List.filter (fun t -> t.Status = Running && t.SlavePid.IsNone)
            if orphans <> [] then
                let names = orphans |> List.map (fun t -> t.Id) |> String.concat ", "
                let warning = sprintf "WARNING: Orphan running tasks without PID: %s. Use /squad-kill or ignore." names
                rt.Deps.PromptSession rt.Client rt.MasterSessionId warning |> Promise.start |> ignore
    }

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
            let now = rt.Deps.Now ()
            let updated =
                targetDag.Tasks |> Map.toList |> List.map snd
                |> List.fold (fun dag t ->
                    if t.Status = Running || t.Status = Submitted then
                        dag |> updateTask t.Id (fun x -> withStatus x Cancelled now)
                    else dag) targetDag
            if targetSessionId = rt.Dag.SessionId then
                rt.Dag <- updated
            else
                rt.Sessions <- rt.Sessions.Add(targetSessionId, updated)
            injectEventFire rt (SquadCancelled targetSessionId)
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

            // Assign IDs
            let existingTaskIds = rt.Dag.Tasks |> Map.toList |> List.map fst |> Set.ofList
            let rec genWithRetries (used: Set<string>) (remaining: int) : string option =
                if remaining <= 0 then None
                else
                    let cand = generateTaskId ()
                    let inExisting = Set.contains cand existingTaskIds
                    let inUsed = Set.contains cand used
                    let onRef = rt.Deps.ShowRefExists rt.ProjectRoot cand
                    if inExisting || inUsed || onRef then genWithRetries used (remaining - 1)
                    else Some cand
            let rec assignIds (used: Set<string>) (tasks: (string option * string * string * string list) list) : (string * string * string * string list) list =
                match tasks with
                | [] -> []
                | (idOpt, title, desc, deps) :: rest ->
                    match idOpt with
                    | Some id ->
                        (id, title, desc, deps) :: assignIds (Set.add id used) rest
                    | None ->
                        match genWithRetries used 10 with
                        | Some tid ->
                            (tid, title, desc, deps) :: assignIds (Set.add tid used) rest
                        | None ->
                            let tid = generateTaskId ()
                            (tid, title, desc, deps) :: assignIds (Set.add tid used) rest
            let assigned = assignIds existingTaskIds allRawTasks

            // Dangling dependency check
            let newIds = assigned |> List.map (fun (id, _, _, _) -> id) |> Set.ofList
            let allIds = Set.union existingTaskIds newIds
            let dangling =
                assigned |> List.collect (fun (id, _, _, deps) ->
                    deps |> List.filter (fun d -> not (Set.contains d allIds))
                         |> List.map (fun d -> id, d))
            if dangling <> [] then return formatSquadUpdateOutcome (DependencyErrors dangling)
            else

            // Cycle detection
            let depsList = assigned |> List.map (fun (id, _, _, deps) -> id, deps)
            let existingDeps = rt.Dag.Tasks |> Map.toList |> List.map (fun (id, t) -> id, t.DependsOn)
            match detectCycle (existingDeps @ depsList) with
            | Some cycle -> return formatSquadUpdateOutcome (CycleDetected cycle)
            | None ->

            // Add tasks to DAG
            let now = rt.Deps.Now ()
            for (tid, title, desc, deps) in assigned do
                let task = Wanxiangzhen.Kernel.Task.create tid title desc deps now
                rt.Dag <- rt.Dag |> addTask task

            // Handle cancellation
            if hasCancelled then do! handleSquadKill rt None

            // Build result text and inject event
            let createdTasks = assigned |> List.map (fun (tid, title, desc, deps) -> tid, title, desc, deps)
            let resultText =
                if hasCancelled && createdTasks = [] then
                    sprintf "Squad session %s cancelled." rt.Dag.SessionId
                else
                    sprintf "%d task(s) created, scheduler notified." (List.length createdTasks)
            if createdTasks <> [] then do! injectEvent rt (TasksCreated (rt.Dag.SessionId, createdTasks))
            schedulerTick rt |> Promise.start |> ignore
            return resultText
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
            ReadAllTexts         = fun _ _ _ -> Promise.lift []
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
            ReadAllTexts          = readAllTexts
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
