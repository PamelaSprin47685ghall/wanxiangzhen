module Wanxiangzhen.E2eTests.IsolatedTestsB

open Fable.Core
open Fable.Core.JsInterop
open Wanxiangzhen.Tests.Assert
open Wanxiangzhen.E2eTests.HarnessHelpers
open Wanxiangzhen.Kernel.Task
open Wanxiangzhen.Kernel.Dag
open Wanxiangzhen.Shell.CoordinatorRuntime
open Wanxiangzhen.Shell.Dyn

let testQuerySquadTaskDetail (harness: Harness) : Fable.Core.JS.Promise<unit> =
    promise {
        let! _ = harness.runCommand "squad" "sess-query-detail" "query detail test"
        let taskObj = mkTask "squad-qd-01" "QueryDetail" "test query detail" [||]
        let evt = mkTasksCreated [| taskObj |]
        let! _ = harness.toolRound "squad_update" (mkUpdateArgs [| evt |])

        let! respOk = harness.coordinatorGet "/task/squad-qd-01" ""
        chk "e2e.query_detail.ok_status" (unbox<int>(get respOk "status") = 200)
        let bodyOk = get respOk "body"
        chk "e2e.query_detail.id" (str bodyOk "id" = "squad-qd-01")
        chk "e2e.query_detail.title" (str bodyOk "title" = "QueryDetail")

        let! resp404 = harness.coordinatorGet "/task/squad-nonexistent" ""
        chk "e2e.query_detail.404_status" (unbox<int>(get resp404 "status") = 404)
        let body404 = get resp404 "body"
        chk "e2e.query_detail.404_result" (str body404 "result" = "task_not_found")
    }

let testReplayAndReconcile (harness: Harness) : Fable.Core.JS.Promise<unit> =
    promise {
        let! _ = harness.runCommand "squad" "sess-replay" "replay test"
        let taskObj = mkTask "squad-rp-01" "ReplayTask" "test replay" [||]
        let evt = mkTasksCreated [| taskObj |]
        let! _ = harness.toolRound "squad_update" (mkUpdateArgs [| evt |])
        do! harness.waitForScheduler "squad-rp-01" ()

        let rt = harness.runtime :?> CoordinatorRuntime
        let branchName = getBranchName rt "squad-rp-01"

        harness.setMergeBaseResult true
        harness.setRevParseRef "main" "reconciled-sha-123"

        do! Wanxiangzhen.Shell.CoordinatorReplay.replayFromEventLog rt

        match findTask "squad-rp-01" rt.Dag with
        | Some t ->
            chk "e2e.replay.reconciled_to_merged" (t.Status = Merged)
            chk "e2e.replay.reconciled_sha" (t.MergedSha = Some "reconciled-sha-123")
        | None -> chk "e2e.replay.task_reloaded" false
    }

