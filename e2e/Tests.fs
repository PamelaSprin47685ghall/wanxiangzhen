module Wanxiangzhen.E2eTests

open Fable.Core
open Fable.Core.JsInterop
open Wanxiangzhen.Tests.Assert
open Wanxiangzhen.E2eTests.HarnessHelpers
open Wanxiangzhen.E2eTests.IsolatedTests
open Wanxiangzhen.E2eTests.IsolatedTestsB
open Wanxiangzhen.Kernel.Dag
open Wanxiangzhen.Kernel.Task
open Wanxiangzhen.Shell.CoordinatorRuntime
open Wanxiangzhen.Shell.Dyn

[<Import("start", "./harness.js")>]
let start: obj -> Fable.Core.JS.Promise<obj> = jsNative

let inline private hasError (o: obj) : bool =
    not (isNullish (get o "error"))

let private runWithFreshHarness (label: string) (body: Harness -> Fable.Core.JS.Promise<unit>) : Fable.Core.JS.Promise<unit> =
    promise {
        let! apiObj = start (createObj [ "inProcess", box true ])
        if hasError apiObj then
            let err = str apiObj "error"
            let stack = if isNullish (get apiObj "stack") then "" else str apiObj "stack"
            recordException (sprintf "HARNESS_START_FAILED for %s: %s\n%s" label err stack)
        else
            let harness = harnessFromObj apiObj
            try
                setCurrentLabel label
                do! body harness
            with ex -> recordException (sprintf "EXCEPTION in %s: %s" label (string ex))
            do! harness.dispose ()
    }

let private runWithCustomMasterBranch (label: string) (body: Harness -> Fable.Core.JS.Promise<unit>) : Fable.Core.JS.Promise<unit> =
    promise {
        let! apiObj = start (createObj [ "inProcess", box true; "masterBranch", box "custom-main" ])
        if hasError apiObj then
            let err = str apiObj "error"
            let stack = if isNullish (get apiObj "stack") then "" else str apiObj "stack"
            recordException (sprintf "HARNESS_START_FAILED for %s: %s\n%s" label err stack)
        else
            let harness = harnessFromObj apiObj
            try
                setCurrentLabel label
                do! body harness
            with ex -> recordException (sprintf "EXCEPTION in %s: %s" label (string ex))
            do! harness.dispose ()
    }

// ── Shared harness tests ───────────────────────────────────────────────────

let testPluginConfigCommands (harness: Harness) : Fable.Core.JS.Promise<unit> =
    promise {
        let hooks = harness.hooks
        let cfgHook = get hooks "config"
        let cfg = createObj [ "command", box (createObj []) ]
        do! unbox<Fable.Core.JS.Promise<unit>> (cfgHook $ (cfg))
        let cmds = get cfg "command"
        chk "e2e.plugin_commands.squad"     (not (isNullish (get cmds "squad")))
        chk "e2e.plugin_commands.squad_kill" (not (isNullish (get cmds "squad-kill")))
        chk "e2e.plugin_commands.squad_status" (not (isNullish (get cmds "squad-status")))
        let squadCmd = get cmds "squad"
        chk "e2e.plugin_commands.squad.template" ((str squadCmd "template") <> "")
        chk "e2e.plugin_commands.squad.description" ((str squadCmd "description") <> "")
    }

let testSquadCreatesEvent (harness: Harness) : Fable.Core.JS.Promise<unit> =
    promise {
        let! parts = harness.runCommand "squad" "sess-e2e-001" "add remember-me"
        let parts = partsToList parts
        chk "e2e.squad_creates_event.parts_count" (parts.Length = 1)
        let text = str parts.[0] "text"
        chk "e2e.squad_creates_event.contains_squad_created" (text.Contains "squad_event: squad_created")
        chk "e2e.squad_creates_event.contains_requirement" (text.Contains "add remember-me")
    }

let testSquadUpdateNoSession (harness: Harness) : Fable.Core.JS.Promise<unit> =
    promise {
        let taskObj = mkTask "squad-orphan" "Orphan" "no session" [||]
        let evtObj = mkTasksCreated [| taskObj |]
        let args = mkUpdateArgs [| evtObj |]
        let! result = harness.toolRound "squad_update" args
        chk "e2e.squad_update_no_session.no_active_squad" (result.Contains "no active squad")
    }

