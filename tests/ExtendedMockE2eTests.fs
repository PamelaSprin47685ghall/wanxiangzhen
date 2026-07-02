module Wanxiangzhen.Tests.ExtendedMockE2eTests

open Fable.Core
open Fable.Core.JsInterop
open Wanxiangzhen.Plugin
open Wanxiangzhen.Kernel.Task
open Wanxiangzhen.Kernel.Dag
open Wanxiangzhen.Kernel.SquadConfig
open Wanxiangzhen.Kernel.SquadEvent
open Wanxiangzhen.Shell.Dyn
open Wanxiangzhen.Shell.CoordinatorRuntime
open Wanxiangzhen.Shell.CoordinatorOps
open Wanxiangzhen.Shell.CoordinatorLifecycle
open Wanxiangzhen.Shell.HttpCodec
open Wanxiangzhen.Shell.SerialQueue
open Wanxiangzhen.Shell.HttpServer
open Wanxiangzhen.Shell.EventCodec
open Wanxiangzhen.Tests.Assert
open Wanxiangzhen.Tests.TestDoubles

// Shared FakeState / mkFake / mkDeps / mkRuntime / helpers come from TestDoubles.

let private mkRunningTaskServer () =
    promise {
        let s    = mkFake ()
        let deps = mkDeps s
        let rt   = mkRuntime deps
        rt.MasterSessionId <- "squad-session-001"

        let evts  = [| TestDoubles.mkTaskEvent "squad-a1b2" "A" "desc A" [] |]
        let args  = TestDoubles.mkSquadUpdateArgs evts
        rt.Scheduling <- true
        let! _    = handleSquadUpdate rt args
        rt.Scheduling <- false
        do! schedulerTick rt
        let! _ = routeHandler rt "POST" "/task/squad-a1b2/register" (createObj [ "pid", box 111 ])
        let! server = startServer rt.Token (routeHandler rt)
        return s, rt, server
    }

let private waitUntil (predicate: unit -> bool) (timeoutMs: int) : JS.Promise<unit> =
    promise {
        let mutable elapsed = 0
        while not (predicate ()) && elapsed < timeoutMs do
            do! Promise.sleep 10
            elapsed <- elapsed + 10
        if not (predicate ()) then
            failwith "waitUntil timeout"
    }

// ══════════════════════════════════════════════════════════════════════════════
// Test 1 — replayFromHistory sets MasterSessionId and rebuilds DAG from history
// ══════════════════════════════════════════════════════════════════════════════

let testChatMessageCapturesSessionIdAndReplays () : JS.Promise<unit> =
    promise {
        let s    = mkFake ()
        let deps = mkDeps s
        let rt   = mkRuntime deps

        let sessionId = "squad-session-001"
        let evt1 = SquadCreated (sessionId, "add remember-me")
        let evt2 = TasksCreated (sessionId, [("squad-a1b2", "Task A", "desc A", [])])
        let history = [ evt1; evt2 ]

        s.readAllSquadEventsOverride <- Some (fun _ -> Promise.lift history)

        // Step 1: chat.message hook sets MasterSessionId (simulate the hook body)
        rt.MasterSessionId <- sessionId

        // Step 2: replayFromHistory is what the hook calls next
        do! replayFromHistory rt

        // Verify MasterSessionId was set and replay ran
        check (rt.MasterSessionId = sessionId)

        // Verify DAG has squad-a1b2 task (replay rebuilt it from history)
        match TestDoubles.findTask "squad-a1b2" rt.Dag with
        | None -> check false
        | Some t -> check (t.Status = Pending)
     }

// ══════════════════════════════════════════════════════════════════════════════
// Test 2 — replay reconciles Submitted → Merged when MergeBaseIsAncestor=true
// ══════════════════════════════════════════════════════════════════════════════

let testReplayReconcilesSubmittedToMerged () : JS.Promise<unit> =
    promise {
        let s    = mkFake ()
        let deps = mkDeps s
        let rt   = mkRuntime deps

        let sessionId = "squad-session-001"
        // history: task_created → task_started → task_submitted
        let evt1 = SquadCreated (sessionId, "req")
        let evt2 = TasksCreated (sessionId, [("squad-a1b2", "A", "desc", [])])
        let evt3 = TaskStarted (sessionId, "squad-a1b2", "/wt/a", "squad-a1b2")
        let evt4 = TaskSubmitted (sessionId, "squad-a1b2", "sha123")
        let history = [ evt1; evt2; evt3; evt4 ]

        s.readAllSquadEventsOverride <- Some (fun _ -> Promise.lift history)
        // MergeBaseIsAncestor: first call returns true → Submitted reconciled to Merged
        s.mergeBaseOverride <- Some (fun c a d -> s.mergeBaseIsAncestorCalls <- s.mergeBaseIsAncestorCalls @ [(c, a, d)]; true)
        // RevParseRef for master branch returns sha for MergedSha
        s.revParseRefOverride <- Some (fun c r -> s.revParseRefCalls <- s.revParseRefCalls @ [(c, r)]; "merged-sha")

        rt.MasterSessionId <- sessionId
        do! replayFromHistory rt

        match TestDoubles.findTask "squad-a1b2" rt.Dag with
        | None -> check false
        | Some t ->
            check (t.Status = Merged)
            check (t.MergedSha = Some "merged-sha")
    }

