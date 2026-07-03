module Wanxiangzhen.Tests.CoordinatorLifecycleEventLogTests

open Fable.Core
open Fable.Core.JsInterop
open Wanxiangzhen.Kernel.Task
open Wanxiangzhen.Kernel.Dag
open Wanxiangzhen.Shell.CoordinatorRuntime
open Wanxiangzhen.Shell.CoordinatorLifecycle
open Wanxiangzhen.Tests.Assert
open Wanxiangzhen.Tests.TestFixtures
let private mkTask = Wanxiangzhen.Tests.TestDoubles.mkTask
let private mkTasksCreated = Wanxiangzhen.Tests.TestDoubles.mkTasksCreated

let entries () : (string * (unit -> JS.Promise<unit>)) list = [
    ("handleSquadKill append fail → task remains Running not Cancelled", fun () ->
        promise {
            let failDeps =
                { stubDeps () with
                    AppendSquadEvent = fun _ _ _ -> Promise.lift (Error "test-append-failure") }
            let rt = mkRuntimeWithDeps failDeps
            let now = rt.Deps.Now ()
            let task = Wanxiangzhen.Kernel.Task.create "squad-a1b2" "Task A" "Desc A" [] now
            rt.Dag <- rt.Dag |> addTask task
            rt.Dag <- rt.Dag |> updateTask "squad-a1b2" (fun t -> { t with Status = Running; UpdatedAt = now })
            do! handleSquadKill rt None
            match findTask "squad-a1b2" rt.Dag with
            | None -> check false
            | Some t -> check (t.Status = Running)
            check (rt.InjectError.IsSome)
            match rt.InjectError with
            | Some msg -> check (msg.Contains "append failed")
            | None -> check false
        })

    ("handleSquadUpdate append fail → Dag unchanged + error text", fun () ->
        promise {
            let failDeps =
                { stubDeps () with
                    AppendSquadEvent = fun _ _ _ -> Promise.lift (Error "test-append-failure") }
            let rt = mkRuntimeWithDeps failDeps
            let events = box [| mkTasksCreated [ mkTask "squad-a1b2" "Task A" "Desc A" [] ] |]
            let args = createObj [ "events", box events ]
            let! result = handleSquadUpdate rt args
            check (rt.Dag.Tasks.IsEmpty)
            check (result.Contains "append failed")
        })

    ("handleSquadKill Ok → Pending and Running both Cancelled (foldEvent parity)", fun () ->
        promise {
            let rt = mkRuntimeWithDeps (stubDeps ())
            let now = rt.Deps.Now ()
            let pending = Wanxiangzhen.Kernel.Task.create "squad-pend" "P" "d" [] now
            let running = Wanxiangzhen.Kernel.Task.create "squad-run" "R" "d" [] now
            rt.Dag <- rt.Dag |> addTask pending |> addTask running
            rt.Dag <- rt.Dag |> updateTask "squad-run" (fun t -> { t with Status = Running })
            do! handleSquadKill rt None
            match findTask "squad-pend" rt.Dag, findTask "squad-run" rt.Dag with
            | Some p, Some r -> check (p.Status = Cancelled && r.Status = Cancelled)
            | _ -> check false
        })
]
