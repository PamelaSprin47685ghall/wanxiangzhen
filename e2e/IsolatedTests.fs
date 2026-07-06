module Wanxiangzhen.E2eTests.IsolatedTests

open Fable.Core
open Fable.Core.JsInterop
open Wanxiangzhen.Tests.Assert
open Wanxiangzhen.E2eTests.HarnessHelpers
open Wanxiangzhen.Kernel.Task
open Wanxiangzhen.Kernel.Dag
open Wanxiangzhen.Shell.CoordinatorRuntime
open Wanxiangzhen.Shell.Dyn

let testHttpRegisterAndSubmitMerged (harness: Harness) : Fable.Core.JS.Promise<unit> =
    promise {
        let! _ = harness.runCommand "squad" "sess-regsub" "register+submit test"
        let taskObj = mkTask "squad-regsub-01" "RegSub" "test register+submit" [||]
        let evt = mkTasksCreated [| taskObj |]
        let! _ = harness.toolRound "squad_update" (mkUpdateArgs [| evt |])
        do! harness.waitForScheduler "squad-regsub-01" ()

        let rt = harness.runtime :?> CoordinatorRuntime
        match findTask "squad-regsub-01" rt.Dag with
        | Some t ->
            chk "e2e.regsub.is_running" (t.Status = Running)
            match t.WorktreePath with
            | Some p -> chk "e2e.regsub.worktree_path_format" (p.Contains "worktree-squad-regsub-01")
            | None -> chk "e2e.regsub.worktree_path_present" false
        | None -> chk "e2e.regsub.task_exists" false

        let spawns = harness.getSpawnCalls() :?> obj array
        chk "e2e.regsub.spawn_count" (spawns.Length > 0)
        let firstSpawn = spawns.[0]
        chk "e2e.regsub.spawn_has_env" (not (isNullish (get firstSpawn "env")))
        chk "e2e.regsub.spawn_correct_task" (str (get firstSpawn "env") "SQUAD_TASK_ID" = "squad-regsub-01")

        let! regResp = harness.coordinatorPost "/task/squad-regsub-01/register" (createObj [ "pid", box 12345 ]) ""
        chk "e2e.regsub.register_200" (unbox<int>(get regResp "status") = 200)

        let branchName = getBranchName rt "squad-regsub-01"
        harness.setRevParseRef branchName "deadbeef"
        harness.setMergeBaseResult true
        harness.setMergeFfResult "merged-sha"

        let wtBefore = (harness.getWorktreeRemoveCalls () :?> obj array).Length
        let brBefore = (harness.getBranchDeleteCalls () :?> obj array).Length

        let! submitResp = harness.coordinatorPost "/task/squad-regsub-01/submit" (createObj [ "commitSha", box "deadbeef" ]) ""
        chk "e2e.regsub.submit_200" (unbox<int>(get submitResp "status") = 200)
        chk "e2e.regsub.merged" (str (get submitResp "body") "result" = "merged")

        let wtRm = harness.getWorktreeRemoveCalls () :?> obj array
        let brDl = harness.getBranchDeleteCalls () :?> obj array
        chk "e2e.regsub.worktree_cleaned" (wtRm.Length - wtBefore = 1)
        chk "e2e.regsub.branch_cleaned" (brDl.Length - brBefore = 1)

        let prompts = harness.getPromptCalls() :?> obj array
        chk "e2e.regsub.has_prompts" (prompts.Length > 0)
        let checkNdjsonMerged () =
            promise {
                let ndjsonText = harness.readMeta()
                return ndjsonText.Contains "task_merged"
            }
        let! hasMergedEvt = spinUntil checkNdjsonMerged 2000
        chk "e2e.regsub.ndjson_contains_merged" hasMergedEvt
    }