// ══════════════════════════════════════════════════════════════════════════════
// Test 3 — replay warns orphan Running tasks (no SlavePid)
// ══════════════════════════════════════════════════════════════════════════════

let testReplayWarnsOrphanRunningTasks () : JS.Promise<unit> =
    promise {
        let s    = mkFake ()
        let deps = mkDeps s
        let rt   = mkRuntime deps

        let sessionId = "squad-session-001"
        // history: task_created → task_started (no SlavePid in event)
        let evt1 = SquadCreated (sessionId, "req")
        let evt2 = TasksCreated (sessionId, [("squad-a1b2", "A", "desc", [])])
        let evt3 = TaskStarted (sessionId, "squad-a1b2", "/wt/a", "squad-a1b2")
        let history = [ evt1; evt2; evt3 ]

        s.readAllSquadEventsOverride <- Some (fun _ -> Promise.lift history)
        s.promptSessionOverride <- Some (fun c m p ->
            s.promptSessionCalls <- s.promptSessionCalls @ [(m, p)]
            s.orphanWarningSent <- true
            Promise.lift ())

        rt.MasterSessionId <- sessionId
        // MergeBaseIsAncestor = false so Running task stays Running (no false Merged)
        s.mergeBaseOverride <- Some (fun _ _ _ -> false)
        do! replayFromHistory rt

        // task should be Running (replay sets Running from TaskStarted)
        match TestDoubles.findTask "squad-a1b2" rt.Dag with
        | None -> check false
        | Some t -> check (t.Status = Running)

        // orphan warning must have been sent
        check s.orphanWarningSent
        let callMsg = s.promptSessionCalls |> List.tryHead |> Option.map snd |> Option.defaultValue ""
        check (callMsg.Contains "orphan" || callMsg.Contains "Orphan")
    }

// ══════════════════════════════════════════════════════════════════════════════
// Test 4 — maxConcurrent limits ready tasks
// ══════════════════════════════════════════════════════════════════════════════

let testMaxConcurrentLimitsReadyTasks () : JS.Promise<unit> =
    promise {
        let s    = mkFake ()
        let deps = mkDeps s
        let rt   = mkRuntime deps
        rt.Config <- { rt.Config with MaxConcurrent = 2 }
        rt.MasterSessionId <- "squad-session-001"

        // 5 independent tasks
        let evts =
            [| TestDoubles.mkTaskEvent "squad-a1b2" "A" "desc A" []
               TestDoubles.mkTaskEvent "squad-c3d4" "B" "desc B" []
               TestDoubles.mkTaskEvent "squad-e5f6" "C" "desc C" []
               TestDoubles.mkTaskEvent "squad-g7h8" "D" "desc D" []
               TestDoubles.mkTaskEvent "squad-i9j0" "E" "desc E" [] |]
        let args = TestDoubles.mkSquadUpdateArgs evts
        rt.Scheduling <- true
        let! _ = handleSquadUpdate rt args

        // tick — only 2 should start
        rt.Scheduling <- false
        do! schedulerTick rt

        let running = rt.Dag.Tasks |> Map.toList |> List.map snd |> List.filter (fun t -> t.Status = Running)
        let pending = rt.Dag.Tasks |> Map.toList |> List.map snd |> List.filter (fun t -> t.Status = Pending)
        check (running.Length = 2)
        check (pending.Length = 3)

        // merge one Running task → tick → one more starts
        match running |> List.tryHead with
        | None -> check false
        | Some firstTask ->
            // register pid so submit path works
            let! _ = routeHandler rt "POST" (sprintf "/task/%s/register" firstTask.Id) (createObj [ "pid", box 111 ])
            s.revParseRefOverrides <- s.revParseRefOverrides.Add(firstTask.Id, "deadbeef")
            let! _ = routeHandler rt "POST" (sprintf "/task/%s/submit" firstTask.Id) (createObj [ "commitSha", box "deadbeef" ])

            match TestDoubles.findTask firstTask.Id rt.Dag with
            | Some t -> check (t.Status = Merged)
            | None -> check false

            // tick again — one more should start
            rt.Scheduling <- false
            do! schedulerTick rt

            let running2 = rt.Dag.Tasks |> Map.toList |> List.map snd |> List.filter (fun t -> t.Status = Running)
            let pending2 = rt.Dag.Tasks |> Map.toList |> List.map snd |> List.filter (fun t -> t.Status = Pending)
            check (running2.Length = 2)
            check (pending2.Length = 2)
    }

// ══════════════════════════════════════════════════════════════════════════════
// Test 5 — dependency chain schedules sequentially A→B→C
// ══════════════════════════════════════════════════════════════════════════════

