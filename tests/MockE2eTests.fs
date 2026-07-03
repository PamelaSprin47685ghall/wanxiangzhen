module Wanxiangzhen.Tests.MockE2eTests

open Fable.Core
open Fable.Core.JsInterop
open Wanxiangzhen.Plugin
open Wanxiangzhen.Kernel.Task
open Wanxiangzhen.Kernel.Dag
open Wanxiangzhen.Kernel.SquadConfig
open Wanxiangzhen.Shell.Dyn
open Wanxiangzhen.Shell.CoordinatorRuntime
open Wanxiangzhen.Shell.CoordinatorOps
open Wanxiangzhen.Shell.CoordinatorLifecycle
open Wanxiangzhen.Shell.HttpCodec
open Wanxiangzhen.Shell.SerialQueue
open Wanxiangzhen.Shell.HttpServer
open Wanxiangzhen.Tests.Assert
open Wanxiangzhen.Tests.TestDoubles

let testHappyPath () : JS.Promise<unit> =
    promise {
        let s    = TestDoubles.mkFake ()
        let deps = TestDoubles.mkDeps s
        let rt   = TestDoubles.mkRuntime deps

        // ① /squad → handleCommandExecuteBefore → squad_created frontmatter
        let input  = createObj [ "command", box "squad"; "sessionID", box "squad-session-001"; "arguments", box "add remember-me" ]
        let output = createObj [ "parts", box (System.Collections.Generic.List<obj>()) ]
        do! handleCommandExecuteBefore rt input output

        let parts = get output "parts" :?> System.Collections.Generic.List<obj>
        check (parts.Count = 1)
        check ((str parts.[0] "text").Contains "squad_event: squad_created")

        // ② handleSquadUpdate → task Pending ( Scheduling=true suppresses fire-and-forget tick )
        rt.Scheduling <- true
        let evts  = [| TestDoubles.mkTaskEvent "squad-a1b2" "add remember-me" "add remember-me to login" [] |]
        let args  = TestDoubles.mkSquadUpdateArgs evts
        let! reply = handleSquadUpdate rt args
        check (reply.Contains "created")
        check (reply.Contains "1")

        match TestDoubles.findTask "squad-a1b2" rt.Dag with
        | None -> check false
        | Some t -> check (t.Status = Pending)

        // ③ schedulerTick → task Running + worktree add + spawnSlave
        rt.Scheduling <- false
        do! schedulerTick rt

        match TestDoubles.findTask "squad-a1b2" rt.Dag with
        | None -> check false
        | Some t ->
            check (t.Status = Running)
            check (t.WorktreePath.IsSome)
            check (t.BranchName.IsSome)

        check (List.contains "tryWorktreeAdd" s.log.Value)
        check (List.exists (fun (x: string) -> x.StartsWith "spawnSlave") s.log.Value)

        // ④ POST /register → slavePid
        let! regResp = routeHandler rt "POST" "/task/squad-a1b2/register" (createObj [ "pid", box 12345 ])
        check (regResp.StatusCode = 200)
        check ((str regResp.Body "result") = "registered")

        match TestDoubles.findTask "squad-a1b2" rt.Dag with
        | None -> check false
        | Some t -> check (t.SlavePid = Some 12345)

        // ⑤ POST /submit → merged + cleanup
        // stale-commit guard: branch HEAD SHA must equal reported commitSha;
        // set per-branch override so RevParseRef("squad-a1b2") returns "deadbeef"
        s.revParseRefOverrides <- s.revParseRefOverrides.Add("squad-a1b2", "deadbeef")
        let! subResp = routeHandler rt "POST" "/task/squad-a1b2/submit" (createObj [ "commitSha", box "deadbeef" ])
        check (subResp.StatusCode = 200)
        check ((str subResp.Body "result") = "merged")

        match TestDoubles.findTask "squad-a1b2" rt.Dag with
        | None -> check false
        | Some t ->
            check (t.Status = Merged)
            check (t.MergedSha.IsSome)

        check (List.contains "tryWorktreeRemoveForce" s.log.Value)
        check (List.contains "tryBranchDeleteForce" s.log.Value)

        return ()
    }

