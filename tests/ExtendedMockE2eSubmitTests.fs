module Wanxiangzhen.Tests.ExtendedMockE2eSubmitTests

open Fable.Core
open Fable.Core.JsInterop
open Wanxiangzhen.Kernel.Task
open Wanxiangzhen.Kernel.Dag
open Wanxiangzhen.Kernel.SquadEvent
open Wanxiangzhen.Shell.Dyn
open Wanxiangzhen.Shell.CoordinatorRuntime
open Wanxiangzhen.Shell.CoordinatorOps
open Wanxiangzhen.Shell.CoordinatorLifecycle
open Wanxiangzhen.Shell.HttpCodec
open Wanxiangzhen.Tests.Assert
open Wanxiangzhen.Tests.TestDoubles
open Wanxiangzhen.Tests.ExtendedMockE2eHelpers

let testWorktreeAddFailureInjectsTaskError () : JS.Promise<unit> =
    promise {
        let s    = mkFake ()
        let deps = mkDeps s
        let rt   = mkRuntime deps
        rt.MasterSessionId <- "squad-session-001"

        let evts  = [| mkTaskEvent "squad-a1b2" "A" "desc A" [] |]
        let args  = mkSquadUpdateArgs evts
        rt.Scheduling <- true
        let! _    = handleSquadUpdate rt args

        s.tryWorktreeAddOverride <- Some (fun c b p b2 -> Error "disk full")

        rt.Scheduling <- false
        do! schedulerTick rt
        do! waitUntil (fun () -> s.appendSquadEventCalls <> []) 2000

        match findTask "squad-a1b2" rt.Dag with
        | None -> checkBare false
        | Some t -> checkBare (t.Status = Pending)

        checkBare (s.appendSquadEventCalls |> List.exists (function TaskError _ -> true | _ -> false))
    }

let testMergedWithAlreadyDeadSlaveDoesNotCrash () : JS.Promise<unit> =
    promise {
        let s    = mkFake ()
        let deps = mkDeps s
        let rt   = mkRuntime deps
        rt.MasterSessionId <- "squad-session-001"

        let evts  = [| mkTaskEvent "squad-a1b2" "A" "desc A" [] |]
        let args  = mkSquadUpdateArgs evts
        rt.Scheduling <- true
        let! _    = handleSquadUpdate rt args
        rt.Scheduling <- false
        do! schedulerTick rt
        let! _ = routeHandler rt "POST" "/task/squad-a1b2/register" (createObj [ "pid", box 12345 ])

        s.isPidAliveResult <- false

        s.killPidOverride <- Some (fun p signal -> s.killPidCalled <- true; s.killPidPid <- Some p; s.killPidSignal <- Some signal; failwith "ESRCH")

        s.revParseRefOverrides <- s.revParseRefOverrides.Add("squad-a1b2", "deadbeef")
        let! resp = routeHandler rt "POST" "/task/squad-a1b2/submit" (createObj [ "commitSha", box "deadbeef" ])

        checkBare (resp.StatusCode = 200)
        checkBare ((str resp.Body "result") = "merged")

        checkBare s.killPidCalled
        checkBare (s.killPidPid = Some 12345)

        checkBare (s.tryWorktreeRemoveForceCalls <> [])
        checkBare (s.tryBranchDeleteForceCalls <> [])

        match findTask "squad-a1b2" rt.Dag with
        | None -> checkBare false
        | Some t -> checkBare (t.Status = Merged)
    }

let testSubmitRebaseNeededReturnsRunning () : JS.Promise<unit> =
    promise {
        let s    = mkFake ()
        let deps = mkDeps s
        let rt   = mkRuntime deps
        rt.MasterSessionId <- "squad-session-001"

        let evts  = [| mkTaskEvent "squad-a1b2" "A" "desc A" [] |]
        let args  = mkSquadUpdateArgs evts
        rt.Scheduling <- true
        let! _    = handleSquadUpdate rt args
        rt.Scheduling <- false
        do! schedulerTick rt
        let! _ = routeHandler rt "POST" "/task/squad-a1b2/register" (createObj [ "pid", box 111 ])

        s.mergeBaseOverride <- Some (fun c a d -> false)

        s.revParseRefOverrides <- s.revParseRefOverrides.Add("squad-a1b2", "deadbeef")
        let! resp = routeHandler rt "POST" "/task/squad-a1b2/submit" (createObj [ "commitSha", box "deadbeef" ])

        checkBare (resp.StatusCode = 200)
        checkBare ((str resp.Body "result") = "rebase_needed")

        match findTask "squad-a1b2" rt.Dag with
        | None -> checkBare false
        | Some t -> checkBare (t.Status = Running)
     }