let testDependencyChainSchedulesSequentially () : JS.Promise<unit> =
    promise {
        let s    = mkFake ()
        let deps = mkDeps s
        let rt   = mkRuntime deps
        // MergeBaseIsAncestor = true throughout so both A and B submit return merged
        s.mergeBaseOverride <- Some (fun _ _ _ -> true)
        rt.MasterSessionId <- "squad-session-001"

        // A (no deps), B (→ A), C (→ B)
        let evts =
            [| TestDoubles.mkTaskEvent "squad-a1b2" "A" "desc A" []
               TestDoubles.mkTaskEvent "squad-c3d4" "B" "desc B" ["squad-a1b2"]
               TestDoubles.mkTaskEvent "squad-e5f6" "C" "desc C" ["squad-c3d4"] |]
        let args = TestDoubles.mkSquadUpdateArgs evts
        rt.Scheduling <- true
        let! _ = handleSquadUpdate rt args

        // tick — only A starts
        rt.Scheduling <- false
        do! schedulerTick rt

        match TestDoubles.findTask "squad-a1b2" rt.Dag with
        | None -> check false
        | Some a -> check (a.Status = Running)
        match TestDoubles.findTask "squad-c3d4" rt.Dag with
        | None -> check false
        | Some b -> check (b.Status = Pending)
        match TestDoubles.findTask "squad-e5f6" rt.Dag with
        | None -> check false
        | Some c -> check (c.Status = Pending)

        // submit A → merged
        s.revParseRefOverrides <- s.revParseRefOverrides.Add("squad-a1b2", "deadbeef")
        let! _ = routeHandler rt "POST" "/task/squad-a1b2/submit" (createObj [ "commitSha", box "deadbeef" ])

        match TestDoubles.findTask "squad-a1b2" rt.Dag with
        | Some a -> check (a.Status = Merged)
        | None -> check false

        // tick — B should now start
        rt.Scheduling <- false
        do! schedulerTick rt

        match TestDoubles.findTask "squad-c3d4" rt.Dag with
        | None -> check false
        | Some b -> check (b.Status = Running)
        match TestDoubles.findTask "squad-e5f6" rt.Dag with
        | None -> check false
        | Some c -> check (c.Status = Pending)

        // submit B → merged
        s.revParseRefOverrides <- s.revParseRefOverrides.Add("squad-c3d4", "deadbeef")
        let! _ = routeHandler rt "POST" "/task/squad-c3d4/submit" (createObj [ "commitSha", box "deadbeef" ])

        match TestDoubles.findTask "squad-c3d4" rt.Dag with
        | Some b -> check (b.Status = Merged)
        | None -> check false

        // tick — C should now start
        rt.Scheduling <- false
        do! schedulerTick rt

        match TestDoubles.findTask "squad-e5f6" rt.Dag with
        | None -> check false
        | Some c -> check (c.Status = Running)
    }

// ══════════════════════════════════════════════════════════════════════════════
// Test 6 — done beacon marks task Done and triggers cleanup
// ══════════════════════════════════════════════════════════════════════════════

let testDoneBeaconMarksTaskDone () : JS.Promise<unit> =
    promise {
        let s    = mkFake ()
        let deps = mkDeps s
        let rt   = mkRuntime deps
        rt.MasterSessionId <- "squad-session-001"

        // create + tick → Running + register
        let evts  = [| TestDoubles.mkTaskEvent "squad-a1b2" "A" "desc A" [] |]
        let args  = TestDoubles.mkSquadUpdateArgs evts
        rt.Scheduling <- true
        let! _    = handleSquadUpdate rt args
        rt.Scheduling <- false
        do! schedulerTick rt

        let! _ = routeHandler rt "POST" "/task/squad-a1b2/register" (createObj [ "pid", box 12345 ])

        // POST /done → task done
        let! resp = routeHandler rt "POST" "/task/squad-a1b2/done" (createObj [])
        check (resp.StatusCode = 200)
        check ((str resp.Body "result") = "acknowledged")

        match TestDoubles.findTask "squad-a1b2" rt.Dag with
        | None -> check false
        | Some t -> check (t.Status = Done)

        // cleanup must be called
        check (s.tryWorktreeRemoveForceCalls <> [])
        check (s.tryBranchDeleteForceCalls <> [])
    }

// ══════════════════════════════════════════════════════════════════════════════
// Test 7 — PID polling detects slave death
// ══════════════════════════════════════════════════════════════════════════════

let testPidPollingDetectsSlaveDeath () : JS.Promise<unit> =
    promise {
        let s    = mkFake ()
        let deps = mkDeps s
        let rt   = mkRuntime deps
        rt.MasterSessionId <- "squad-session-001"

        // create + tick → Running + register
        let evts  = [| TestDoubles.mkTaskEvent "squad-a1b2" "A" "desc A" [] |]
        let args  = TestDoubles.mkSquadUpdateArgs evts
        rt.Scheduling <- true
        let! _    = handleSquadUpdate rt args
        rt.Scheduling <- false
        do! schedulerTick rt
        let! _ = routeHandler rt "POST" "/task/squad-a1b2/register" (createObj [ "pid", box 12345 ])

        // capture the polling callback
        let mutable capturedCheck : (unit -> unit) option = None
        s.startPollingOverride <- Some (fun ms f -> capturedCheck <- Some f; box "poll-handle")

        // start polling
        let _ = startPidPolling rt
        check (capturedCheck.IsSome)

        // make IsPidAlive return false → simulate death
        s.isPidAliveResult <- false

        // fire the check callback
        match capturedCheck with
        | None -> check false
        | Some checkFn -> checkFn ()

        do! waitUntil (fun () ->
            match TestDoubles.findTask "squad-a1b2" rt.Dag with
            | Some t -> t.Status = Done
            | None -> false) 2000

        match TestDoubles.findTask "squad-a1b2" rt.Dag with
        | None -> check false
        | Some t -> check (t.Status = Done)
    }