let testHttpSubmitFailures (harness: Harness) : Fable.Core.JS.Promise<unit> =
    promise {
        let! _ = harness.runCommand "squad" "sess-subfail" "submit failures test"
        let taskObj = mkTask "squad-subfail-01" "SubFail" "fail cases" [||]
        let evt = mkTasksCreated [| taskObj |]
        let! _ = harness.toolRound "squad_update" (mkUpdateArgs [| evt |])
        do! harness.waitForScheduler "squad-subfail-01" ()

        let! regResp = harness.coordinatorPost "/task/squad-subfail-01/register" (createObj [ "pid", box 33333 ]) ""
        chk "e2e.subfail.register_200" (unbox<int>(get regResp "status") = 200)

        let rt = harness.runtime :?> CoordinatorRuntime
        let branchName = getBranchName rt "squad-subfail-01"

        harness.setRevParseRef branchName "headbeef"
        let! staleResp = harness.coordinatorPost "/task/squad-subfail-01/submit" (createObj [ "commitSha", box "stalebeef" ]) ""
        chk "e2e.subfail.stale_commit_status" (unbox<int>(get staleResp "status") = 200)
        chk "e2e.subfail.stale_commit_result" (str (get staleResp "body") "result" = "stale_commit")

        harness.setStatusClean false
        let! dirtyResp = harness.coordinatorPost "/task/squad-subfail-01/submit" (createObj [ "commitSha", box "headbeef" ]) ""
        chk "e2e.subfail.dirty_status" (unbox<int>(get dirtyResp "status") = 200)
        chk "e2e.subfail.dirty_result" (str (get dirtyResp "body") "result" = "coordinator_not_ready")
        chk "e2e.subfail.dirty_reason" (str (get dirtyResp "body") "reason" = "dirty")

        rt?MasterBranch <- "different-branch"
        let! notOnMasterResp = harness.coordinatorPost "/task/squad-subfail-01/submit" (createObj [ "commitSha", box "headbeef" ]) ""
        chk "e2e.subfail.not_master_status" (unbox<int>(get notOnMasterResp "status") = 200)
        chk "e2e.subfail.not_master_result" (str (get notOnMasterResp "body") "result" = "coordinator_not_ready")
        chk "e2e.subfail.not_master_reason" (str (get notOnMasterResp "body") "reason" = "not_on_master")
        rt?MasterBranch <- "main"

        harness.setStatusClean true
        harness.setMergeBaseResult false
        let! rebaseResp = harness.coordinatorPost "/task/squad-subfail-01/submit" (createObj [ "commitSha", box "headbeef" ]) ""
        chk "e2e.subfail.rebase_status" (unbox<int>(get rebaseResp "status") = 200)
        chk "e2e.subfail.rebase_result" (str (get rebaseResp "body") "result" = "rebase_needed")

        harness.setMergeBaseResult true
        harness.setMergeFfResult "merged-sha-99"
        let! okResp = harness.coordinatorPost "/task/squad-subfail-01/submit" (createObj [ "commitSha", box "headbeef" ]) ""
        chk "e2e.subfail.ok_status" (unbox<int>(get okResp "status") = 200)
        chk "e2e.subfail.ok_result" (str (get okResp "body") "result" = "merged")

        let! finishedResp = harness.coordinatorPost "/task/squad-subfail-01/submit" (createObj [ "commitSha", box "headbeef" ]) ""
        chk "e2e.subfail.finished_status" (unbox<int>(get finishedResp "status") = 200)
        chk "e2e.subfail.finished_result" (str (get finishedResp "body") "result" = "not_submittable")
    }

let testDoneBeacon (harness: Harness) : Fable.Core.JS.Promise<unit> =
    promise {
        let! _ = harness.runCommand "squad" "sess-done-beacon" "done beacon test"
        let taskObj = mkTask "squad-done-01" "DoneTask" "will send done beacon" [||]
        let evt = mkTasksCreated [| taskObj |]
        let! _ = harness.toolRound "squad_update" (mkUpdateArgs [| evt |])
        do! harness.waitForScheduler "squad-done-01" ()

        let! _ = harness.coordinatorPost "/task/squad-done-01/register" (createObj [ "pid", box 44444 ]) ""
        let wtBefore = (harness.getWorktreeRemoveCalls () :?> obj array).Length

        let! doneResp = harness.coordinatorPost "/task/squad-done-01/done" (createObj []) ""
        chk "e2e.done_beacon.status_200" (unbox<int>(get doneResp "status") = 200)
        chk "e2e.done_beacon.result_acknowledged" (str (get doneResp "body") "result" = "acknowledged")

        let rt = harness.runtime :?> CoordinatorRuntime
        let checkDone () =
            promise {
                match findTask "squad-done-01" rt.Dag with
                | Some t when t.Status = Done -> return true
                | _ -> return false
            }
        let! converged = spinUntil checkDone 2000

        chk "e2e.done_beacon.status_is_done" converged
        let wtRm = harness.getWorktreeRemoveCalls () :?> obj array
        chk "e2e.done_beacon.worktree_cleaned" (wtRm.Length - wtBefore = 1)
        let checkNdjsonDone () =
            promise {
                let ndjsonText = harness.readMeta()
                return ndjsonText.Contains "task_done"
            }
        let! hasDoneEvt = spinUntil checkNdjsonDone 2000
        chk "e2e.done_beacon.ndjson_contains_done" hasDoneEvt
    }