let testHttpUnauthorized (harness: Harness) : Fable.Core.JS.Promise<unit> =
    promise {
        let! resp = harness.coordinatorGet "/state" "__NO_AUTH__"
        let status = unbox<int>(get resp "status")
        chk "e2e.http_unauthorized.status_401" (status = 401)
        let body = get resp "body"
        chk "e2e.http_unauthorized.body" ((str body "result") = "unauthorized")
    }

let testFullFlow (harness: Harness) : Fable.Core.JS.Promise<unit> =
    promise {
        let! _ = harness.runCommand "squad" "sess-e2e-002" "full flow test"
        let taskObj = mkTask "squad-ff-01" "FullFlow" "test" [||]
        let evtObj = mkTasksCreated [| taskObj |]
        let args = mkUpdateArgs [| evtObj |]
        let! updateResult = harness.toolRound "squad_update" args
        chk "e2e.full_flow.update_ok" (updateResult.Contains "created")
        let! resp = harness.coordinatorGet "/state" ""
        let status = unbox<int>(get resp "status")
        chk "e2e.full_flow.state_200" (status = 200)
    }

let testSquadStatusCommand (harness: Harness) : Fable.Core.JS.Promise<unit> =
    promise {
        let! _ = harness.runCommand "squad" "sess-e2e-003" "status test"
        let! parts = harness.runCommand "squad-status" "" ""
        let parts = partsToList parts
        chk "e2e.squad_status.has_output" (parts.Length > 0)
        let text = str parts.[0] "text"
        chk "e2e.squad_status.contains_session" (text.Contains "squad-session-")
        chk "e2e.squad_status.contains_task_title" (text.Contains "status test")
    }

let testSquadUpdateOneTask (harness: Harness) : Fable.Core.JS.Promise<unit> =
    promise {
        let! _ = harness.runCommand "squad" "sess-e2e-004" "one task test"
        let taskObj = mkTask "squad-single-01" "Single" "one task" [||]
        let evtObj = mkTasksCreated [| taskObj |]
        let args = mkUpdateArgs [| evtObj |]
        let! result = harness.toolRound "squad_update" args
        chk "e2e.squad_update_one_task.created" (result.Contains "created")
        chk "e2e.squad_update_one_task.count_1" (result.Contains "1")
    }

let testSquadKill (harness: Harness) : Fable.Core.JS.Promise<unit> =
    promise {
        harness.clearCallSpies ()
        let! _ = harness.runCommand "squad" "sess-kill" "kill test"
        let taskObj = mkTask "squad-kill-01" "KillMe" "test kill" [||]
        let evtObj = mkTasksCreated [| taskObj |]
        let args = mkUpdateArgs [| evtObj |]
        let! _ = harness.toolRound "squad_update" args
        do! harness.waitForScheduler "squad-kill-01" ()
        let! _ = harness.coordinatorPost "/task/squad-kill-01/register" (createObj [ "pid", box 98765 ]) ""
        let wtBefore = (harness.getWorktreeRemoveCalls () :?> obj array).Length
        let brBefore = (harness.getBranchDeleteCalls () :?> obj array).Length
        let! _ = harness.runCommand "squad-kill" "" ""
        let kills = harness.getKillCalls () :?> obj array
        chk "e2e.squad_kill.kill_called" (kills.Length > 0)
        let wtRm = harness.getWorktreeRemoveCalls () :?> obj array
        let brDl = harness.getBranchDeleteCalls () :?> obj array
        chk "e2e.squad_kill.no_wt_cleanup" (wtRm.Length = wtBefore)
        chk "e2e.squad_kill.no_br_cleanup" (brDl.Length = brBefore)
    }

let testCycleRejected (harness: Harness) : Fable.Core.JS.Promise<unit> =
    promise {
        let! _ = harness.runCommand "squad" "sess-cycle" "cycle test"
        let taskA = mkTask "squad-cycle-a" "A" "a" [| "squad-cycle-b" |]
        let taskB = mkTask "squad-cycle-b" "B" "b" [| "squad-cycle-a" |]
        let evtObj = mkTasksCreated [| taskA; taskB |]
        let args = mkUpdateArgs [| evtObj |]
        let! result = harness.toolRound "squad_update" args
        chk "e2e.cycle_rejected.contains_cycle" (result.Contains "cycle")
    }