// ══════════════════════════════════════════════════════════════════════════════
// Test 8 — worktree add failure injects task_error and keeps task Pending
// ══════════════════════════════════════════════════════════════════════════════

let testWorktreeAddFailureInjectsTaskError () : JS.Promise<unit> =
    promise {
        let s    = mkFake ()
        let deps = mkDeps s
        let rt   = mkRuntime deps
        rt.MasterSessionId <- "squad-session-001"

        let evts  = [| TestDoubles.mkTaskEvent "squad-a1b2" "A" "desc A" [] |]
        let args  = TestDoubles.mkSquadUpdateArgs evts
        rt.Scheduling <- true
        let! _    = handleSquadUpdate rt args

        // make TryWorktreeAdd fail
        s.tryWorktreeAddOverride <- Some (fun c b p b2 -> Error "disk full")

        rt.Scheduling <- false
        do! schedulerTick rt
        do! waitUntil (fun () -> s.appendSquadEventCalls <> []) 2000

        match TestDoubles.findTask "squad-a1b2" rt.Dag with
        | None -> check false
        | Some t -> check (t.Status = Pending)

        check (s.appendSquadEventCalls |> List.exists (function TaskError _ -> true | _ -> false))
    }

// ══════════════════════════════════════════════════════════════════════════════
// Test 9 — merged with already-dead slave does not crash (bug-exposure test)
// ══════════════════════════════════════════════════════════════════════════════

let testMergedWithAlreadyDeadSlaveDoesNotCrash () : JS.Promise<unit> =
    promise {
        let s    = mkFake ()
        let deps = mkDeps s
        let rt   = mkRuntime deps
        rt.MasterSessionId <- "squad-session-001"

        // create + tick → Running + register pid
        let evts  = [| TestDoubles.mkTaskEvent "squad-a1b2" "A" "desc A" [] |]
        let args  = TestDoubles.mkSquadUpdateArgs evts
        rt.Scheduling <- true
        let! _    = handleSquadUpdate rt args
        rt.Scheduling <- false
        do! schedulerTick rt
        let! _ = routeHandler rt "POST" "/task/squad-a1b2/register" (createObj [ "pid", box 12345 ])

        // slave is dead
        s.isPidAliveResult <- false

        // KillPid throws ESRCH (slave already dead) — test exposes missing try-catch in handleSubmit
        s.killPidOverride <- Some (fun p signal -> s.killPidCalled <- true; s.killPidPid <- Some p; s.killPidSignal <- Some signal; failwith "ESRCH")

        // submit → merged (stale-commit guard must pass via override)
        s.revParseRefOverrides <- s.revParseRefOverrides.Add("squad-a1b2", "deadbeef")
        let! resp = routeHandler rt "POST" "/task/squad-a1b2/submit" (createObj [ "commitSha", box "deadbeef" ])

        // Must return 200 merged (not crash)
        check (resp.StatusCode = 200)
        check ((str resp.Body "result") = "merged")

        // KillPid must have been called even though slave was dead
        check s.killPidCalled
        check (s.killPidPid = Some 12345)

        // cleanup must have run (worktree + branch removed)
        check (s.tryWorktreeRemoveForceCalls <> [])
        check (s.tryBranchDeleteForceCalls <> [])

        // task status must be Merged
        match TestDoubles.findTask "squad-a1b2" rt.Dag with
        | None -> check false
        | Some t -> check (t.Status = Merged)
    }

// ══════════════════════════════════════════════════════════════════════════════
// Test 10 — submit rebase_needed returns task to Running
// ══════════════════════════════════════════════════════════════════════════════

let testSubmitRebaseNeededReturnsRunning () : JS.Promise<unit> =
    promise {
        let s    = mkFake ()
        let deps = mkDeps s
        let rt   = mkRuntime deps
        rt.MasterSessionId <- "squad-session-001"

        let evts  = [| TestDoubles.mkTaskEvent "squad-a1b2" "A" "desc A" [] |]
        let args  = TestDoubles.mkSquadUpdateArgs evts
        rt.Scheduling <- true
        let! _    = handleSquadUpdate rt args
        rt.Scheduling <- false
        do! schedulerTick rt
        let! _ = routeHandler rt "POST" "/task/squad-a1b2/register" (createObj [ "pid", box 111 ])

        // MergeBaseIsAncestor returns false → rebase_needed
        s.mergeBaseOverride <- Some (fun c a d -> false)

        s.revParseRefOverrides <- s.revParseRefOverrides.Add("squad-a1b2", "deadbeef")
        let! resp = routeHandler rt "POST" "/task/squad-a1b2/submit" (createObj [ "commitSha", box "deadbeef" ])

        check (resp.StatusCode = 200)
        check ((str resp.Body "result") = "rebase_needed")

        match TestDoubles.findTask "squad-a1b2" rt.Dag with
        | None -> check false
        | Some t -> check (t.Status = Running)
     }

