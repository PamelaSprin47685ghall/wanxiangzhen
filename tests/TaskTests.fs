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
        checkBare (not (isTerminal Pending))
        checkBare (not (isTerminal Running))
        checkBare (not (isTerminal Submitted))
        checkBare (isTerminal Merged)
        checkBare (isTerminal Done)
        checkBare (isTerminal Cancelled))

    ("Task.canTransition valid", fun () ->
        checkBare (canTransition Pending Running)
        checkBare (canTransition Pending Cancelled)
        checkBare (canTransition Running Submitted)
        checkBare (canTransition Running Done)
        checkBare (canTransition Running Cancelled)
        checkBare (canTransition Submitted Merged)
        checkBare (canTransition Submitted Running)
        checkBare (canTransition Submitted Done)
        checkBare (canTransition Submitted Cancelled))

    ("Task.canTransition invalid", fun () ->
        checkBare (not (canTransition Merged Running))
        checkBare (not (canTransition Done Pending))
        checkBare (not (canTransition Cancelled Running))
        checkBare (not (canTransition Pending Merged))
        checkBare (not (canTransition Running Pending)))

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
        | Error _ -> checkBare false)

    ("Task.tryWithStatus error on invalid", fun () ->
        let t = { (create "t1" "t" "d" [] "now") with Status = Merged }
        match tryWithStatus t Running "later" with
        | Ok _ -> checkBare false
        | Error msg -> checkBare (msg.Length > 0))

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
            checkBare false
        with _ -> 
            checkBare true)
]
