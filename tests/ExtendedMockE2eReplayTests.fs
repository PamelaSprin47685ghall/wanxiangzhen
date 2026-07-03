module Wanxiangzhen.Tests.ExtendedMockE2eReplayTests

open Fable.Core
open Fable.Core.JsInterop
open Wanxiangzhen.Kernel.Task
open Wanxiangzhen.Kernel.Dag
open Wanxiangzhen.Kernel.SquadEvent
open Wanxiangzhen.Shell.CoordinatorRuntime
open Wanxiangzhen.Shell.CoordinatorReplay
open Wanxiangzhen.Tests.Assert
open Wanxiangzhen.Tests.TestDoubles
open Wanxiangzhen.Tests.ExtendedMockE2eHelpers

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

        rt.MasterSessionId <- sessionId
        do! replayFromEventLog rt

        check (rt.MasterSessionId = sessionId)

        match findTask "squad-a1b2" rt.Dag with
        | None -> check false
        | Some t -> check (t.Status = Pending)
     }

let testReplayReconcilesSubmittedToMerged () : JS.Promise<unit> =
    promise {
        let s    = mkFake ()
        let deps = mkDeps s
        let rt   = mkRuntime deps

        let sessionId = "squad-session-001"
        let evt1 = SquadCreated (sessionId, "req")
        let evt2 = TasksCreated (sessionId, [("squad-a1b2", "A", "desc", [])])
        let evt3 = TaskStarted (sessionId, "squad-a1b2", "/wt/a", "squad-a1b2")
        let evt4 = TaskSubmitted (sessionId, "squad-a1b2", "sha123")
        let history = [ evt1; evt2; evt3; evt4 ]

        s.readAllSquadEventsOverride <- Some (fun _ -> Promise.lift history)
        s.mergeBaseOverride <- Some (fun c a d -> s.mergeBaseIsAncestorCalls <- s.mergeBaseIsAncestorCalls @ [(c, a, d)]; true)
        s.revParseRefOverride <- Some (fun c r -> s.revParseRefCalls <- s.revParseRefCalls @ [(c, r)]; "merged-sha")

        rt.MasterSessionId <- sessionId
        do! replayFromEventLog rt

        match findTask "squad-a1b2" rt.Dag with
        | None -> check false
        | Some t ->
            check (t.Status = Merged)
            check (t.MergedSha = Some "merged-sha")
    }

let testReplayWarnsOrphanRunningTasks () : JS.Promise<unit> =
    promise {
        let s    = mkFake ()
        let deps = mkDeps s
        let rt   = mkRuntime deps

        let sessionId = "squad-session-001"
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
        s.mergeBaseOverride <- Some (fun _ _ _ -> false)
        do! replayFromEventLog rt

        match findTask "squad-a1b2" rt.Dag with
        | None -> check false
        | Some t -> check (t.Status = Running)

        check s.orphanWarningSent
        let callMsg = s.promptSessionCalls |> List.tryHead |> Option.map snd |> Option.defaultValue ""
        check (callMsg.Contains "orphan" || callMsg.Contains "Orphan")
    }

let entriesAsync () : (string * (unit -> JS.Promise<unit>)) list = [
    ("ExtendedMockE2e.chat_message_captures_session_id_and_replays",
     testChatMessageCapturesSessionIdAndReplays)

    ("ExtendedMockE2e.replay_reconciles_submitted_to_merged",
     testReplayReconcilesSubmittedToMerged)

    ("ExtendedMockE2e.replay_warns_orphan_running_tasks",
     testReplayWarnsOrphanRunningTasks)
]