let testMultiSessionAndKillSpecific (harness: Harness) : Fable.Core.JS.Promise<unit> =
    promise {
        let! _ = harness.runCommand "squad" "sess-multi-A" "session A test"
        let rt = harness.runtime :?> CoordinatorRuntime
        let firstSessionId = rt.Dag.SessionId

        let taskA = mkTask "squad-ma-01" "TaskA" "desc A" [||]
        let evtA = mkTasksCreated [| taskA |]
        let! _ = harness.toolRound "squad_update" (mkUpdateArgs [| evtA |])
        do! harness.waitForScheduler "squad-ma-01" ()
        let! _ = harness.coordinatorPost "/task/squad-ma-01/register" (createObj [ "pid", box 55551 ]) ""

        harness.setNowResult "2025-01-01T00:00:01.000Z"
        let! _ = harness.runCommand "squad" "sess-multi-B" "session B test"
        let secondSessionId = rt.Dag.SessionId

        let taskB = mkTask "squad-mb-01" "TaskB" "desc B" [||]
        let evtB = mkTasksCreated [| taskB |]
        let! _ = harness.toolRound "squad_update" (mkUpdateArgs [| evtB |])
        do! harness.waitForScheduler "squad-mb-01" ()
        let! _ = harness.coordinatorPost "/task/squad-mb-01/register" (createObj [ "pid", box 55552 ]) ""

        let! stateResp = harness.coordinatorGet "/state" ""
        chk "e2e.multi.state_200" (unbox<int>(get stateResp "status") = 200)
        let sessions = (get (get stateResp "body") "sessions") :?> obj array
        chk "e2e.multi.sessions_count" (sessions.Length = 2)

        let! _ = harness.runCommand "squad-kill" "" firstSessionId

        match rt.Sessions.TryFind firstSessionId with
        | Some dagA ->
            match findTask "squad-ma-01" dagA with
            | Some t -> chk "e2e.multi.taskA_cancelled" (t.Status = Cancelled)
            | None -> chk "e2e.multi.taskA_exists" false
        | None -> chk "e2e.multi.sessionA_archived" false

        match findTask "squad-mb-01" rt.Dag with
        | Some t -> chk "e2e.multi.taskB_running" (t.Status = Running)
        | None -> chk "e2e.multi.taskB_exists" false

        let checkNdjsonCancelled () =
            promise {
                let ndjsonText = harness.readMeta()
                return ndjsonText.Contains "squad_cancelled"
            }
        let! hasCancelledEvt = spinUntil checkNdjsonCancelled 2000
        chk "e2e.multi.ndjson_cancelled" hasCancelledEvt
    }

let testMaxConcurrentLimitsScheduler (harness: Harness) : Fable.Core.JS.Promise<unit> =
    promise {
        let! _ = harness.runCommand "squad" "sess-concurrent" "concurrent limit test"
        let rt = harness.runtime :?> CoordinatorRuntime
        rt.Config <- { rt.Config with MaxConcurrent = 1 }

        let taskA = mkTask "squad-lim-01" "Task1" "first" [||]
        let taskB = mkTask "squad-lim-02" "Task2" "second" [||]
        let taskC = mkTask "squad-lim-03" "Task3" "third" [| "squad-lim-02" |]
        let evt = mkTasksCreated [| taskA; taskB; taskC |]
        let! _ = harness.toolRound "squad_update" (mkUpdateArgs [| evt |])

        let checkAnyRunning () =
            promise {
                let t1 = findTask "squad-lim-01" rt.Dag
                let t2 = findTask "squad-lim-02" rt.Dag
                match t1, t2 with
                | Some a, Some b when a.Status = Running || b.Status = Running -> return true
                | _ -> return false
            }
        let! anyRunning = spinUntil checkAnyRunning 1000

        chk "e2e.concurrent.one_started" anyRunning

        let t1 = findTask "squad-lim-01" rt.Dag |> Option.get
        let t2 = findTask "squad-lim-02" rt.Dag |> Option.get
        
        let runningId, pendingId =
            if t1.Status = Running then "squad-lim-01", "squad-lim-02"
            else "squad-lim-02", "squad-lim-01"

        let runningTask = if runningId = "squad-lim-01" then t1 else t2
        let pendingTask = if pendingId = "squad-lim-01" then t1 else t2

        chk "e2e.concurrent.running_is_running" (runningTask.Status = Running)
        chk "e2e.concurrent.pending_is_pending" (pendingTask.Status = Pending)

        let! _ = harness.coordinatorPost $"/task/{runningId}/register" (createObj [ "pid", box 66661 ]) ""
        let! _ = harness.coordinatorPost $"/task/{runningId}/done" (createObj []) ""

        let checkSecondRunning () =
            promise {
                match findTask pendingId rt.Dag with
                | Some t when t.Status = Running -> return true
                | _ -> return false
            }
        let! secondRunning = spinUntil checkSecondRunning 1000

        chk "e2e.concurrent.second_started_after_first_done" secondRunning
    }

