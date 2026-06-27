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
open Wanxiangzhen.Shell.StateBackup

let internal extractTaskId (path: string) (suffix: string) : string =
    let prefix = "/task/"
    let suf = "/" + suffix
    if path.StartsWith prefix && path.EndsWith suf then
        path.Substring(prefix.Length, path.Length - prefix.Length - suf.Length)
    else ""

let formatDagText (rt: CoordinatorRuntime) : string =
    Wanxiangzhen.Kernel.Dag.formatDag rt.Dag

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
            let vibeFs = detectVibeFs rt.ProjectRoot
            let prompt = buildSlavePrompt taskId task.Title task.Description rt.MasterBranch vibeFs
            let slaveEnv = createObj []
            assignInto slaveEnv (get nodeProcess "env") |> ignore
            setKey slaveEnv "SQUAD_COORDINATOR_URL" (box rt.CoordinatorUrl)
            setKey slaveEnv "SQUAD_TASK_ID" (box taskId)
            setKey slaveEnv "SQUAD_WORKTREE_PATH" (box wtPath)
            setKey slaveEnv "SQUAD_MASTER_BRANCH" (box rt.MasterBranch)
            setKey slaveEnv "SQUAD_TOKEN" (box rt.Token)
            if vibeFs then setKey slaveEnv "SQUAD_VIBEFS" (box "1")
            spawnSlave rt.Config.Terminal wtPath slaveEnv prompt
            let now = nowUtc ()
            rt.Dag <- rt.Dag |> updateTask taskId (fun t ->
                { (withStatus t Running now) with
                     WorktreePath = Some wtPath
                     BranchName = Some taskId })
            injectEventFire rt (TaskStarted (rt.Dag.SessionId, taskId, wtPath, taskId))
    }

let schedulerTick (rt: CoordinatorRuntime) : JS.Promise<unit> =
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

let handleSlaveExit (rt: CoordinatorRuntime) (taskId: string) : JS.Promise<unit> =
    promise {
        match findTask taskId rt.Dag with
        | None -> return ()
        | Some task when isTerminal task.Status -> return ()
        | Some task ->
            let now = nowUtc ()
            rt.Dag <- rt.Dag |> updateTask taskId (fun t -> withStatus t Done now)
            injectEventFire rt (TaskDone (rt.Dag.SessionId, taskId, false))
            cleanupTask rt task
            saveState rt
            do! schedulerTick rt
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
            injectEventFire rt (TaskSubmitted (rt.Dag.SessionId, taskId, reportedSha))
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
                    { (withStatus t TaskStatus.Merged n2) with MergedSha = Some sha })
                injectEventFire rt (TaskMerged (rt.Dag.SessionId, taskId, sha))
                match findTask taskId rt.Dag with
                | Some t ->
                    match t.SlavePid with
                    | Some pid ->
                        killPid pid (box "SIGTERM")
                        do! waitForPidDeath pid 5
                    | None -> ()
                    cleanupTask rt t
                | None -> ()
                saveState rt
                do! schedulerTick rt
            | _ ->
                let n2 = nowUtc ()
                rt.Dag <- rt.Dag |> updateTask taskId (fun t -> withStatus t Running n2)
                do! schedulerTick rt
            return { StatusCode = 200; Body = encodeFfResponseBody result }
        }

let routeHandler (rt: CoordinatorRuntime) : RouteHandler =
    fun method path body ->
        promise {
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
            | "POST", p when p.EndsWith "/log" ->
                let tid = extractTaskId p "log"
                match decodeLogBody body with
                | Some _msg ->
                    return { StatusCode = 200; Body = encodeResult "logged" }
                | None -> return { StatusCode = 400; Body = encodeResult "bad_request" }
            | "GET", "/state" ->
                return { StatusCode = 200; Body = encodeFullState rt.Dag rt.Sessions }
            | "GET", p when p.StartsWith "/task/" ->
                let tid = p.Substring 6
                match findTask tid rt.Dag with
                | None -> return { StatusCode = 404; Body = encodeResult "task_not_found" }
                | Some t -> return { StatusCode = 200; Body = encodeTaskDetail t }
            | _ -> return { StatusCode = 404; Body = encodeResult "not_found" }
        }

let startPidPolling (rt: CoordinatorRuntime) : unit =
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
                    | Some pid when isPidAlive pid ->
                        let now = nowUtc ()
                        rt.Dag <- rt.Dag |> updateTask t.Id (fun x -> { x with LastHeartbeatAt = Some now })
                    | _ -> ()
            } |> Promise.start))

