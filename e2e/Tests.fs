module Wanxiangzhen.E2eTests

open Fable.Core
open Fable.Core.JsInterop
open Fable.Core.JS
open Wanxiangzhen.Tests.Assert
open Wanxiangzhen.Tests.TestDoubles
open Wanxiangzhen.Kernel.Dag
open Wanxiangzhen.Kernel.Task
open Wanxiangzhen.Shell.Dyn
open Wanxiangzhen.Shell.CoordinatorRuntime

// ── harness import ──────────────────────────────────────────────────────────
[<Import("start", "./harness.js")>]
let start: obj -> JS.Promise<obj> = jsNative

let inline private hasError (o: obj) : bool =
    not (isNullish (get o "error"))

// ── types ───────────────────────────────────────────────────────────────────
type Harness =
    abstract mode: string
    abstract hooks: obj
    abstract runtime: obj
    abstract tmpDir: string
    abstract token: string
    abstract url: string
    abstract runCommand: string -> string -> string -> JS.Promise<obj>
    abstract toolRound: string -> obj -> JS.Promise<string>
    abstract coordinatorGet: string -> string -> JS.Promise<obj>
    abstract coordinatorPost: string -> obj -> string -> JS.Promise<obj>
    abstract readMeta: unit -> string
    abstract waitForMeta: unit -> JS.Promise<string>
    abstract waitForScheduler: string -> unit -> JS.Promise<unit>
    abstract ensureSchedulerCapacity: unit -> JS.Promise<unit>
    abstract getLog: unit -> obj
    abstract getSquadEvents: unit -> obj
    abstract getPromptCalls: unit -> obj
    abstract getSpawnCalls: unit -> obj
    abstract getKillCalls: unit -> obj
    abstract getWorktreeAddCalls: unit -> obj
    abstract getWorktreeRemoveCalls: unit -> obj
    abstract getBranchDeleteCalls: unit -> obj
    abstract clearCallSpies: unit -> unit
    abstract setRevParseRef: string -> string -> unit
    abstract setMergeBaseResult: bool -> unit
    abstract setMergeFfResult: string -> unit
    abstract setStatusClean: bool -> unit
    abstract setHasCommits: bool -> unit
    abstract setShowRefExists: bool -> unit
    abstract setIsPidAlive: bool -> unit
    abstract callSlavePlugin: obj -> string -> string -> string -> string -> string -> JS.Promise<obj>
    abstract dispose: unit -> JS.Promise<unit>

let private harnessFromObj (o: obj) : Harness = unbox o
let private emptyObj = createObj []

let private partsToList (parts: obj) : obj list =
    if isNullish parts then []
    else
        let e = parts :?> System.Collections.Generic.IEnumerable<obj>
        Seq.toList e


let private runWithFreshHarness (label: string) (body: Harness -> JS.Promise<unit>) : JS.Promise<unit> =
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

let private waitForSubmitResult (harness: Harness) (taskId: string) : JS.Promise<bool> =
    promise {
        let! resp = harness.coordinatorPost ($"/task/{taskId}/submit") (createObj [ "commitSha", box "deadbeef" ]) ""
        chk "e2e.regsub.submit_200" (unbox<int>(get resp "status") = 200)
        return (str (get resp "body") "result") = "merged"
    }

// ════════════════════════════════════════════════════════════════════════════
// Test 1 — plugin config commands: config hook registers squad / squad-kill / squad-status
// ════════════════════════════════════════════════════════════════════════════
let testPluginConfigCommands (harness: Harness) : JS.Promise<unit> =
    promise {
        let hooks = harness.hooks
        let cfgHook = get hooks "config"
        let cfg = createObj [ "command", box (createObj []) ]
        do! unbox<JS.Promise<unit>> (cfgHook $ (cfg))
        let cmds = get cfg "command"
        chk "e2e.plugin_commands.squad"     (not (isNullish (get cmds "squad")))
        chk "e2e.plugin_commands.squad_kill" (not (isNullish (get cmds "squad-kill")))
        chk "e2e.plugin_commands.squad_status" (not (isNullish (get cmds "squad-status")))
        let squadCmd = get cmds "squad"
        chk "e2e.plugin_commands.squad.template" ((str squadCmd "template") <> "")
        chk "e2e.plugin_commands.squad.description" ((str squadCmd "description") <> "")
    }

