module Wanxiangzhen.Shell.CoordinatorLifecycle

open Fable.Core
open Fable.Core.JsInterop
open Wanxiangzhen.Kernel.Task
open Wanxiangzhen.Kernel.Dag
open Wanxiangzhen.Kernel.SquadEvent
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
open Wanxiangzhen.Shell.StateBackup
open Wanxiangzhen.Shell.CoordinatorOps

let replayFromHistory (rt: CoordinatorRuntime) : JS.Promise<unit> =
    promise {
        if rt.MasterSessionId = "" then return ()
        else
            let! texts = readAllTexts rt.Client rt.MasterSessionId ""
            let events = texts |> List.choose decodeEvent
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
                                withReconciledStatus t TaskStatus.Running (nowUtc ())
                            else t
                        | None ->
                            match t.BranchName with
                            | Some b when mergeBaseIsAncestor rt.ProjectRoot rt.MasterBranch b ->
                                let sha = revParseRef rt.ProjectRoot rt.MasterBranch
                                { (withReconciledStatus t TaskStatus.Merged (nowUtc ())) with MergedSha = Some sha }
                            | _ ->
                                if t.Status = Submitted then
                                    withReconciledStatus t TaskStatus.Running (nowUtc ())
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
                promptSession rt.Client rt.MasterSessionId warning |> Promise.start |> ignore
            if rt.Dag.Tasks.IsEmpty then loadStateFallback rt
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
                t.SlavePid |> Option.iter (fun pid ->
                    try killPid pid (box "SIGTERM") with _ -> ())
            let now = nowUtc ()
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

let handleSquadUpdate (rt: CoordinatorRuntime) (args: obj) : string =
    let eventsRaw = get args "events"
    if isNullish eventsRaw || not (isArray eventsRaw) then
        formatSquadUpdateOutcome (InvalidInput "events must be a non-empty array.")
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
                     |> List.map (fun d -> id, d))
        if dangling <> [] then formatSquadUpdateOutcome (DependencyErrors dangling)
        else
            let fullDepsList =
                let existingDeps = rt.Dag.Tasks |> Map.toList |> List.map (fun (id, t) -> id, t.DependsOn)
                existingDeps @ depsList
            match detectCycle fullDepsList with
            | Some cycle -> formatSquadUpdateOutcome (CycleDetected cycle)
            | None ->
                let now = nowUtc ()
                for (ty, tid, title, desc, deps) in inputsWithIds do
                    if ty = "task_created" then
                        let task = Wanxiangzhen.Kernel.Task.create tid title desc deps now
                        rt.Dag <- rt.Dag |> addTask task
                        injectEventFire rt (TaskCreated (rt.Dag.SessionId, tid, title, desc, deps))
                    elif ty = "squad_cancelled" then
                        handleSquadKill rt None |> Promise.start
                schedulerTick rt |> Promise.start
                saveState rt
                formatSquadUpdateOutcome (Success created.Length)

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
        let token =
            let hex = "0123456789abcdef"
            System.String([| for _ in 0..31 -> hex[int (JS.Math.random () * 16.0)] |])
        let rtRef = ref None
        let! server =
            startServer token (fun m p b ->
                promise {
                    match rtRef.Value with
                    | None -> return { StatusCode = 503; Body = box {| result = "not_ready" |} }
                    | Some r ->
                        let handler = routeHandler r
                        return! handler m p b
                })
        let runtime = {
            Dag = empty "" ""
            Sessions = Map.empty
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
            GitError = gitError
        }
        rtRef.Value <- Some runtime
        startPidPolling runtime
        startConfigWatch runtime
        return runtime
    }
