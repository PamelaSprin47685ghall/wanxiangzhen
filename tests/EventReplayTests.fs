module Wanxiangzhen.Tests.EventReplayTests

open Wanxiangzhen.Kernel.Task
open Wanxiangzhen.Kernel.Dag
open Wanxiangzhen.Kernel.SquadEvent
open Wanxiangzhen.Tests.Assert

let private mkEvent ty =
    { Type = ty; SessionId = "s1"; TaskId = Some "a"; Title = None
      Description = None; DependsOn = None; WorktreePath = None
      BranchName = None; SlavePid = None; CommitSha = None
      MasterSha = None; Merged = None }

let entries () : (string * (unit -> unit)) list = [
    ("Event.fold SquadCreated", fun () ->
        let d = empty "s1" ""
        let d2 = foldEvent d (mkEvent SquadCreated)
        check d2.Tasks.IsEmpty)

    ("Event.fold TaskCreated", fun () ->
        let d = empty "s1" ""
        let e = { mkEvent TaskCreated with Title = Some "t"; Description = Some "d" }
        let d2 = foldEvent d e
        equal Pending (findTask "a" d2).Value.Status)

    ("Event.fold TaskStarted", fun () ->
        let d = empty "s1" "" |> addTask (Wanxiangzhen.Kernel.Task.create "a" "t" "d" [] "now")
        let d2 = foldEvent d { mkEvent TaskStarted with WorktreePath = Some "/wt"; BranchName = Some "a" }
        equal Running (findTask "a" d2).Value.Status
        equal (Some "/wt") (findTask "a" d2).Value.WorktreePath)

    ("Event.fold TaskSubmitted", fun () ->
        let d = empty "s1" "" |> addTask (Wanxiangzhen.Kernel.Task.create "a" "t" "d" [] "now")
        let d2 = foldEvent d (mkEvent TaskSubmitted)
        equal Submitted (findTask "a" d2).Value.Status)

    ("Event.fold TaskMerged", fun () ->
        let d = empty "s1" "" |> addTask (Wanxiangzhen.Kernel.Task.create "a" "t" "d" [] "now")
        let d2 = foldEvent d { mkEvent TaskMerged with MasterSha = Some "sha123" }
        equal Merged (findTask "a" d2).Value.Status
        equal (Some "sha123") (findTask "a" d2).Value.MergedSha)

    ("Event.fold TaskDone", fun () ->
        let d = empty "s1" "" |> addTask (Wanxiangzhen.Kernel.Task.create "a" "t" "d" [] "now")
        let d2 = foldEvent d (mkEvent TaskDone)
        equal Done (findTask "a" d2).Value.Status)

    ("Event.fold SquadCancelled", fun () ->
        let d = empty "s1" ""
                |> addTask (Wanxiangzhen.Kernel.Task.create "a" "t" "d" [] "now")
                |> addTask ({ Wanxiangzhen.Kernel.Task.create "b" "t" "d" [] "now" with Status = Merged })
        let d2 = foldEvent d (mkEvent SquadCancelled)
        equal Cancelled (findTask "a" d2).Value.Status
        equal Merged (findTask "b" d2).Value.Status)

    ("Event.foldEvents sequence", fun () ->
        let events = [
            { mkEvent TaskCreated with Title = Some "t"; Description = Some "d" }
            mkEvent TaskStarted
            mkEvent TaskSubmitted
            { mkEvent TaskMerged with MasterSha = Some "sha" }
        ]
        let d = foldEvents events (empty "s1" "")
        equal Merged (findTask "a" d).Value.Status)
]