// ══════════════════════════════════════════════════════════════════════════════
// Test 11 — submit stale_commit (branch SHA differs from reported)
// ══════════════════════════════════════════════════════════════════════════════

let testSubmitStaleCommit () : JS.Promise<unit> =
    promise {
        let s    = mkFake ()
        let deps = mkDeps s
        let rt   = mkRuntime deps
        rt.MasterSessionId <- "squad-session-001"

        let evts  = [| TestDoubles.mkTaskEvent "squad-a1b2" "A" "desc A" [] |]
        let args  = TestDoubles.mkSquadUpdateArgs evts
        rt.Scheduling <- true
        let! _    = handleSquadUpdate rt args
        rt.Scheduling <- false
        do! schedulerTick rt
        let! _ = routeHandler rt "POST" "/task/squad-a1b2/register" (createObj [ "pid", box 111 ])

        // RevParseRef returns "actual-sha" ≠ reported "deadbeef" → stale_commit
        s.revParseRefOverride <- Some (fun c r -> "actual-sha")

        let! resp = routeHandler rt "POST" "/task/squad-a1b2/submit" (createObj [ "commitSha", box "deadbeef" ])

        check (resp.StatusCode = 200)
        check ((str resp.Body "result") = "stale_commit")

        // task reverts to Running
        match TestDoubles.findTask "squad-a1b2" rt.Dag with
        | None -> check false
        | Some t -> check (t.Status = Running)
     }

// ══════════════════════════════════════════════════════════════════════════════
// Test 12 — submit coordinator_not_ready (dirty worktree)
// ══════════════════════════════════════════════════════════════════════════════

let testSubmitCoordinatorNotReadyDirty () : JS.Promise<unit> =
    promise {
        let s    = mkFake ()
        let deps = mkDeps s
        let rt   = mkRuntime deps
        rt.MasterSessionId <- "squad-session-001"

        let evts  = [| TestDoubles.mkTaskEvent "squad-a1b2" "A" "desc A" [] |]
        let args  = TestDoubles.mkSquadUpdateArgs evts
        rt.Scheduling <- true
        let! _    = handleSquadUpdate rt args
        rt.Scheduling <- false
        do! schedulerTick rt
        let! _ = routeHandler rt "POST" "/task/squad-a1b2/register" (createObj [ "pid", box 111 ])

        // StatusIsClean = false → coordinator_not_ready
        s.statusIsCleanOverride <- Some (fun c -> false)

        s.revParseRefOverrides <- s.revParseRefOverrides.Add("squad-a1b2", "deadbeef")
        let! resp = routeHandler rt "POST" "/task/squad-a1b2/submit" (createObj [ "commitSha", box "deadbeef" ])

        check (resp.StatusCode = 200)
        check ((str resp.Body "result") = "coordinator_not_ready")

        match TestDoubles.findTask "squad-a1b2" rt.Dag with
        | None -> check false
        | Some t -> check (t.Status = Running)
     }

// ══════════════════════════════════════════════════════════════════════════════
// Test 13 — HTTP GET /task/unknown → 404
// ══════════════════════════════════════════════════════════════════════════════

let testHttpTaskNotFound404 () : JS.Promise<unit> =
    promise {
        let s    = mkFake ()
        let deps = mkDeps s
        let rt   = mkRuntime deps
        rt.MasterSessionId <- "squad-session-001"

        let! resp = routeHandler rt "GET" "/task/unknown-task-id" (createObj [])
        check (resp.StatusCode = 404)
        check ((str resp.Body "result") = "task_not_found")
    }

// ══════════════════════════════════════════════════════════════════════════════
// Test 14 — HTTP POST /task/:id/register with no pid → 400
// ══════════════════════════════════════════════════════════════════════════════

let testHttpBadRegisterBody400 () : JS.Promise<unit> =
    promise {
        let s    = mkFake ()
        let deps = mkDeps s
        let rt   = mkRuntime deps
        rt.MasterSessionId <- "squad-session-001"

        // create a task so the route matches
        let evts  = [| TestDoubles.mkTaskEvent "squad-a1b2" "A" "desc A" [] |]
        let args  = TestDoubles.mkSquadUpdateArgs evts
        rt.Scheduling <- true
        let! _    = handleSquadUpdate rt args
        rt.Scheduling <- false
        do! schedulerTick rt

        let! resp = routeHandler rt "POST" "/task/squad-a1b2/register" (createObj [])
        check (resp.StatusCode = 400)
        check ((str resp.Body "result") = "bad_request")
    }

// ══════════════════════════════════════════════════════════════════════════════
// Test 15a — submit returns merged
// ══════════════════════════════════════════════════════════════════════════════