let testPidPollingTimeoutToDone (harness: Harness) : Fable.Core.JS.Promise<unit> =
    promise {
        let! _ = harness.runCommand "squad" "sess-pid-timeout" "pid timeout test"
        let taskObj = mkTask "squad-timeout-01" "TimeoutTask" "will die" [||]
        let evt = mkTasksCreated [| taskObj |]
        let! _ = harness.toolRound "squad_update" (mkUpdateArgs [| evt |])
        do! harness.waitForScheduler "squad-timeout-01" ()

        let! _ = harness.coordinatorPost "/task/squad-timeout-01/register" (createObj [ "pid", box 77777 ]) ""

        harness.setIsPidAlive false

        let rt = harness.runtime :?> CoordinatorRuntime
        let checkTimeoutDone () =
            promise {
                match findTask "squad-timeout-01" rt.Dag with
                | Some t when t.Status = Done -> return true
                | _ -> return false
            }
        let! converged = spinUntil checkTimeoutDone 3000

        chk "e2e.pid_timeout.is_done" converged
    }

let testMasterBranchFrontmatterOverride (harness: Harness) : Fable.Core.JS.Promise<unit> =
    promise {
        let rt = harness.runtime :?> CoordinatorRuntime
        chk "e2e.config.master_branch_overridden" (rt.MasterBranch = "custom-main")
    }

let testSlaveQueryViaEnv (harness: Harness) : Fable.Core.JS.Promise<unit> =
    promise {
        let! _ = harness.runCommand "squad" "sess-slave" "slave e2e"
        let taskObj = mkTask "squad-slave-01" "SlaveTask" "slave e2e" [||]
        let evtObj = mkTasksCreated [| taskObj |]
        let args = mkUpdateArgs [| evtObj |]
        let! _ = harness.toolRound "squad_update" args
        do! harness.waitForScheduler "squad-slave-01" ()

        let coordinatorUrl = harness.url
        let coordinatorToken = harness.token
        let taskId = "squad-slave-01"
        let worktreePath = harness.tmpDir
        let masterBranch = "main"

        let slaveCtx = createObj [
            "client", box (createObj [])
            "directory", box worktreePath
            "worktree", box worktreePath
        ]
        let! slaveHooksResult = harness.callSlavePlugin slaveCtx coordinatorUrl taskId worktreePath masterBranch coordinatorToken
        let slaveHooks = unbox<obj> slaveHooksResult
        let tools = get slaveHooks "tool"

        chk "e2e.slave_query.has_submit" (not (isNullish (get tools "submit_to_squad")))
        chk "e2e.slave_query.has_query" (not (isNullish (get tools "query_squad")))

        let qsTool = get tools "query_squad"
        let qsExecute = get qsTool "execute"
        let qsExecFn = unbox<System.Func<obj, obj, Fable.Core.JS.Promise<string>>> qsExecute
        let qsArgs = createObj [ "query", box "state" ]
        let! qsResp = qsExecFn.Invoke(qsArgs, createObj [])
        chk "e2e.slave_query.response_contains_task" (qsResp.Contains "squad-slave-01")

        let qsArgsId = createObj [ "query", box "squad-slave-01" ]
        let! qsRespId = qsExecFn.Invoke(qsArgsId, createObj [])
        chk "e2e.slave_query.response_contains_task_detail" (qsRespId.Contains "SlaveTask")

        let submitTool = get tools "submit_to_squad"
        let submitExecute = get submitTool "execute"
        let submitExecFn = unbox<System.Func<obj, obj, Fable.Core.JS.Promise<string>>> submitExecute
        let realHeadSha = Wanxiangzhen.Shell.GitShell.revParseHead harness.tmpDir
        harness.setRevParseRef "squad-slave-01" realHeadSha
        harness.setRevParseRef "main" realHeadSha
        harness.setMergeBaseResult true
        harness.setMergeFfResult "merged-sha"
        let! subToolResp = submitExecFn.Invoke(createObj [], createObj [])
        chk (sprintf "e2e.slave_submit.tool_response_merged (actual: %s)" subToolResp) (subToolResp.Contains "Merged")
    }
