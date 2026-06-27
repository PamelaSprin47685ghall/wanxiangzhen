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

[<Global("process")>]
let private nodeProcess : obj = jsNative

let internal extractTaskId (path: string) (suffix: string) : string =
    let prefix = "/task/"
    let suf = "/" + suffix
    if path.StartsWith prefix && path.EndsWith suf then
        path.Substring(prefix.Length, path.Length - prefix.Length - suf.Length)
    else ""

let formatDagText (rt: CoordinatorRuntime) : string =
    Wanxiangzhen.Kernel.Dag.formatDag rt.Dag

let rec private resolveBranchName (rt: CoordinatorRuntime) (taskId: string) (attempts: int) : string =
    let candidate = taskId
    if attempts <= 0 then candidate
    elif rt.Deps.ShowRefExists rt.ProjectRoot candidate then
        let suffix = (generateTaskId ()).Substring 6
        resolveBranchName rt (taskId + "-" + suffix) (attempts - 1)
    else candidate

let private startTask (rt: CoordinatorRuntime) (taskId: string) : JS.Promise<unit> =
    promise {
        match findTask taskId rt.Dag with
        | None -> return ()
        | Some task ->
            let parent =
                let lastSlash = rt.ProjectRoot.LastIndexOf '/'
                rt.ProjectRoot.Substring(0, lastSlash + 1)
            let branchName = resolveBranchName rt taskId 5
            let wtPath = parent + "worktree-" + branchName
            match rt.Deps.TryWorktreeAdd rt.ProjectRoot branchName wtPath rt.MasterBranch with
            | Error e ->
                injectEventFire rt (TaskError (rt.Dag.SessionId, taskId, e))
                return ()
            | Ok _ ->
                rt.Deps.CreateSymlinks wtPath rt.ProjectRoot rt.Config.SharedDirs
                let vibeFs = rt.Deps.DetectVibeFs rt.ProjectRoot
                let prompt = buildSlavePrompt taskId task.Title task.Description rt.MasterBranch vibeFs
                let slaveEnv = createObj []
                assignInto slaveEnv (get nodeProcess "env") |> ignore
                setKey slaveEnv "SQUAD_COORDINATOR_URL" (box rt.CoordinatorUrl)
                setKey slaveEnv "SQUAD_TASK_ID" (box taskId)
                setKey slaveEnv "SQUAD_WORKTREE_PATH" (box wtPath)
                setKey slaveEnv "SQUAD_MASTER_BRANCH" (box rt.MasterBranch)
                setKey slaveEnv "SQUAD_TOKEN" (box rt.Token)
                if vibeFs then setKey slaveEnv "SQUAD_VIBEFS" (box "1")
                rt.Deps.SpawnSlave rt.Config.Terminal wtPath slaveEnv prompt
                let now = rt.Deps.Now ()
                rt.Dag <- rt.Dag |> updateTask taskId (fun t ->
                    { (withStatus t Running now) with
                         WorktreePath = Some wtPath
                         BranchName = Some branchName })
                injectEventFire rt (TaskStarted (rt.Dag.SessionId, taskId, wtPath, branchName))
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
            let now = rt.Deps.Now ()
            rt.Dag <- rt.Dag |> updateTask taskId (fun t -> withStatus t Done now)
            injectEventFire rt (TaskDone (rt.Dag.SessionId, taskId, false))
            cleanupTask rt task
            do! schedulerTick rt
    }

let handleSubmit (rt: CoordinatorRuntime) (taskId: string) (reportedSha: string) : JS.Promise<HttpResponse> =
    match findTask taskId rt.Dag with
    | None ->
        Promise.lift { StatusCode = 404; Body = encodeResult "task_not_found" }
    | Some task when task.Status <> Running ->
        Promise.lift { StatusCode = 200
                       Body = encodeFfResponseBody (NotSubmittable (statusToString task.Status)) }
    | Some task ->
        let branchName = task.BranchName |> Option.defaultValue taskId
        promise {
            let now = rt.Deps.Now ()
            rt.Dag <- rt.Dag |> updateTask taskId (fun t -> withStatus t Submitted now)
            injectEventFire rt (TaskSubmitted (rt.Dag.SessionId, taskId, reportedSha))
            let! result =
                rt.GitQueue.Enqueue(fun () ->
                    promise {
                        let branchSha = rt.Deps.RevParseRef rt.ProjectRoot branchName
                        if branchSha <> reportedSha then return StaleCommit
                        else
                            let cur = rt.Deps.RevParseBranch rt.ProjectRoot
                            if cur <> rt.MasterBranch then
                                return CoordinatorNotReady "not_on_master"
                            elif not (rt.Deps.StatusIsClean rt.ProjectRoot) then
                                return CoordinatorNotReady "dirty"
                            elif rt.Deps.MergeBaseIsAncestor rt.ProjectRoot rt.MasterBranch branchName then
                                let sha = rt.Deps.MergeFfOnly rt.ProjectRoot branchName
                                return Merged sha
                            else
                                let sha = rt.Deps.RevParseRef rt.ProjectRoot rt.MasterBranch
                                return RebaseNeeded sha })
            match result with
            | Merged sha ->
                let n2 = rt.Deps.Now ()
                rt.Dag <- rt.Dag |> updateTask taskId (fun t ->
                    { (withStatus t TaskStatus.Merged n2) with MergedSha = Some sha })
                injectEventFire rt (TaskMerged (rt.Dag.SessionId, taskId, sha))
                match findTask taskId rt.Dag with
                | Some t ->
                    match t.SlavePid with
                    | Some pid ->
                        rt.Deps.KillPid pid (box "SIGTERM")
                        do! rt.Deps.WaitForPidDeath pid 5
                    | None -> ()
                    cleanupTask rt t
                | None -> ()
                do! schedulerTick rt
            | _ ->
                let n2 = rt.Deps.Now ()
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
        Some (rt.Deps.StartPolling 2000 (fun () ->
            promise {
                let toCheck =
                    rt.Dag.Tasks |> Map.toList |> List.map snd
                    |> List.filter (fun t ->
                        (t.Status = Running || t.Status = Submitted) && t.SlavePid.IsSome)
                for t in toCheck do
                    match t.SlavePid with
                    | Some pid when not (rt.Deps.IsPidAlive pid) -> do! handleSlaveExit rt t.Id
                    | Some pid when rt.Deps.IsPidAlive pid ->
                        let now = rt.Deps.Now ()
                        rt.Dag <- rt.Dag |> updateTask t.Id (fun x -> { x with LastHeartbeatAt = Some now })
                    | _ -> ()
            } |> Promise.start))