// ════════════════════════════════════════════════════════════════════════════
// Test 2 — /squad creates ndjson event: running /squad writes squad_created to event log
// ════════════════════════════════════════════════════════════════════════════
let testSquadCreatesEvent (harness: Harness) : JS.Promise<unit> =
    promise {
        let! parts = harness.runCommand "squad" "sess-e2e-001" "add remember-me"
        let parts = partsToList parts
        chk "e2e.squad_creates_event.parts_count" (parts.Length = 1)
        let text = str parts.[0] "text"
        chk "e2e.squad_creates_event.contains_squad_created" (text.Contains "squad_event: squad_created")
        chk "e2e.squad_creates_event.contains_requirement" (text.Contains "add remember-me")
    }

// ════════════════════════════════════════════════════════════════════════════
// Test 3 — squad-update-no-session: calling squad_update without /squad first returns error
// ════════════════════════════════════════════════════════════════════════════
let testSquadUpdateNoSession (harness: Harness) : JS.Promise<unit> =
    promise {
        // fresh start — no /squad first
        let taskObj = createObj [
            "taskId", box "squad-orphan"
            "title", box "Orphan"
            "description", box "no session"
            "dependsOn", box [||]
        ]
        let evtObj = createObj [
            "type", box "tasks_created"
            "tasks", box [| taskObj |]
        ]
        let args = createObj [ "events", box [| evtObj |] ]
        let! result = harness.toolRound "squad_update" args
        // without a master session, squad_update should return error for no active squad
        chk "e2e.squad_update_no_session.no_active_squad" (result.Contains "no active squad")
    }

// ════════════════════════════════════════════════════════════════════════════
// Test 4 — HTTP unauthorized: GET /state with explicit bad token returns 401
// ════════════════════════════════════════════════════════════════════════════
let testHttpUnauthorized (harness: Harness) : JS.Promise<unit> =
    promise {
        // use explicit bad token → unauthorized
        let! resp = harness.coordinatorGet "/state" "__NO_AUTH__"
        let status = unbox<int>(get resp "status")
        chk "e2e.http_unauthorized.status_401" (status = 401)
        let body = get resp "body"
        chk "e2e.http_unauthorized.body" ((str body "result") = "unauthorized")
    }

// ════════════════════════════════════════════════════════════════════════════
// Test 5 — full flow: /squad → squad_update → HTTP state shows task
// ════════════════════════════════════════════════════════════════════════════
let testFullFlow (harness: Harness) : JS.Promise<unit> =
    promise {
        // ① /squad
        let! _ = harness.runCommand "squad" "sess-e2e-002" "full flow test"

        // ② squad_update with one task
        let taskObj = createObj [
            "taskId", box "squad-ff-01"
            "title", box "FullFlow"
            "description", box "test"
            "dependsOn", box [||]
        ]
        let evtObj = createObj [
            "type", box "tasks_created"
            "tasks", box [| taskObj |]
        ]
        let args = createObj [ "events", box [| evtObj |] ]
        let! updateResult = harness.toolRound "squad_update" args
        chk "e2e.full_flow.update_ok" (updateResult.Contains "created")

        // ③ GET /state authorized — should show one task
        let! resp = harness.coordinatorGet "/state" ""
        let status = unbox<int>(get resp "status")
        chk "e2e.full_flow.state_200" (status = 200)
    }

// ════════════════════════════════════════════════════════════════════════════
// Test 6 — /squad-status command: shows current DAG
// ════════════════════════════════════════════════════════════════════════════
let testSquadStatusCommand (harness: Harness) : JS.Promise<unit> =
    promise {
        // ① /squad
        let! _ = harness.runCommand "squad" "sess-e2e-003" "status test"

        // ② /squad-status
        let! parts = harness.runCommand "squad-status" "" ""
        let parts = partsToList parts
        chk "e2e.squad_status.has_output" (parts.Length > 0)
        let text = str parts.[0] "text"
        chk "e2e.squad_status.contains_session" (text.Contains "squad-session-")
    }