let testSubmitStaleCommit () : JS.Promise<unit> =
    promise {
        let s    = mkFake ()
        let deps = mkDeps s
        let rt   = mkRuntime deps
        rt.MasterSessionId <- "squad-session-001"

        let evts  = [| mkTaskEvent "squad-a1b2" "A" "desc A" [] |]
        let args  = mkSquadUpdateArgs evts
        rt.Scheduling <- true
        let! _    = handleSquadUpdate rt args
        rt.Scheduling <- false
        do! schedulerTick rt
        let! _ = routeHandler rt "POST" "/task/squad-a1b2/register" (createObj [ "pid", box 111 ])

        s.revParseRefOverride <- Some (fun c r -> "actual-sha")

        let! resp = routeHandler rt "POST" "/task/squad-a1b2/submit" (createObj [ "commitSha", box "deadbeef" ])

        checkBare (resp.StatusCode = 200)
        checkBare ((str resp.Body "result") = "stale_commit")

        match findTask "squad-a1b2" rt.Dag with
        | None -> checkBare false
        | Some t -> checkBare (t.Status = Running)
     }

let testSubmitCoordinatorNotReadyDirty () : JS.Promise<unit> =
    promise {
        let s    = mkFake ()
        let deps = mkDeps s
        let rt   = mkRuntime deps
        rt.MasterSessionId <- "squad-session-001"

        let evts  = [| mkTaskEvent "squad-a1b2" "A" "desc A" [] |]
        let args  = mkSquadUpdateArgs evts
        rt.Scheduling <- true
        let! _    = handleSquadUpdate rt args
        rt.Scheduling <- false
        do! schedulerTick rt
        let! _ = routeHandler rt "POST" "/task/squad-a1b2/register" (createObj [ "pid", box 111 ])

        s.statusIsCleanOverride <- Some (fun c -> false)

        s.revParseRefOverrides <- s.revParseRefOverrides.Add("squad-a1b2", "deadbeef")
        let! resp = routeHandler rt "POST" "/task/squad-a1b2/submit" (createObj [ "commitSha", box "deadbeef" ])

        checkBare (resp.StatusCode = 200)
        checkBare ((str resp.Body "result") = "coordinator_not_ready")

        match findTask "squad-a1b2" rt.Dag with
        | None -> checkBare false
        | Some t -> checkBare (t.Status = Running)
     }

let testHttpTaskNotFound404 () : JS.Promise<unit> =
    promise {
        let s    = mkFake ()
        let deps = mkDeps s
        let rt   = mkRuntime deps
        rt.MasterSessionId <- "squad-session-001"

        let! resp = routeHandler rt "GET" "/task/unknown-task-id" (createObj [])
        checkBare (resp.StatusCode = 404)
        checkBare ((str resp.Body "result") = "task_not_found")
    }

let testHttpBadRegisterBody400 () : JS.Promise<unit> =
    promise {
        let s    = mkFake ()
        let deps = mkDeps s
        let rt   = mkRuntime deps
        rt.MasterSessionId <- "squad-session-001"

        let evts  = [| mkTaskEvent "squad-a1b2" "A" "desc A" [] |]
        let args  = mkSquadUpdateArgs evts
        rt.Scheduling <- true
        let! _    = handleSquadUpdate rt args
        rt.Scheduling <- false
        do! schedulerTick rt

        let! resp = routeHandler rt "POST" "/task/squad-a1b2/register" (createObj [])
        checkBare (resp.StatusCode = 400)
        checkBare ((str resp.Body "result") = "bad_request")
    }

let entriesAsync () : (string * (unit -> JS.Promise<unit>)) list = [
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
]
