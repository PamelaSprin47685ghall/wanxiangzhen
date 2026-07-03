module Wanxiangzhen.Tests.ExtendedMockE2eSchedulerTests

open Fable.Core
open Fable.Core.JsInterop
open Wanxiangzhen.Kernel.Task
open Wanxiangzhen.Kernel.Dag
open Wanxiangzhen.Kernel.SquadConfig
open Wanxiangzhen.Shell.Dyn
open Wanxiangzhen.Shell.CoordinatorRuntime
open Wanxiangzhen.Shell.CoordinatorOps
open Wanxiangzhen.Shell.CoordinatorLifecycle
open Wanxiangzhen.Shell.HttpCodec
open Wanxiangzhen.Tests.Assert
open Wanxiangzhen.Tests.TestDoubles
open Wanxiangzhen.Tests.ExtendedMockE2eHelpers

let testMaxConcurrentLimitsReadyTasks () : JS.Promise<unit> =
    promise {
        let s    = mkFake ()
        let deps = mkDeps s
        let rt   = mkRuntime deps
        rt.Config <- { rt.Config with MaxConcurrent = 2 }
        rt.MasterSessionId <- "squad-session-001"

        let evts =
            [| mkTaskEvent "squad-a1b2" "A" "desc A" []
               mkTaskEvent "squad-c3d4" "B" "desc B" []
               mkTaskEvent "squad-e5f6" "C" "desc C" []
               mkTaskEvent "squad-g7h8" "D" "desc D" []
               mkTaskEvent "squad-i9j0" "E" "desc E" [] |]
        let args = mkSquadUpdateArgs evts
        rt.Scheduling <- true
        let! _ = handleSquadUpdate rt args

        rt.Scheduling <- false
        do! schedulerTick rt

        let running = rt.Dag.Tasks |> Map.toList |> List.map snd |> List.filter (fun t -> t.Status = Running)
        let pending = rt.Dag.Tasks |> Map.toList |> List.map snd |> List.filter (fun t -> t.Status = Pending)
        checkBare (running.Length = 2)
        checkBare (pending.Length = 3)

        match running |> List.tryHead with
        | None -> checkBare false
        | Some firstTask ->
            let! _ = routeHandler rt "POST" (sprintf "/task/%s/register" firstTask.Id) (createObj [ "pid", box 111 ])
            s.revParseRefOverrides <- s.revParseRefOverrides.Add(firstTask.Id, "deadbeef")
            let! _ = routeHandler rt "POST" (sprintf "/task/%s/submit" firstTask.Id) (createObj [ "commitSha", box "deadbeef" ])

            match findTask firstTask.Id rt.Dag with
            | Some t -> checkBare (t.Status = Merged)
            | None -> checkBare false

            rt.Scheduling <- false
            do! schedulerTick rt

            let running2 = rt.Dag.Tasks |> Map.toList |> List.map snd |> List.filter (fun t -> t.Status = Running)
            let pending2 = rt.Dag.Tasks |> Map.toList |> List.map snd |> List.filter (fun t -> t.Status = Pending)
            checkBare (running2.Length = 2)
            checkBare (pending2.Length = 2)
    }

let testDependencyChainSchedulesSequentially () : JS.Promise<unit> =
    promise {
        let s    = mkFake ()
        let deps = mkDeps s
        let rt   = mkRuntime deps
        s.mergeBaseOverride <- Some (fun _ _ _ -> true)
        rt.MasterSessionId <- "squad-session-001"

        let evts =
            [| mkTaskEvent "squad-a1b2" "A" "desc A" []
               mkTaskEvent "squad-c3d4" "B" "desc B" ["squad-a1b2"]
               mkTaskEvent "squad-e5f6" "C" "desc C" ["squad-c3d4"] |]
        let args = mkSquadUpdateArgs evts
        rt.Scheduling <- true
        let! _ = handleSquadUpdate rt args

        rt.Scheduling <- false
        do! schedulerTick rt

        match findTask "squad-a1b2" rt.Dag with
        | None -> checkBare false
        | Some a -> checkBare (a.Status = Running)
        match findTask "squad-c3d4" rt.Dag with
        | None -> checkBare false
        | Some b -> checkBare (b.Status = Pending)
        match findTask "squad-e5f6" rt.Dag with
        | None -> checkBare false
        | Some c -> checkBare (c.Status = Pending)

        s.revParseRefOverrides <- s.revParseRefOverrides.Add("squad-a1b2", "deadbeef")
        let! _ = routeHandler rt "POST" "/task/squad-a1b2/submit" (createObj [ "commitSha", box "deadbeef" ])

        match findTask "squad-a1b2" rt.Dag with
        | Some a -> checkBare (a.Status = Merged)
        | None -> checkBare false

        rt.Scheduling <- false
        do! schedulerTick rt

        match findTask "squad-c3d4" rt.Dag with
        | None -> checkBare false
        | Some b -> checkBare (b.Status = Running)
        match findTask "squad-e5f6" rt.Dag with
        | None -> checkBare false
        | Some c -> checkBare (c.Status = Pending)

        s.revParseRefOverrides <- s.revParseRefOverrides.Add("squad-c3d4", "deadbeef")
        let! _ = routeHandler rt "POST" "/task/squad-c3d4/submit" (createObj [ "commitSha", box "deadbeef" ])

        match findTask "squad-c3d4" rt.Dag with
        | Some b -> checkBare (b.Status = Merged)
        | None -> checkBare false

        rt.Scheduling <- false
        do! schedulerTick rt

        match findTask "squad-e5f6" rt.Dag with
        | None -> checkBare false
        | Some c -> checkBare (c.Status = Running)
    }

let testDoneBeaconMarksTaskDone () : JS.Promise<unit> =
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

        let! resp = routeHandler rt "POST" "/task/squad-a1b2/done" (createObj [])
        checkBare (resp.StatusCode = 200)
        checkBare ((str resp.Body "result") = "acknowledged")

        match findTask "squad-a1b2" rt.Dag with
        | None -> checkBare false
        | Some t -> checkBare (t.Status = Done)

        checkBare (s.tryWorktreeRemoveForceCalls <> [])
        checkBare (s.tryBranchDeleteForceCalls <> [])
    }

let testPidPollingDetectsSlaveDeath () : JS.Promise<unit> =
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

        let mutable capturedCheck : (unit -> unit) option = None
        s.startPollingOverride <- Some (fun ms f -> capturedCheck <- Some f; box "poll-handle")

        let _ = startPidPolling rt
        checkBare (capturedCheck.IsSome)

        s.isPidAliveResult <- false

        match capturedCheck with
            | None -> checkBare false
            | Some checkFn -> checkFn ()

        do! waitUntil (fun () ->
            match findTask "squad-a1b2" rt.Dag with
            | Some t -> t.Status = Done
            | None -> false) 2000

        match findTask "squad-a1b2" rt.Dag with
        | None -> checkBare false
        | Some t -> checkBare (t.Status = Done)
    }

let entriesAsync () : (string * (unit -> JS.Promise<unit>)) list = [
    ("ExtendedMockE2e.maxConcurrent_limits_ready_tasks",
     testMaxConcurrentLimitsReadyTasks)

    ("ExtendedMockE2e.dependency_chain_schedules_sequentially",
     testDependencyChainSchedulesSequentially)

    ("ExtendedMockE2e.done_beacon_marks_task_done",
     testDoneBeaconMarksTaskDone)

    ("ExtendedMockE2e.pid_polling_detects_slave_death",
     testPidPollingDetectsSlaveDeath)
]
