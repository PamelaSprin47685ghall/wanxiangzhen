module Wanxiangzhen.Tests.ExtendedCoordinatorOpsTests

open Fable.Core
open Fable.Core.JsInterop
open Wanxiangzhen.Kernel.Task
open Wanxiangzhen.Kernel.Dag
open Wanxiangzhen.Shell.Dyn
open Wanxiangzhen.Shell.CoordinatorRuntime
open Wanxiangzhen.Shell.CoordinatorOps
open Wanxiangzhen.Tests.Assert

let private mkRuntime () : CoordinatorRuntime =
    { Dag = Wanxiangzhen.Kernel.Dag.empty "" ""
      Sessions = Map.empty
      Config = Wanxiangzhen.Kernel.SquadConfig.defaults
      MasterBranch = "main"
      ProjectRoot = "/tmp"
      MasterSessionId = ""
      Client = createObj []
      Token = "t"
      CoordinatorUrl = "http://l:0"
      GitQueue = Wanxiangzhen.Shell.SerialQueue.SerialQueue ()
      InjectQueue = Wanxiangzhen.Shell.SerialQueue.SerialQueue ()
      Server = { Port = 0; Url = ""; Close = fun () -> () }
      Scheduling = false
      PidPollHandle = None
      GitError = None }

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