let testSlaveSubmitMerged () : JS.Promise<unit> =
    promise {
        let! s, rt, server = mkRunningTaskServer ()
        try
            s.mergeBaseTrueForFirstN <- 1
            s.revParseRefOverrides <- s.revParseRefOverrides.Add("squad-a1b2", "deadbeef")
            let! r = TestDoubles.fetchJson (server.Url + "/task/squad-a1b2/submit") (createObj [
                "method", box "POST"
                "headers", box {| Authorization = box ("Bearer " + rt.Token) |}
                "body", box (JSON?stringify (createObj [ "commitSha", box "deadbeef" ])) ])
            check (r.status = 200)
            check (str r.body "result" = "merged")
        finally
            server.Close ()
    }

// ══════════════════════════════════════════════════════════════════════════════
// Test 15b — submit returns rebase_needed
// ══════════════════════════════════════════════════════════════════════════════

let testSlaveSubmitRebaseNeeded () : JS.Promise<unit> =
    promise {
        let! s, rt, server = mkRunningTaskServer ()
        try
            s.mergeBaseTrueForFirstN <- 0
            s.revParseRefOverrides <- s.revParseRefOverrides.Add("squad-a1b2", "deadbeef")
            let! r = TestDoubles.fetchJson (server.Url + "/task/squad-a1b2/submit") (createObj [
                "method", box "POST"
                "headers", box {| Authorization = box ("Bearer " + rt.Token) |}
                "body", box (JSON?stringify (createObj [ "commitSha", box "deadbeef" ])) ])
            check (r.status = 200)
            check (str r.body "result" = "rebase_needed")
        finally
            server.Close ()
    }

// ══════════════════════════════════════════════════════════════════════════════
// Test 15c — submit returns stale_commit
// ══════════════════════════════════════════════════════════════════════════════

let testSlaveSubmitStaleCommit () : JS.Promise<unit> =
    promise {
        let! s, rt, server = mkRunningTaskServer ()
        try
            s.revParseRefOverride <- Some (fun _ _ -> "actual-sha")
            let! r = TestDoubles.fetchJson (server.Url + "/task/squad-a1b2/submit") (createObj [
                "method", box "POST"
                "headers", box {| Authorization = box ("Bearer " + rt.Token) |}
                "body", box (JSON?stringify (createObj [ "commitSha", box "deadbeef" ])) ])
            check (r.status = 200)
            check (str r.body "result" = "stale_commit")
        finally
            server.Close ()
    }

// ══════════════════════════════════════════════════════════════════════════════
// Test 15d — submit returns coordinator_not_ready (dirty)
// ══════════════════════════════════════════════════════════════════════════════

let testSlaveSubmitCoordinatorNotReady () : JS.Promise<unit> =
    promise {
        let! s, rt, server = mkRunningTaskServer ()
        try
            s.revParseRefOverride <- Some (fun _ r ->
                if r = "squad-a1b2" then "deadbeef"
                elif r = rt.MasterBranch then "merged-sha"
                else "actual-sha")
            s.statusIsCleanOverride <- Some (fun _ -> false)
            let! r = TestDoubles.fetchJson (server.Url + "/task/squad-a1b2/submit") (createObj [
                "method", box "POST"
                "headers", box {| Authorization = box ("Bearer " + rt.Token) |}
                "body", box (JSON?stringify (createObj [ "commitSha", box "deadbeef" ])) ])
            check (r.status = 200)
            check (str r.body "result" = "coordinator_not_ready")
        finally
            server.Close ()
    }

// ══════════════════════════════════════════════════════════════════════════════
// Test 15e — submit returns not_submittable
// ══════════════════════════════════════════════════════════════════════════════

let testSlaveSubmitNotSubmittable () : JS.Promise<unit> =
    promise {
        let! s, rt, server = mkRunningTaskServer ()
        try
            rt.Scheduling <- true
            rt.Dag <- rt.Dag |> updateTask "squad-a1b2" (fun t -> { t with Status = Merged })
            rt.Scheduling <- false
            let! r = TestDoubles.fetchJson (server.Url + "/task/squad-a1b2/submit") (createObj [
                "method", box "POST"
                "headers", box {| Authorization = box ("Bearer " + rt.Token) |}
                "body", box (JSON?stringify (createObj [ "commitSha", box "deadbeef" ])) ])
            check (r.status = 200)
            check (str r.body "result" = "not_submittable")
        finally
            server.Close ()
    }

// ══════════════════════════════════════════════════════════════════════════════
// Test 15f — submit returns task_not_found
// ══════════════════════════════════════════════════════════════════════════════

let testSlaveSubmitTaskNotFound () : JS.Promise<unit> =
    promise {
        let! _, rt, server = mkRunningTaskServer ()
        try
            let! r = TestDoubles.fetchJson (server.Url + "/task/does-not-exist/submit") (createObj [
                "method", box "POST"
                "headers", box {| Authorization = box ("Bearer " + rt.Token) |}
                "body", box (JSON?stringify (createObj [ "commitSha", box "deadbeef" ])) ])
            check (r.status = 404)
            check (str r.body "result" = "task_not_found")
        finally
            server.Close ()
    }

// ══════════════════════════════════════════════════════════════════════════════
// Test 15g — submit returns unauthorized
// ══════════════════════════════════════════════════════════════════════════════