// ════════════════════════════════════════════════════════════════════════════
// Test 7 — squad_update with one task: task appears in DAG
// ════════════════════════════════════════════════════════════════════════════
let testSquadUpdateOneTask (harness: Harness) : JS.Promise<unit> =
    promise {
        // ① /squad
        let! _ = harness.runCommand "squad" "sess-e2e-004" "one task test"

        // ② squad_update with one task
        let taskObj = createObj [
            "taskId", box "squad-single-01"
            "title", box "Single"
            "description", box "one task"
            "dependsOn", box [||]
        ]
        let evtObj = createObj [
            "type", box "tasks_created"
            "tasks", box [| taskObj |]
        ]
        let args = createObj [ "events", box [| evtObj |] ]
        let! result = harness.toolRound "squad_update" args
        chk "e2e.squad_update_one_task.created" (result.Contains "created")
        chk "e2e.squad_update_one_task.count_1" (result.Contains "1")
    }

// ════════════════════════════════════════════════════════════════════════════
// Test 8 — HTTP register + submit merged flow
// ════════════════════════════════════════════════════════════════════════════
let testHttpRegisterAndSubmitMerged (harness: Harness) : JS.Promise<unit> =
    promise {
        // ① /squad
        let! _ = harness.runCommand "squad" "sess-regsub" "register+submit test"

        // ② squad_update creates task
        let taskObj = createObj [
            "taskId", box "squad-regsub-01"
            "title", box "RegSub"
            "description", box "test register+submit"
            "dependsOn", box [||]
        ]
        let evtObj = createObj [
            "type", box "tasks_created"
            "tasks", box [| taskObj |]
        ]
        let args = createObj [ "events", box [| evtObj |] ]
        let! _ = harness.toolRound "squad_update" args

        // ③ wait for scheduler to start the task
        do! harness.waitForScheduler "squad-regsub-01" ()

        // verify task Running + capture actual branch name (may differ from taskId due to resolveBranchName collision suffix)
        let rt = harness.runtime :?> CoordinatorRuntime
        match findTask "squad-regsub-01" rt.Dag with
        | Some t -> chk "e2e.regsub.is_running" (t.Status = Running)
        | None -> chk "e2e.regsub.task_exists" false

        // ④ POST /register with fake pid
        let! regResp = harness.coordinatorPost "/task/squad-regsub-01/register" (createObj [ "pid", box 12345 ]) ""
        chk "e2e.regsub.register_200" (unbox<int>(get regResp "status") = 200)
        chk "e2e.regsub.registered" ((str (get regResp "body") "result") = "registered")

        // ⑤ configure git stubs for ff merge — must use task.BranchName (the key handleSubmit passes to RevParseRef), not taskId
        let branchName =
            match findTask "squad-regsub-01" rt.Dag with
            | Some t -> t.BranchName |> Option.defaultValue "squad-regsub-01"
            | None -> "squad-regsub-01"
        harness.setRevParseRef branchName "deadbeef"
        harness.setMergeBaseResult true
        harness.setMergeFfResult "merged-sha"

        // capture spy counts before submit
        let wtBefore = (harness.getWorktreeRemoveCalls () :?> obj array).Length
        let brBefore = (harness.getBranchDeleteCalls () :?> obj array).Length

        // ⑥ wait for submit result to be merged
        let! merged = waitForSubmitResult harness "squad-regsub-01"
        chk "e2e.regsub.merged" merged

        // ⑦ verify cleanup calls (delta = 1)
        let wtRm = harness.getWorktreeRemoveCalls () :?> obj array
        let brDl = harness.getBranchDeleteCalls () :?> obj array
        chk "e2e.regsub.worktree_cleaned" (wtRm.Length - wtBefore = 1)
        chk "e2e.regsub.branch_cleaned" (brDl.Length - brBefore = 1)
    }

