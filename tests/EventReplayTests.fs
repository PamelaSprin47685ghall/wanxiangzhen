module Wanxiangzhen.Tests.EventReplayTests

open Wanxiangzhen.Kernel.Task
open Wanxiangzhen.Kernel.Dag
open Wanxiangzhen.Kernel.SquadEvent
open Wanxiangzhen.Tests.Assert

let private t = Wanxiangzhen.Kernel.Task.create

let entries () : (string * (unit -> unit)) list = [
    ("Event.fold SquadCreated", fun () ->
        let d = empty "" ""
        let d2 = foldEvent d (SquadCreated ("s1", "req"))
        equal "s1" d2.SessionId
        equal "req" d2.RootRequirement)

    ("Event.fold TaskCreated", fun () ->
        let d = empty "s1" ""
        let d2 = foldEvent d (TaskCreated ("s1", "a", "t", "d", []))
        equal Pending (findTask "a" d2).Value.Status)

    ("Event.fold TaskStarted", fun () ->
        let d = empty "s1" "" |> addTask (t "a" "t" "d" [] "now")
        let d2 = foldEvent d (TaskStarted ("s1", "a", "/wt", "a"))
        equal Running (findTask "a" d2).Value.Status
        equal (Some "/wt") (findTask "a" d2).Value.WorktreePath)

    ("Event.fold TaskSubmitted", fun () ->
        let d = empty "s1" "" |> addTask (t "a" "t" "d" [] "now")
        let d2 = foldEvent d (TaskSubmitted ("s1", "a", "sha"))
        equal Submitted (findTask "a" d2).Value.Status)

    ("Event.fold TaskMerged", fun () ->
        let d = empty "s1" "" |> addTask (t "a" "t" "d" [] "now")
        let d2 = foldEvent d (TaskMerged ("s1", "a", "sha123"))
        equal Merged (findTask "a" d2).Value.Status
        equal (Some "sha123") (findTask "a" d2).Value.MergedSha)

    ("Event.fold TaskDone", fun () ->
        let d = empty "s1" "" |> addTask (t "a" "t" "d" [] "now")
        let d2 = foldEvent d (TaskDone ("s1", "a", false))
        equal Done (findTask "a" d2).Value.Status)

    ("Event.fold SquadCancelled", fun () ->
        let d = empty "s1" ""
                |> addTask (t "a" "t" "d" [] "now")
                |> addTask ({ t "b" "t" "d" [] "now" with Status = Merged })
        let d2 = foldEvent d (SquadCancelled "s1")
        equal Cancelled (findTask "a" d2).Value.Status
        equal Merged (findTask "b" d2).Value.Status)

    ("Event.foldEvents sequence", fun () ->
        let events = [
            TaskCreated ("s1", "a", "t", "d", [])
            TaskStarted ("s1", "a", "/wt", "a")
            TaskSubmitted ("s1", "a", "sha")
            TaskMerged ("s1", "a", "sha")
        ]
        let d = foldEvents events (empty "s1" "")
        equal Merged (findTask "a" d).Value.Status)
]
