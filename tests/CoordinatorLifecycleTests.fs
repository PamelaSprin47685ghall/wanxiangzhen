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
open Wanxiangzhen.Shell.EventCodec
open Wanxiangzhen.Tests.Assert
open Wanxiangzhen.Tests.TestFixtures

// ══════════════════════════════════════════════════════════════════════════════
// All tests are async (handleSquadUnit / replayFromHistory return JS.Promise).
// Single entries () list — Tests.fs calls this, not entriesAsync.
// ══════════════════════════════════════════════════════════════════════════════

let entries () : (string * (unit -> JS.Promise<unit>)) list = [
    ("handleSquadUpdate null events returns Error", fun () ->
        promise {
            let rt = mkRuntime ()
            let! result = handleSquadUpdate rt (box null)
            check (result.Contains "Error")
            check (result.Contains "events")
        })

    ("handleSquadUpdate non-array events returns Error", fun () ->
        promise {
            let rt = mkRuntime ()
            let args = createObj [ "events", box "not-an-array" ]
            let! result = handleSquadUpdate rt args
            check (result.Contains "Error")
        })

    ("handleSquadUpdate empty title rejected — DAG unchanged", fun () ->
        promise {
            let rt = mkRuntime ()
            let events = box [| createObj [
                "type", box "task_created"
                "taskId", box "squad-a1b2"
                "title", box ""
                "description", box "Desc A"
                "dependsOn", box [||]
            ] |]
            let args = createObj [ "events", box events ]
            let! result = handleSquadUpdate rt args
            check (result.Contains "Error")
            check (result.Contains "non-empty")
            check (rt.Dag.Tasks.IsEmpty)
        })

    ("handleSquadUpdate empty description rejected — DAG unchanged", fun () ->
        promise {
            let rt = mkRuntime ()
            let events = box [| createObj [
                "type", box "task_created"
                "taskId", box "squad-a1b2"
                "title", box "Task A"
                "description", box ""
                "dependsOn", box [||]
            ] |]
            let args = createObj [ "events", box events ]
            let! result = handleSquadUpdate rt args
            check (result.Contains "Error")
            check (result.Contains "non-empty")
            check (rt.Dag.Tasks.IsEmpty)
        })

    ("handleSquadUpdate dangling deps returns dependency error", fun () ->
        promise {
            let rt = mkRuntime ()
            let events = box [| createObj [
                "type", box "task_created"
                "taskId", box "squad-a1b2"
                "title", box "Task A"
                "description", box "Desc A"
                "dependsOn", box [| "squad-zzzz" |]
            ] |]
            let args = createObj [ "events", box events ]
            let! result = handleSquadUpdate rt args
            check (result.Contains "squad-a1b2")
            check (result.Contains "squad-zzzz")
        })

    ("handleSquadUpdate cycle returns cycle detected", fun () ->
        promise {
            let rt = mkRuntime ()
            let events = box [|
                createObj [
                    "type", box "task_created"
                    "taskId", box "squad-a1b2"
                    "title", box "Task A"
                    "description", box "desc A"
                    "dependsOn", box [| "squad-c3d4" |]
                ]
                createObj [
                    "type", box "task_created"
                    "taskId", box "squad-c3d4"
                    "title", box "Task B"
                    "description", box "desc B"
                    "dependsOn", box [| "squad-a1b2" |]
                ]
            |]
            let args = createObj [ "events", box events ]
            let! result = handleSquadUpdate rt args
            check (result.Contains "cycle")
        })

    ("handleSquadUpdate success returns YAML frontmatter", fun () ->
        promise {
            let rt = mkRuntime ()
            let events = box [|
                createObj [
                    "type", box "task_created"
                    "taskId", box "squad-a1b2"
                    "title", box "Task A"
                    "description", box "Desc A"
                    "dependsOn", box [||]
                ]
                createObj [
                    "type", box "task_created"
                    "taskId", box "squad-c3d4"
                    "title", box "Task B"
                    "description", box "Desc B"
                    "dependsOn", box [| "squad-a1b2" |]
                ]
            |]
            let args = createObj [ "events", box events ]
            let! result = handleSquadUpdate rt args
            check (result.StartsWith "---")
            check (result.Contains "squad_event: tasks_created")
            check (result.Contains "squad-a1b2")
            check (result.Contains "squad-c3d4")
        })

    ("mkRuntime produces independent GitQueue and InjectQueue", fun () ->
        promise {
            let rt = mkRuntime ()
            let same = obj.ReferenceEquals(box rt.GitQueue, box rt.InjectQueue)
            check (not same)
        })

    // ══════════════════════════════════════════════════════════════════════════
    // New — cancel-only: events contain only squad_cancelled → result is
    // squad_cancelled event text, NOT tasks_created.
    // ══════════════════════════════════════════════════════════════════════════

    ("handleSquadUpdate cancel-only returns squad_cancelled not tasks_created", fun () ->
        promise {
            let rt = mkRuntime ()
            let events = box [| createObj [
                "type", box "squad_cancelled"
            ] |]
            let args = createObj [ "events", box events ]
            rt.MasterSessionId <- "squad-session-001"
            let! result = handleSquadUpdate rt args
            check (result.Contains "cancelled")
            check (not (result.Contains "tasks_created"))
            check (not (result.StartsWith "---"))
        })

    // ══════════════════════════════════════════════════════════════════════════
    // New — replayFromHistory: encoded history + git reconcile → Submitted
    // task upgraded to Merged.  Uses stubDeps + record overrides; no mkFake.
    // ══════════════════════════════════════════════════════════════════════════

    ("replayFromHistory Submitted task with git reconcile → Merged", fun () ->
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
            let history = [
                encodeEvent evtSquadCreated
                encodeEvent evtTasksCreated
                encodeEvent evtTaskStarted
                encodeEvent evtTaskSubmitted ]
            let deps2 = { deps with ReadAllTexts = fun _ _ _ -> Promise.lift history }
            let rt = mkRuntimeWithDeps deps2
            rt.MasterSessionId <- "squad-session-001"
            rt.GitError       <- None
            do! replayFromHistory rt
            match findTask "squad-a1b2" rt.Dag with
            | None -> check false
            | Some t ->
                check (t.Status = Merged)
                check (t.MergedSha = Some "merged-sha")
        })
]