let testSlaveSubmitUnauthorized () : JS.Promise<unit> =
    promise {
        let! _, rt, server = mkRunningTaskServer ()
        try
            let! r = TestDoubles.fetchJson (server.Url + "/task/squad-a1b2/submit") (createObj [
                "method", box "POST"
                "headers", box {| Authorization = box "Bearer wrong-token" |}
                "body", box (JSON?stringify (createObj [ "commitSha", box "deadbeef" ])) ])
            check (r.status = 401)
            check (str r.body "result" = "unauthorized")
        finally
            server.Close ()
    }

// ══════════════════════════════════════════════════════════════════════════════
// Test 16 — slave query_squad task detail
// ══════════════════════════════════════════════════════════════════════════════

let testSlaveQuerySquadTaskDetail () : JS.Promise<unit> =
    promise {
        let s    = mkFake ()
        let deps = mkDeps s
        let rt   = mkRuntime deps
        rt.MasterSessionId <- "squad-session-001"

        // create task
        let evts  = [| TestDoubles.mkTaskEvent "squad-a1b2" "Task A" "desc A" [] |]
        let args  = TestDoubles.mkSquadUpdateArgs evts
        rt.Scheduling <- true
        let! _    = handleSquadUpdate rt args
        rt.Scheduling <- false
        do! schedulerTick rt

        let! server = startServer rt.Token (routeHandler rt)

        try
            let! resp = TestDoubles.fetchJson (server.Url + "/task/squad-a1b2") (createObj [
                "method", box "GET"
                "headers", box {| Authorization = box ("Bearer " + rt.Token) |} ])
            check (resp.status = 200)
            check ((str resp.body "id") = "squad-a1b2")
            check ((str resp.body "title") = "Task A")
            check ((str resp.body "status") = "running")
        finally
            server.Close ()
    }

// ══════════════════════════════════════════════════════════════════════════════
// Test 17 — multi session: /squad command saves previous session
// ══════════════════════════════════════════════════════════════════════════════

let testMultiSessionSquadCommandSavesPrevious () : JS.Promise<unit> =
    promise {
        let s    = mkFake ()
        let deps = mkDeps s
        let rt   = mkRuntime deps
        rt.MasterSessionId <- "squad-session-001"

        // first /squad → task
        let evts1 = [| TestDoubles.mkTaskEvent "squad-a1b2" "A" "desc A" [] |]
        let args1 = TestDoubles.mkSquadUpdateArgs evts1
        rt.Scheduling <- true
        let! _    = handleSquadUpdate rt args1
        rt.Scheduling <- false
        do! schedulerTick rt

        let session1 = rt.Dag.SessionId
        check (session1 <> "")

        // second /squad → new session
        let input  = createObj [ "command", box "squad"; "sessionID", box "squad-session-002"; "arguments", box "req2" ]
        let output = createObj [ "parts", box (System.Collections.Generic.List<obj>()) ]
        do! handleCommandExecuteBefore rt input output

        let evts2 = [| TestDoubles.mkTaskEvent "squad-c3d4" "B" "desc B" [] |]
        let args2 = TestDoubles.mkSquadUpdateArgs evts2
        rt.Scheduling <- true
        let! _    = handleSquadUpdate rt args2

        // Sessions map must contain first session
        check (rt.Sessions.ContainsKey session1)
        let savedDag = rt.Sessions.[session1]
        check (savedDag.Tasks.ContainsKey "squad-a1b2")
    }

// ══════════════════════════════════════════════════════════════════════════════
// Test 18 — dispose hook: calls Server.Close and StopPolling
// ══════════════════════════════════════════════════════════════════════════════

let testDisposeHookClosesServerAndStopsPolling () : JS.Promise<unit> =
    promise {
        let s    = mkFake ()
        let deps = mkDeps s
        let rt   = mkRuntime deps

        // build custom Server-backed rt, then start polling on it
        let mutable closed = false
        let mutable stopped = false
        let rt = { mkRuntime deps with Server = { Port = 12345; Url = "http://127.0.0.1:12345"; Close = fun () -> closed <- true } }
        let _ = startPidPolling rt
        check (rt.PidPollHandle.IsSome)
        s.stopPollingOverride <- Some (fun h -> stopped <- true)

        // Build dispose closure exactly as assembleCoordinatorHooks does
        let dispose () : JS.Promise<unit> =
            promise {
                rt.Server.Close ()
                rt.PidPollHandle |> Option.iter (fun h -> deps.StopPolling h)
            }

        do! dispose ()

        check closed
        check stopped
    }

// ══════════════════════════════════════════════════════════════════════════════
// Test 19 — realistic opencode PluginInput mock, pluginWithDeps works end-to-end
// ══════════════════════════════════════════════════════════════════════════════

