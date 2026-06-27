module Wanxiangzhen.Tests.CoordinatorLifecycleTests

open Fable.Core
open Fable.Core.JsInterop
open Wanxiangzhen.Kernel.Task
open Wanxiangzhen.Kernel.Dag
open Wanxiangzhen.Shell.Dyn
open Wanxiangzhen.Shell.SerialQueue
open Wanxiangzhen.Shell.CoordinatorRuntime
open Wanxiangzhen.Shell.CoordinatorLifecycle
open Wanxiangzhen.Tests.Assert

let private mkRuntime () : CoordinatorRuntime =
    { Dag = Wanxiangzhen.Kernel.Dag.empty "" ""
      Sessions = Map.empty
      Config = Wanxiangzhen.Kernel.SquadConfig.defaults
      MasterBranch = "main"
      ProjectRoot = "/tmp"
      MasterSessionId = ""
      Client = createObj []
      Token = "test-token"
      CoordinatorUrl = "http://localhost:0"
      GitQueue = SerialQueue ()
      InjectQueue = SerialQueue ()
      Server = { Port = 0; Url = ""; Close = fun () -> () }
      Scheduling = false
      PidPollHandle = None
      GitError = None }

let entries () : (string * (unit -> unit)) list = [
    ("handleSquadUpdate null events returns Error", fun () ->
        let rt = mkRuntime ()
        let result = handleSquadUpdate rt (box null)
        check (result.Contains "Error")
        check (result.Contains "events"))

    ("handleSquadUpdate non-array events returns Error", fun () ->
        let rt = mkRuntime ()
        let args = createObj [ "events", box "not-an-array" ]
        let result = handleSquadUpdate rt args
        check (result.Contains "Error"))

    ("handleSquadUpdate dangling deps returns dependency error", fun () ->
        let rt = mkRuntime ()
        let events = box [| createObj [
            "type", box "task_created"
            "taskId", box "squad-a1b2"
            "title", box "Task A"
            "description", box "Desc A"
            "dependsOn", box [| "squad-zzzz" |]
        ] |]
        let args = createObj [ "events", box events ]
        let result = handleSquadUpdate rt args
        check (result.Contains "squad-a1b2")
        check (result.Contains "squad-zzzz"))

    ("handleSquadUpdate cycle returns cycle detected", fun () ->
        let rt = mkRuntime ()
        let events = box [|
            createObj [
                "type", box "task_created"
                "taskId", box "squad-a1b2"
                "title", box "Task A"
                "description", box ""
                "dependsOn", box [| "squad-c3d4" |]
            ]
            createObj [
                "type", box "task_created"
                "taskId", box "squad-c3d4"
                "title", box "Task B"
                "description", box ""
                "dependsOn", box [| "squad-a1b2" |]
            ]
        |]
        let args = createObj [ "events", box events ]
        let result = handleSquadUpdate rt args
        check (result.Contains "cycle"))
]