// ══════════════════════════════════════════════════════════════════════════════
// Test 2 — Competing Submits → rebase_needed
// ══════════════════════════════════════════════════════════════════════════════

let testCompetingSubmitReturnsRebaseNeeded () : JS.Promise<unit> =
    promise {
        let s    = TestDoubles.mkFake ()   // mergeBaseTrueForFirstN = 1 → first call true, rest false
        let deps = TestDoubles.mkDeps s
        let rt   = TestDoubles.mkRuntime deps

        // create two independent tasks
        let evts =
            [| TestDoubles.mkTaskEvent "squad-a1b2" "Task A" "desc A" []
               TestDoubles.mkTaskEvent "squad-c3d4" "Task B" "desc B" [] |]
        let args = TestDoubles.mkSquadUpdateArgs evts
        rt.Scheduling <- true     // suppress fire-and-forget tick during creation
        let! _   = handleSquadUpdate rt args

        // start both tasks (re-enable scheduling first)
        rt.Scheduling <- false
        do! schedulerTick rt

        match TestDoubles.findTask "squad-a1b2" rt.Dag, TestDoubles.findTask "squad-c3d4" rt.Dag with
        | Some a, Some b ->
            check (a.Status = Running)
            check (b.Status = Running)
        | _ -> check false

        // register both slaves
        let! _ = routeHandler rt "POST" "/task/squad-a1b2/register" (createObj [ "pid", box 111 ])
        let! _ = routeHandler rt "POST" "/task/squad-c3d4/register" (createObj [ "pid", box 222 ])

        // Task A submit → merged (stale-commit guard uses default revParseRefResult="deadbeef")
        let! respA = routeHandler rt "POST" "/task/squad-a1b2/submit" (createObj [ "commitSha", box "deadbeef" ])
        check (respA.StatusCode = 200)
        check ((str respA.Body "result") = "merged")

        match TestDoubles.findTask "squad-a1b2" rt.Dag with
        | Some a -> check (a.Status = Merged)
        | None   -> check false

        check (s.mergeFfOnlyCalled = true)

        // Task B submit → rebase_needed (stale-commit guard uses default revParseRefResult="deadbeef")
        rt.Scheduling <- false   // reset before second explicit tick
        let! respB = routeHandler rt "POST" "/task/squad-c3d4/submit" (createObj [ "commitSha", box "deadbeef" ])
        check (respB.StatusCode = 200)
        check ((str respB.Body "result") = "rebase_needed")

        // B falls back to Running
        match TestDoubles.findTask "squad-c3d4" rt.Dag with
        | Some b -> check (b.Status = Running)
        | None   -> check false

        return ()
    }

// ══════════════════════════════════════════════════════════════════════════════
// Test 3 — DAG cycle rejected
// ══════════════════════════════════════════════════════════════════════════════

let testCycleRejected () : JS.Promise<unit> =
    promise {
        let s    = TestDoubles.mkFake ()
        let deps = TestDoubles.mkDeps s
        let rt   = TestDoubles.mkRuntime deps

        let evts =
            [| TestDoubles.mkTaskEvent "squad-a1b2" "A" "a" ["squad-c3d4"]
               TestDoubles.mkTaskEvent "squad-c3d4" "B" "b" ["squad-a1b2"] |]
        let args = TestDoubles.mkSquadUpdateArgs evts
        rt.Scheduling <- false
        let! result = handleSquadUpdate rt args
        check (result.Contains "cycle")
        check (rt.Dag.Tasks.IsEmpty)
        return ()
    }

// ══════════════════════════════════════════════════════════════════════════════
// Test 4 — Dangling dependency rejected
// ══════════════════════════════════════════════════════════════════════════════

let testDanglingDepsRejected () : JS.Promise<unit> =
    promise {
        let s    = TestDoubles.mkFake ()
        let deps = TestDoubles.mkDeps s
        let rt   = TestDoubles.mkRuntime deps

        let evts = [| TestDoubles.mkTaskEvent "squad-a1b2" "A" "a" ["squad-zzzz"] |]
        let args = TestDoubles.mkSquadUpdateArgs evts
        rt.Scheduling <- false
        let! result = handleSquadUpdate rt args
        check (result.Contains "squad-zzzz")
        check (rt.Dag.Tasks.IsEmpty)
        return ()
    }

