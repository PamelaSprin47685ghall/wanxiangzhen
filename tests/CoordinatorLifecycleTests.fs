module Wanxiangzhen.Tests.CoordinatorLifecycleTests

open Fable.Core
open Fable.Core.JsInterop
open Wanxiangzhen.Kernel.Task
open Wanxiangzhen.Kernel.Dag
open Wanxiangzhen.Kernel.SquadEvent
open Wanxiangzhen.Shell.Dyn
open Wanxiangzhen.Shell.CoordinatorRuntime
open Wanxiangzhen.Shell.SerialQueue
open Wanxiangzhen.Shell.CoordinatorLifecycle
open Wanxiangzhen.Shell.CoordinatorReplay
open Wanxiangzhen.Shell.SquadEventLogCodec
open Wanxiangzhen.Tests.Assert
open Wanxiangzhen.Tests.TestFixtures
let private mkTask = Wanxiangzhen.Tests.TestDoubles.mkTask
let private mkTasksCreated = Wanxiangzhen.Tests.TestDoubles.mkTasksCreated

// ══════════════════════════════════════════════════════════════════════════════
// All tests are async (handleSquadUpdate / replayFromEventLog return JS.Promise).
// Single entries () list — Tests.fs calls this, not entriesAsync.
// ══════════════════════════════════════════════════════════════════════════════