// ════════════════════════════════════════════════════════════════════════════
// Test 9 — /squad-kill cancels running task
// ════════════════════════════════════════════════════════════════════════════
let testSquadKill (harness: Harness) : JS.Promise<unit> =
    promise {
        harness.clearCallSpies ()

        // ① /squad
        let! _ = harness.runCommand "squad" "sess-kill" "kill test"

        // ② create a task
        let taskObj = createObj [
            "taskId", box "squad-kill-01"
            "title", box "KillMe"
            "description", box "test kill"
            "dependsOn", box [||]
        ]
        let evtObj = createObj [
            "type", box "tasks_created"
            "tasks", box [| taskObj |]
        ]
        let args = createObj [ "events", box [| evtObj |] ]
        let! _ = harness.toolRound "squad_update" args

        // ③ wait for scheduler to start the task
        do! harness.waitForScheduler "squad-kill-01" ()

        // ④ register a pid
        let! _ = harness.coordinatorPost "/task/squad-kill-01/register" (createObj [ "pid", box 98765 ]) ""

        // capture baseline before kill — no task cleanup should happen on kill alone
        let wtBefore = (harness.getWorktreeRemoveCalls () :?> obj array).Length
        let brBefore = (harness.getBranchDeleteCalls () :?> obj array).Length

        // ⑤ /squad-kill
        let! _ = harness.runCommand "squad-kill" "" ""

        // ⑥ verify KillPid was called
        let kills = harness.getKillCalls () :?> obj array
        chk "e2e.squad_kill.kill_called" (kills.Length > 0)

        // ⑦ verify no worktree/branch cleanup (kill doesn't clean up)
        let wtRm = harness.getWorktreeRemoveCalls () :?> obj array
        let brDl = harness.getBranchDeleteCalls () :?> obj array
        chk "e2e.squad_kill.no_wt_cleanup" (wtRm.Length = wtBefore)
        chk "e2e.squad_kill.no_br_cleanup" (brDl.Length = brBefore)
    }

// ════════════════════════════════════════════════════════════════════════════
// Test 10 — DAG cycle rejected
// ════════════════════════════════════════════════════════════════════════════
let testCycleRejected (harness: Harness) : JS.Promise<unit> =
    promise {
        let! _ = harness.runCommand "squad" "sess-cycle" "cycle test"

        let taskA = createObj [
            "taskId", box "squad-cycle-a"
            "title", box "A"
            "description", box "a"
            "dependsOn", box [| "squad-cycle-b" |]
        ]
        let taskB = createObj [
            "taskId", box "squad-cycle-b"
            "title", box "B"
            "description", box "b"
            "dependsOn", box [| "squad-cycle-a" |]
        ]
        let evtObj = createObj [
            "type", box "tasks_created"
            "tasks", box [| taskA; taskB |]
        ]
        let args = createObj [ "events", box [| evtObj |] ]
        let! result = harness.toolRound "squad_update" args
        chk "e2e.cycle_rejected.contains_cycle" (result.Contains "cycle")
    }

// ════════════════════════════════════════════════════════════════════════════
// Test 11 — Dangling dependency rejected
// ════════════════════════════════════════════════════════════════════════════
let testDanglingDepRejected (harness: Harness) : JS.Promise<unit> =
    promise {
        let! _ = harness.runCommand "squad" "sess-dangling" "dangling test"

        let taskObj = createObj [
            "taskId", box "squad-dangling-a"
            "title", box "A"
            "description", box "a"
            "dependsOn", box [| "squad-nonexistent" |]
        ]
        let evtObj = createObj [
            "type", box "tasks_created"
            "tasks", box [| taskObj |]
        ]
        let args = createObj [ "events", box [| evtObj |] ]
        let! result = harness.toolRound "squad_update" args
        chk "e2e.dangling_dep.contains_missing" (result.Contains "squad-nonexistent")
    }

// ════════════════════════════════════════════════════════════════════════════
// Test 12 — GET /task 404 for unknown task
// ════════════════════════════════════════════════════════════════════════════
let testGetTask404 (harness: Harness) : JS.Promise<unit> =
    promise {
        let! resp = harness.coordinatorGet "/task/squad-nonexistent" ""
        chk "e2e.get_task_404.status" (unbox<int>(get resp "status") = 404)
        chk "e2e.get_task_404.body" ((str (get resp "body") "result") = "task_not_found")
    }