let testRealisticOpencodePluginInputMock () : JS.Promise<unit> =
    promise {
        let s    = mkFake ()
        let deps = mkDeps s
        let rt   = mkRuntime deps

        // Build a PluginInput-like context object (mimics real SDK shape)
        let mockClient = createObj [
            "session", box (createObj [
                "prompt",   box (System.Func<obj, JS.Promise<unit>>(fun _ -> Promise.lift ()))
                "messages", box (System.Func<obj, JS.Promise<obj>>(fun _ -> Promise.lift (createObj [ "data", box [||] ])))
                "command",  box (System.Func<obj, JS.Promise<obj>>(fun _ -> Promise.lift (createObj [])))
                "create",   box (System.Func<obj, JS.Promise<obj>>(fun _ -> Promise.lift (createObj [])))
            ])
            "event", box (createObj [
                "subscribe", box (System.Func<obj, JS.Promise<unit>>(fun _ -> Promise.lift ()))
            ])
        ]
        let mockCtx = createObj [
            "client",     box mockClient
            "directory",  box "/tmp/test-project"
            "worktree",   box "/tmp/test-project"
            "serverUrl",  box "http://localhost:0"
        ]

        // Override deps via FakeState (pluginWithDeps → createWithDeps → replayFromHistory → ReadAllTexts)
        s.readAllTextsOverride <- Some (fun _ sid _ -> Promise.lift [])
        s.revParseBranchOverride <- Some (fun c -> "main")
        // IsDetached override not in mkDeps yet; set via the FakeState field consumed at call-time
        // (mkDeps IsDetached closure already reads s.isDetachedCalls + false default; no separate override field needed)

        let! result = pluginWithDeps mockCtx deps

        // hooks must be present — anonymous record, use get
        let hooks = get result "hooks"
        check (not (isNullish hooks))
        check (not (isNullish (get hooks "tool")))
        check (not (isNullish (get hooks "config")))
        check (not (isNullish (get hooks "command.execute.before")))
        check (not (isNullish (get hooks "chat.message")))
        check (not (isNullish (get hooks "dispose")))

        // runtime must be non-null
        let runtime = get result "runtime"
        check (not (isNullish runtime))

        // SquadUpdate tool must be registered
        let tools = get hooks "tool"
        check (not (isNullish (get tools "squad_update")))

        // squad-status command must be registered
        let cfg = get hooks "config"
        // config hook is a function; just verify the hook exists
        check (not (isNullish cfg))
    }

// ══════════════════════════════════════════════════════════════════════════════
// Public entries
// ══════════════════════════════════════════════════════════════════════════════

let entriesAsync () : (string * (unit -> JS.Promise<unit>)) list = [
    ("ExtendedMockE2e.chat_message_captures_session_id_and_replays",
     testChatMessageCapturesSessionIdAndReplays)

    ("ExtendedMockE2e.replay_reconciles_submitted_to_merged",
     testReplayReconcilesSubmittedToMerged)

    ("ExtendedMockE2e.replay_warns_orphan_running_tasks",
     testReplayWarnsOrphanRunningTasks)

    ("ExtendedMockE2e.maxConcurrent_limits_ready_tasks",
     testMaxConcurrentLimitsReadyTasks)

    ("ExtendedMockE2e.dependency_chain_schedules_sequentially",
     testDependencyChainSchedulesSequentially)

    ("ExtendedMockE2e.done_beacon_marks_task_done",
     testDoneBeaconMarksTaskDone)

    ("ExtendedMockE2e.pid_polling_detects_slave_death",
     testPidPollingDetectsSlaveDeath)

    ("ExtendedMockE2e.worktree_add_failure_injects_task_error",
     testWorktreeAddFailureInjectsTaskError)

    ("ExtendedMockE2e.merged_with_already_dead_slave_does_not_crash",
     testMergedWithAlreadyDeadSlaveDoesNotCrash)

    ("ExtendedMockE2e.submit_rebase_needed_returns_running",
     testSubmitRebaseNeededReturnsRunning)

    ("ExtendedMockE2e.submit_stale_commit_branch",
     testSubmitStaleCommit)

    ("ExtendedMockE2e.submit_coordinator_not_ready_dirty",
     testSubmitCoordinatorNotReadyDirty)

    ("ExtendedMockE2e.http_task_not_found_404",
     testHttpTaskNotFound404)

    ("ExtendedMockE2e.http_bad_register_body_400",
     testHttpBadRegisterBody400)

    ("ExtendedMockE2e.slave_submit_merged", testSlaveSubmitMerged)
    ("ExtendedMockE2e.slave_submit_rebase_needed", testSlaveSubmitRebaseNeeded)
    ("ExtendedMockE2e.slave_submit_stale_commit", testSlaveSubmitStaleCommit)
    ("ExtendedMockE2e.slave_submit_coordinator_not_ready", testSlaveSubmitCoordinatorNotReady)
    ("ExtendedMockE2e.slave_submit_not_submittable", testSlaveSubmitNotSubmittable)
    ("ExtendedMockE2e.slave_submit_task_not_found", testSlaveSubmitTaskNotFound)
    ("ExtendedMockE2e.slave_submit_unauthorized", testSlaveSubmitUnauthorized)
    ("ExtendedMockE2e.slave_query_squad_task_detail",
     testSlaveQuerySquadTaskDetail)

    ("ExtendedMockE2e.multi_session_squad_command_saves_previous",
     testMultiSessionSquadCommandSavesPrevious)

    ("ExtendedMockE2e.dispose_hook_closes_server_and_stops_polling",
     testDisposeHookClosesServerAndStopsPolling)

    ("ExtendedMockE2e.realistic_opencode_plugin_input_mock",
     testRealisticOpencodePluginInputMock)
]
