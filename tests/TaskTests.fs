module Wanxiangzhen.Tests.TaskTests

open Wanxiangzhen.Kernel.Task
open Wanxiangzhen.Tests.Assert

let entries () : (string * (unit -> unit)) list = [
    ("Task.status round-trip", fun () ->
        [Pending; Running; Submitted; Merged; Done; Cancelled]
        |> List.iter (fun s ->
            equal (Some s) (s |> statusToString |> statusFromString)))

    ("Task.status fromString unknown", fun () ->
        isNone (statusFromString "bogus"))

    ("Task.isTerminal", fun () ->
        check (not (isTerminal Pending))
        check (not (isTerminal Running))
        check (not (isTerminal Submitted))
        check (isTerminal Merged)
        check (isTerminal Done)
        check (isTerminal Cancelled))

    ("Task.canTransition valid", fun () ->
        check (canTransition Pending Running)
        check (canTransition Pending Cancelled)
        check (canTransition Running Submitted)
        check (canTransition Running Done)
        check (canTransition Running Cancelled)
        check (canTransition Submitted Merged)
        check (canTransition Submitted Running)
        check (canTransition Submitted Done)
        check (canTransition Submitted Cancelled))

    ("Task.canTransition invalid", fun () ->
        check (not (canTransition Merged Running))
        check (not (canTransition Done Pending))
        check (not (canTransition Cancelled Running))
        check (not (canTransition Pending Merged))
        check (not (canTransition Running Pending)))

    ("Task.create", fun () ->
        let t = create "squad-a1b2" "title" "desc" ["squad-x"] "2024-01-01"
        equal "squad-a1b2" t.Id
        equal "title" t.Title
        equal "desc" t.Description
        equal ["squad-x"] t.DependsOn
        equal Pending t.Status
        isNone t.WorktreePath
        isNone t.BranchName
        isNone t.SlavePid
        isNone t.MergedSha)

    ("Task.withStatus valid transition", fun () ->
        let t = create "t1" "t" "d" [] "now"
        let t2 = withStatus t Running "later"
        equal Running t2.Status
        equal "later" t2.UpdatedAt)

    ("Task.tryWithStatus ok on valid", fun () ->
        let t = create "t1" "t" "d" [] "now"
        match tryWithStatus t Running "later" with
        | Ok t2 -> equal Running t2.Status; equal "later" t2.UpdatedAt
        | Error _ -> check false)

    ("Task.tryWithStatus error on invalid", fun () ->
        let t = { (create "t1" "t" "d" [] "now") with Status = Merged }
        match tryWithStatus t Running "later" with
        | Ok _ -> check false
        | Error msg -> check (msg.Length > 0))

    ("Task.withReconciledStatus overrides state machine", fun () ->
        let t = { (create "t1" "t" "d" [] "now") with Status = Running }
        let t2 = withReconciledStatus t Merged "later"
        equal Merged t2.Status
        equal "later" t2.UpdatedAt)

    ("Task.taskIdPrefix is squad-", fun () ->
        equal "squad-" taskIdPrefix)

    ("Task.create always generates Pending", fun () ->
        let t = create "squad-a1b2" "title" "desc" [] "2024-01-01"
        equal Pending t.Status)

    ("Task.withStatus throws on invalid transition", fun () ->
        let t = create "t1" "t" "d" [] "now"
        try 
            withStatus t Merged "later" |> ignore
            check false
        with _ -> 
            check true)
]