let testDanglingDepRejected (harness: Harness) : Fable.Core.JS.Promise<unit> =
    promise {
        let! _ = harness.runCommand "squad" "sess-dangling" "dangling test"
        let taskObj = mkTask "squad-dangling-a" "A" "a" [| "squad-nonexistent" |]
        let evtObj = mkTasksCreated [| taskObj |]
        let args = mkUpdateArgs [| evtObj |]
        let! result = harness.toolRound "squad_update" args
        chk "e2e.dangling_dep.contains_missing" (result.Contains "squad-nonexistent")
    }

let testGetTask404 (harness: Harness) : Fable.Core.JS.Promise<unit> =
    promise {
        let! resp = harness.coordinatorGet "/task/squad-nonexistent" ""
        chk "e2e.get_task_404.status" (unbox<int>(get resp "status") = 404)
        chk "e2e.get_task_404.body" ((str (get resp "body") "result") = "task_not_found")
    }

let testWaitForSchedulerPattern (harness: Harness) : Fable.Core.JS.Promise<unit> =
    promise {
        do! harness.ensureSchedulerCapacity ()
        let! _ = harness.runCommand "squad" "sess-wait" "wait test"
        let taskObj = mkTask "squad-wait-01" "Wait" "wait for scheduler" [||]
        let evtObj = mkTasksCreated [| taskObj |]
        let args = mkUpdateArgs [| evtObj |]
        let! _ = harness.toolRound "squad_update" args
        do! harness.waitForScheduler "squad-wait-01" ()
        let rt = harness.runtime :?> CoordinatorRuntime
        match findTask "squad-wait-01" rt.Dag with
        | Some t -> chk "e2e.wait_for_scheduler.is_running" (t.Status = Running)
        | None -> chk "e2e.wait_for_scheduler.task_exists" false
    }

// ── runAll ─────────────────────────────────────────────────────────────────

let runAll (_args: string array) : Fable.Core.JS.Promise<int> =
    promise {
        clearFailuresForRun ()

        do! runWithFreshHarness "e2e.http_register_and_submit_merged" testHttpRegisterAndSubmitMerged
        do! runWithFreshHarness "e2e.http_submit_failures" testHttpSubmitFailures
        do! runWithFreshHarness "e2e.done_beacon" testDoneBeacon
        do! runWithFreshHarness "e2e.query_detail" testQuerySquadTaskDetail
        do! runWithFreshHarness "e2e.replay_and_reconcile" testReplayAndReconcile
        do! runWithFreshHarness "e2e.multi_session_and_kill_specific" testMultiSessionAndKillSpecific
        do! runWithFreshHarness "e2e.max_concurrent_limits_scheduler" testMaxConcurrentLimitsScheduler
        do! runWithFreshHarness "e2e.pid_timeout_to_done" testPidPollingTimeoutToDone
        do! runWithFreshHarness "e2e.slave_query_via_env" testSlaveQueryViaEnv
        do! runWithCustomMasterBranch "e2e.master_branch_frontmatter_override" testMasterBranchFrontmatterOverride

        let! apiObj = start (createObj [ "inProcess", box true ])
        if hasError apiObj then
            let err = str apiObj "error"
            let stack = if isNullish (get apiObj "stack") then "" else str apiObj "stack"
            console?error (sprintf "HARNESS_START_FAILED: %s\n%s" err stack) |> ignore
            return 1
        else
            let harness = harnessFromObj apiObj

            let tests = [
                ("e2e.plugin_config_commands", testPluginConfigCommands harness)
                ("e2e.squad_update_no_session", testSquadUpdateNoSession harness)
                ("e2e.squad_creates_event", testSquadCreatesEvent harness)
                ("e2e.http_unauthorized", testHttpUnauthorized harness)
                ("e2e.full_flow", testFullFlow harness)
                ("e2e.squad_status_command", testSquadStatusCommand harness)
                ("e2e.squad_update_one_task", testSquadUpdateOneTask harness)
                ("e2e.squad_kill", testSquadKill harness)
                ("e2e.cycle_rejected", testCycleRejected harness)
                ("e2e.dangling_dep_rejected", testDanglingDepRejected harness)
                ("e2e.get_task_404", testGetTask404 harness)
                ("e2e.wait_for_scheduler_pattern", testWaitForSchedulerPattern harness)
            ]

            for (label, body) in tests do
                setCurrentLabel label
                try
                    do! body
                with ex -> recordException (sprintf "EXCEPTION in %s: %s" label (string ex))

            do! harness.dispose ()
            return summary ()
    }