// ══════════════════════════════════════════════════════════════════════════════
// Test 5 — /squad-status command
// ══════════════════════════════════════════════════════════════════════════════

let testSquadStatusCommand () : JS.Promise<unit> =
    promise {
        let s    = TestDoubles.mkFake ()
        let deps = TestDoubles.mkDeps s
        let rt   = TestDoubles.mkRuntime deps
        rt.MasterSessionId <- "squad-session-001"

        let evts  = [| TestDoubles.mkTaskEvent "squad-a1b2" "Task A" "desc" [] |]
        let args  = TestDoubles.mkSquadUpdateArgs evts
        rt.Scheduling <- false
        let! _    = handleSquadUpdate rt args

        let input  = createObj [ "command", box "squad-status"; "sessionID", box ""; "arguments", box "" ]
        let output = createObj [ "parts", box (System.Collections.Generic.List<obj>()) ]
        do! handleCommandExecuteBefore rt input output

        let parts = get output "parts" :?> System.Collections.Generic.List<obj>
        check (parts.Count = 1)
        let statusText = str parts.[0] "text"
        check (statusText.Contains "squad-a1b2")
        return ()
    }

// ══════════════════════════════════════════════════════════════════════════════
// Test 6 — /squad-kill: Running task → Cancelled, KillPid called, no cleanup
// ══════════════════════════════════════════════════════════════════════════════

let testSquadKillCommand () : JS.Promise<unit> =
    promise {
        let s    = TestDoubles.mkFake ()
        let deps = TestDoubles.mkDeps s
        let rt   = TestDoubles.mkRuntime deps
        rt.MasterSessionId <- "squad-session-001"

        // ① create task, suppress auto-tick
        let evts  = [| TestDoubles.mkTaskEvent "squad-a1b2" "Task A" "desc A" [] |]
        let args  = TestDoubles.mkSquadUpdateArgs evts
        rt.Scheduling <- true
        let! _    = handleSquadUpdate rt args

        // ② manual tick → Running + worktree + branch + spawn
        rt.Scheduling <- false
        do! schedulerTick rt

        match TestDoubles.findTask "squad-a1b2" rt.Dag with
        | None -> check false
        | Some t ->
            check (t.Status = Running)
            check (t.WorktreePath.IsSome)
            check (t.BranchName.IsSome)

        // ③ register pid
        let pid = 98765
        let! _  = routeHandler rt "POST" "/task/squad-a1b2/register" (createObj [ "pid", box pid ])

        // ④ /squad-kill (no session id → kill current session)
        let input  = createObj [ "command", box "squad-kill"; "sessionID", box ""; "arguments", box "" ]
        let output = createObj [ "parts", box (System.Collections.Generic.List<obj>()) ]
        do! handleCommandExecuteBefore rt input output

        // ⑤ assertions
        check s.killPidCalled
        check (s.killPidPid = Some pid)
        check (s.tryWorktreeRemoveForceCalls = [])
        check (s.tryBranchDeleteForceCalls = [])

        match TestDoubles.findTask "squad-a1b2" rt.Dag with
        | None -> check false
        | Some t -> check (t.Status = Cancelled)
        return ()
    }

let entriesAsync () : (string * (unit -> JS.Promise<unit>)) list = [
    ("MockE2e.happy_path: /squad → update → schedule → register → submit → merged",
     testHappyPath)

    ("MockE2e.competing_submit: second submit rebase_needed after first merged",
     testCompetingSubmitReturnsRebaseNeeded)

    ("MockE2e.cycle_rejected: handleSquadUpdate rejects DAG cycle",
     testCycleRejected)

    ("MockE2e.dangling_deps_rejected: unknown dep blocked",
     testDanglingDepsRejected)

    ("MockE2e.squad_status_command: /squad-status shows task list",
     testSquadStatusCommand)

    ("MockE2e.squad_kill_command: /squad-kill cancels running task, KillPid called, no cleanup",
     testSquadKillCommand)
]
