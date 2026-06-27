module Wanxiangzhen.Tests.DagTests

open Wanxiangzhen.Kernel.Task
open Wanxiangzhen.Kernel.Dag
open Wanxiangzhen.Tests.Assert

let private mkTask id deps status =
    { Id = id; Title = ""; Description = ""; DependsOn = deps
      Status = status; WorktreePath = None; BranchName = None
      SlavePid = None; LastHeartbeatAt = None; MergedSha = None; CreatedAt = ""; UpdatedAt = "" }

let entries () : (string * (unit -> unit)) list = [
    ("Dag.empty", fun () ->
        let d = empty "s1" "req"
        equal "s1" d.SessionId
        equal "req" d.RootRequirement
        check d.Tasks.IsEmpty)

    ("Dag.addTask + findTask", fun () ->
        let d = empty "s1" "" |> addTask (mkTask "a" [] Pending)
        isSome (findTask "a" d)
        isNone (findTask "b" d))

    ("Dag.updateTask", fun () ->
        let d = empty "s1" "" |> addTask (mkTask "a" [] Pending)
        let d2 = d |> updateTask "a" (fun t -> { t with Title = "updated" })
        equal "updated" (findTask "a" d2).Value.Title)

    ("Dag.isReady no deps", fun () ->
        let d = empty "s1" "" |> addTask (mkTask "a" [] Pending)
        check (isReady (mkTask "a" [] Pending) d))

    ("Dag.isReady dep not merged", fun () ->
        let d = empty "s1" ""
                |> addTask (mkTask "dep" [] Pending)
                |> addTask (mkTask "a" ["dep"] Pending)
        check (not (isReady (findTask "a" d).Value d)))

    ("Dag.isReady dep merged", fun () ->
        let d = empty "s1" ""
                |> addTask (mkTask "dep" [] Merged)
                |> addTask (mkTask "a" ["dep"] Pending)
        check (isReady (findTask "a" d).Value d))

    ("Dag.readyTasks sorted", fun () ->
        let d = empty "s1" ""
                |> addTask (mkTask "b" [] Pending)
                |> addTask (mkTask "a" [] Pending)
                |> addTask (mkTask "c" ["a"] Pending)
        equal ["a"; "b"] (readyTasks d |> List.map (fun t -> t.Id)))

    ("Dag.runningCount", fun () ->
        let d = empty "s1" ""
                |> addTask (mkTask "a" [] Running)
                |> addTask (mkTask "b" [] Submitted)
                |> addTask (mkTask "c" [] Pending)
                |> addTask (mkTask "d" [] Merged)
        equal 2 (runningCount d))

    ("Dag.topologicalOrder linear", fun () ->
        let result = topologicalOrder [("c", ["b"]); ("b", ["a"]); ("a", [])]
        check (Result.isOk result)
        let order = result |> Result.defaultValue []
        equal 3 order.Length
        equal "a" order.[0]
        equal "b" order.[1]
        equal "c" order.[2])

    ("Dag.topologicalOrder branching", fun () ->
        let result = topologicalOrder [("d", ["b"; "c"]); ("c", ["a"]); ("b", ["a"]); ("a", [])]
        check (Result.isOk result))

    ("Dag.detectCycle none", fun () ->
        isNone (detectCycle [("a", []); ("b", ["a"]); ("c", ["b"])]))

    ("Dag.detectCycle self", fun () ->
        isSome (detectCycle [("a", ["a"])]))

    ("Dag.detectCycle mutual", fun () ->
        isSome (detectCycle [("a", ["b"]); ("b", ["a"])]))

    ("formatDag shows sessionId", fun () ->
        let d = empty "squad-session-001" "req"
        let s = formatDag d
        check (s.Contains "squad-session-001"))

    ("formatDag shows (no tasks) when empty", fun () ->
        let d = empty "s1" ""
        let s = formatDag d
        check (s.Contains "(no tasks)"))

    ("formatDag shows task line with id/status for single task", fun () ->
        let d = empty "s1" "" |> addTask (mkTask "squad-a1b2" [] Pending)
        let s = formatDag d
        check (s.Contains "squad-a1b2")
        check (s.Contains "pending"))

    ("formatDag shows deps when present", fun () ->
        let d = empty "s1" ""
                |> addTask (mkTask "squad-a1b2" ["squad-x9y8"] Pending)
        let s = formatDag d
        check (s.Contains "squad-x9y8"))

    ("formatSquadUpdateOutcome Success", fun () ->
        let s = formatSquadUpdateOutcome (Success 3)
        equal "3 tasks created, scheduler notified." s)

    ("formatSquadUpdateOutcome DependencyErrors", fun () ->
        let s = formatSquadUpdateOutcome (DependencyErrors [("squad-a1b2", "squad-zzzz")])
        check (s.Contains "squad-a1b2 dependsOn unknown squad-zzzz"))

    ("formatSquadUpdateOutcome CycleDetected", fun () ->
        let s = formatSquadUpdateOutcome (CycleDetected ["squad-x"; "squad-y"; "squad-x"])
        equal "Dependency cycle detected: squad-x → squad-y → squad-x. Please re-decompose without cycles." s)

    ("formatSquadUpdateOutcome InvalidInput", fun () ->
        let s = formatSquadUpdateOutcome (InvalidInput "bad input")
        equal "Error: bad input" s)
]