// ════════════════════════════════════════════════════════════════════════════
// Test 13 — slave query via env SQUAD_* after in-process coordinator
// ════════════════════════════════════════════════════════════════════════════
let testSlaveQueryViaEnv (harness: Harness) : JS.Promise<unit> =
    promise {
        // ① /squad
        let! _ = harness.runCommand "squad" "sess-slave" "slave e2e"

        // ② create a task
        let taskObj = createObj [
            "taskId", box "squad-slave-01"
            "title", box "SlaveTask"
            "description", box "slave e2e"
            "dependsOn", box [||]
        ]
        let evtObj = createObj [
            "type", box "tasks_created"
            "tasks", box [| taskObj |]
        ]
        let args = createObj [ "events", box [| evtObj |] ]
        let! _ = harness.toolRound "squad_update" args

        // ③ wait for scheduler to start the task
        do! harness.waitForScheduler "squad-slave-01" ()

        let coordinatorUrl = harness.url
        let coordinatorToken = harness.token
        let taskId = "squad-slave-01"
        let worktreePath = "/tmp/wt-slave-01"  // FAKE: matches the spawned task worktree path
        let masterBranch = "main"

        // ④ set SQUAD_* env → triggers slave mode in plugin()
        // use harness.callSlavePlugin which sets env then calls plugin slave mode
        let slaveCtx = createObj [
            "client", box (createObj [])
            "directory", box worktreePath
            "worktree", box worktreePath
        ]
        let! slaveHooksResult = harness.callSlavePlugin slaveCtx coordinatorUrl taskId worktreePath masterBranch coordinatorToken
        let slaveHooks = unbox<obj> slaveHooksResult
        let tools = get slaveHooks "tool"

        // ⑤ hooks must contain both slave tools
        chk "e2e.slave_query.has_submit" (not (isNullish (get tools "submit_to_squad")))
        chk "e2e.slave_query.has_query" (not (isNullish (get tools "query_squad")))

        // ⑥ execute query_squad "state" — hits coordinator HTTP server
        let qsTool = get tools "query_squad"
        let qsExecute = get qsTool "execute"
        let qsExecFn = unbox<System.Func<obj, obj, JS.Promise<string>>> qsExecute
        let qsArgs = createObj [ "query", box "state" ]
        let! qsResp = qsExecFn.Invoke(qsArgs, createObj [])

        chk "e2e.slave_query.response_contains_task" (qsResp.Contains "squad-slave-01")
    }

// ════════════════════════════════════════════════════════════════════════════
// Test 14 — waitForScheduler pattern: poll until task transitions to Running
// ════════════════════════════════════════════════════════════════════════════
let testWaitForSchedulerPattern (harness: Harness) : JS.Promise<unit> =
    promise {
        // ensure scheduler has capacity (prior squads cleared)
        do! harness.ensureSchedulerCapacity ()

        // ① /squad
        let! _ = harness.runCommand "squad" "sess-wait" "wait test"

        let taskObj = createObj [
            "taskId", box "squad-wait-01"
            "title", box "Wait"
            "description", box "wait for scheduler"
            "dependsOn", box [||]
        ]
        let evtObj = createObj [
            "type", box "tasks_created"
            "tasks", box [| taskObj |]
        ]
        let args = createObj [ "events", box [| evtObj |] ]
        let! _ = harness.toolRound "squad_update" args

        // waitForScheduler polls until task.Status = Running
        do! harness.waitForScheduler "squad-wait-01" ()

        // verify task is Running via typed DAG access
        let rt = harness.runtime :?> CoordinatorRuntime
        match findTask "squad-wait-01" rt.Dag with
        | Some t -> chk "e2e.wait_for_scheduler.is_running" (t.Status = Running)
        | None -> chk "e2e.wait_for_scheduler.task_exists" false
    }

// ════════════════════════════════════════════════════════════════════════════
// runAll — entry point used by runner.js --e2e
// ════════════════════════════════════════════════════════════════════════════
let runAll (_args: string array) : JS.Promise<int> =
    promise {
        clearFailuresForRun ()
        // run merged task test isolated to avoid state leaking into shared harness
        do! runWithFreshHarness "e2e.http_register_and_submit_merged" testHttpRegisterAndSubmitMerged

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
                // isolated above; not in shared list
                ("e2e.squad_kill", testSquadKill harness)
                ("e2e.cycle_rejected", testCycleRejected harness)
                ("e2e.dangling_dep_rejected", testDanglingDepRejected harness)
                ("e2e.get_task_404", testGetTask404 harness)
                ("e2e.slave_query_via_env", testSlaveQueryViaEnv harness)
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

[<Global>]
let private console : obj = jsNative