let entries () : (string * (unit -> JS.Promise<unit>)) list = [
    ("handleSquadUpdate null events returns Error", fun () ->
        promise {
            let rt = mkRuntime ()
            let! result = handleSquadUpdate rt (box null)
            checkBare (result.Contains "Error")
            checkBare (result.Contains "events")
        })

    ("handleSquadUpdate non-array events returns Error", fun () ->
        promise {
            let rt = mkRuntime ()
            let args = createObj [ "events", box "not-an-array" ]
            let! result = handleSquadUpdate rt args
            checkBare (result.Contains "Error")
        })

    ("handleSquadUpdate JSON string events are parsed and accepted", fun () ->
        promise {
            let rt = mkRuntime ()
            rt.Dag <- { rt.Dag with SessionId = "squad-session-001" }
            let json =
                "[{\"type\":\"tasks_created\",\"tasks\":[" +
                "{\"taskId\":\"squad-a1b2\",\"title\":\"Task A\",\"description\":\"Desc A\",\"dependsOn\":[]}" +
                "]}]"
            let args = createObj [ "events", box json ]
            let! result = handleSquadUpdate rt args
            checkBare (result.Contains "created")
            checkBare (rt.Dag.Tasks.ContainsKey "squad-a1b2")
        })

    ("handleSquadUpdate rejects tasks when session id is empty", fun () ->
        promise {
            let rt = mkRuntime ()
            let events = box [| mkTasksCreated [ mkTask "squad-a1b2" "Task A" "Desc A" [] ] |]
            let args = createObj [ "events", box events ]
            let! result = handleSquadUpdate rt args
            checkBare (result.Contains "Error")
            checkBare (result.Contains "session")
            checkBare (rt.Dag.Tasks.IsEmpty)
        })

    ("handleSquadUpdate empty title rejected — DAG unchanged", fun () ->
        promise {
            let rt = mkRuntime ()
            rt.Dag <- { rt.Dag with SessionId = "squad-session-001" }
            let events = box [| mkTasksCreated [ mkTask "squad-a1b2" "" "Desc A" [] ] |]
            let args = createObj [ "events", box events ]
            let! result = handleSquadUpdate rt args
            checkBare (result.Contains "Error")
            checkBare (result.Contains "non-empty")
            checkBare (rt.Dag.Tasks.IsEmpty)
        })

    ("handleSquadUpdate empty description rejected — DAG unchanged", fun () ->
        promise {
            let rt = mkRuntime ()
            rt.Dag <- { rt.Dag with SessionId = "squad-session-001" }
            let events = box [| mkTasksCreated [ mkTask "squad-a1b2" "Task A" "" [] ] |]
            let args = createObj [ "events", box events ]
            let! result = handleSquadUpdate rt args
            checkBare (result.Contains "Error")
            checkBare (result.Contains "non-empty")
            checkBare (rt.Dag.Tasks.IsEmpty)
        })

    ("handleSquadUpdate dangling deps returns dependency error", fun () ->
        promise {
            let rt = mkRuntime ()
            rt.Dag <- { rt.Dag with SessionId = "squad-session-001" }
            let events = box [| mkTasksCreated [ mkTask "squad-a1b2" "Task A" "Desc A" ["squad-zzzz"] ] |]
            let args = createObj [ "events", box events ]
            let! result = handleSquadUpdate rt args
            checkBare (result.Contains "squad-a1b2")
            checkBare (result.Contains "squad-zzzz")
        })

    ("handleSquadUpdate cycle returns cycle detected", fun () ->
        promise {
            let rt = mkRuntime ()
            rt.Dag <- { rt.Dag with SessionId = "squad-session-001" }
            let events = box [| mkTasksCreated [
                mkTask "squad-a1b2" "Task A" "desc A" ["squad-c3d4"]
                mkTask "squad-c3d4" "Task B" "desc B" ["squad-a1b2"]
            ] |]
            let args = createObj [ "events", box events ]
            let! result = handleSquadUpdate rt args
            checkBare (result.Contains "cycle")
        })

    ("handleSquadUpdate success returns confirmation text and injects events", fun () ->
        promise {
            let promptCalls = System.Collections.Generic.List<string * string * string>()
            let recordingDeps =
                { stubDeps () with
                    PromptSession = fun client sid msg ->
                        promptCalls.Add((string client, sid, msg)) |> ignore
                        Promise.lift () }
            let rt = mkRuntimeWithDeps recordingDeps
            rt.Dag <- { rt.Dag with SessionId = "squad-session-001" }
            rt.MasterSessionId <- "squad-session-001"
            let events = box [| mkTasksCreated [
                mkTask "squad-a1b2" "Task A" "Desc A" []
                mkTask "squad-c3d4" "Task B" "Desc B" ["squad-a1b2"]
            ] |]
            let args = createObj [ "events", box events ]
            let! result = handleSquadUpdate rt args
            // result is short confirmation text, NOT YAML frontmatter
            checkBare (not (result.StartsWith "---"))
            checkBare (result.Contains "created")
            checkBare (result.Contains "2")
            // events were injected into the master session via PromptSession
            checkBare (promptCalls.Count >= 1)
            let (_, injectedSid, injectedMsg) = promptCalls.[0]
            checkBare (injectedSid = "squad-session-001")
            checkBare (injectedMsg.Contains "squad_event: tasks_created")
            checkBare (injectedMsg.Contains "squad-a1b2")
        })

    ("mkRuntime produces independent GitQueue and InjectQueue", fun () ->
        promise {
            let rt = mkRuntime ()
            let same = obj.ReferenceEquals(box rt.GitQueue, box rt.InjectQueue)
            checkBare (not same)
        })

    // ══════════════════════════════════════════════════════════════════════════
    // New — cancel-only: events contain only squad_cancelled → result is
    // squad_cancelled event text, NOT tasks_created.
    // ══════════════════════════════════════════════════════════════════════════

    ("handleSquadUpdate cancel-only returns squad_cancelled not tasks_created", fun () ->
        promise {
            let rt = mkRuntime ()
            rt.Dag <- { rt.Dag with SessionId = "squad-session-001" }
            let events = box [| createObj [
                "type", box "squad_cancelled"
            ] |]
            let args = createObj [ "events", box events ]
            rt.MasterSessionId <- "squad-session-001"
            let! result = handleSquadUpdate rt args
            checkBare (result.Contains "cancelled")
            checkBare (not (result.Contains "tasks_created"))
            checkBare (not (result.StartsWith "---"))
        })

    // ══════════════════════════════════════════════════════════════════════════
    // New — replayFromEventLog: encoded history + git reconcile → Submitted
    // task upgraded to Merged.  Uses stubDeps + record overrides; no mkFake.
    // ══════════════════════════════════════════════════════════════════════════

    ("replayFromEventLog Submitted task with git reconcile → Merged", fun () ->
        promise {
            let fixedNow = "2025-01-01T00:00:00.0000000Z"
            let baseDeps = stubDeps ()
            let deps =
                { baseDeps with
                    Now                 = fun () -> fixedNow
                    MergeBaseIsAncestor = fun _ a d -> a = "main" && d = "squad-a1b2"
                    RevParseRef         = fun _ r -> if r = "main" then "merged-sha" else "deadbeef" }
            let evtSquadCreated  = SquadCreated ("squad-session-001", "add remember-me")
            let evtTasksCreated  = TasksCreated ("squad-session-001", [("squad-a1b2", "Task A", "desc A", [])])
            let evtTaskStarted   = TaskStarted ("squad-session-001", "squad-a1b2", "/wt/squad-a1b2", "squad-a1b2")
            let evtTaskSubmitted = TaskSubmitted ("squad-session-001", "squad-a1b2", "abc123")
            let history = [ evtSquadCreated; evtTasksCreated; evtTaskStarted; evtTaskSubmitted ]
            let deps2 = { deps with ReadAllSquadEvents = fun _ -> Promise.lift history }
            let rt = mkRuntimeWithDeps deps2
            rt.MasterSessionId <- "squad-session-001"
            rt.GitError       <- None
            do! replayFromEventLog rt
            match findTask "squad-a1b2" rt.Dag with
            | None -> checkBare false
            | Some t ->
                checkBare (t.Status = Merged)
                checkBare (t.MergedSha = Some "merged-sha")
        })
]
