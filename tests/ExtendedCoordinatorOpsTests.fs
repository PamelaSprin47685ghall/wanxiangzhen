module Wanxiangzhen.Tests.ExtendedCoordinatorOpsTests

open Fable.Core
open Fable.Core.JsInterop
open Wanxiangzhen.Kernel.Dag
open Wanxiangzhen.Shell.CoordinatorRuntime
open Wanxiangzhen.Shell.SerialQueue
open Wanxiangzhen.Shell.CoordinatorOps
open Wanxiangzhen.Tests.Assert
open Wanxiangzhen.Tests.TestFixtures

// ══════════════════════════════════════════════════════════════════════════════
// Tests — pure CoordinatorOps unit tests, no git/spawn/session paths exercised
// ══════════════════════════════════════════════════════════════════════════════

let entries () : (string * (unit -> unit)) list = [
    ("extractTaskId returns id for submit path", fun () ->
        let id = extractTaskId "/task/squad-a1b2/submit" "submit"
        equal "squad-a1b2" id)

    ("extractTaskId returns id for register path", fun () ->
        let id = extractTaskId "/task/x/register" "register"
        equal "x" id)

    ("extractTaskId returns id for done path", fun () ->
        let id = extractTaskId "/task/y/done" "done"
        equal "y" id)

    ("extractTaskId returns empty for unrelated path", fun () ->
        let id = extractTaskId "/unrelated" "submit"
        equal "" id)

    ("extractTaskId returns empty for non-matching suffix", fun () ->
        let id = extractTaskId "/task/x/submit" "register"
        equal "" id)

    ("extractTaskId handles short task IDs", fun () ->
        let id = extractTaskId "/task/a/done" "done"
        equal "a" id)

    ("formatDagText returns empty-DAG text", fun () ->
        let rt = mkRuntime ()
        let text = formatDagText rt
        check (text.Contains "no tasks"))

    ("startPidPolling records handle without crashing", fun () ->
        let rt = mkRuntime ()
        startPidPolling rt
        check (rt.PidPollHandle.IsSome))
]
