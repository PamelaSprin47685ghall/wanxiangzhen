module Wanxiangzhen.Tests.StateBackupTests

open Fable.Core
open Fable.Core.JsInterop
open Wanxiangzhen.Kernel.Task
open Wanxiangzhen.Shell.StateBackup
open Wanxiangzhen.Shell.Dyn
open Wanxiangzhen.Tests.Assert

[<Import("mkdtempSync", "node:fs")>]
let private mkdtemp (prefix: string) : string = jsNative

[<Import("readFileSync", "node:fs")>]
let private readFile (path: string) (enc: string) : string = jsNative

[<Import("existsSync", "node:fs")>]
let private existsSync (path: string) : bool = jsNative

[<Import("writeFileSync", "node:fs")>]
let private writeFile (path: string) (data: string) : unit = jsNative

[<Import("mkdirSync", "node:fs")>]
let private mkdirSync (path: string) : unit = jsNative

[<Import("rmSync", "node:fs")>]
let private rmSync (path: string) (opts: obj) : unit = jsNative

[<Import("join", "node:path")>]
let private pathJoin (a: string) (b: string) : string = jsNative

let private cleanup (dir: string) : unit =
    try rmSync dir (createObj [ "recursive", box true; "force", box true ]) with _ -> ()

let private mkRuntime (dir: string) (dag: Wanxiangzhen.Kernel.Dag.Dag) : Wanxiangzhen.Shell.CoordinatorRuntime.CoordinatorRuntime =
    { Dag = dag
      Sessions = Map.empty
      Config = Wanxiangzhen.Kernel.SquadConfig.defaults
      MasterBranch = "main"
      ProjectRoot = dir
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
    ("detectVibeFs empty directory returns false", fun () ->
        equal false (detectVibeFs ""))

    ("detectVibeFs non-empty directory without wanxiangshu returns false", fun () ->
        equal false (detectVibeFs "/some/project/root"))

    ("saveState creates .squad/state.json with sessionId and requirement", fun () ->
        let temp = mkdtemp "wanxiangzhen-test-"
        try
            let rt = mkRuntime temp (Wanxiangzhen.Kernel.Dag.empty "session-1" "test requirement")
            saveState rt
            let statePath = pathJoin temp ".squad/state.json"
            check (existsSync statePath)
            let content = readFile statePath "utf-8"
            check (content.Contains "session-1")
            check (content.Contains "test requirement")
        finally
            cleanup temp)

    ("loadStateFallback with existing state.json restores tasks", fun () ->
        let temp = mkdtemp "wanxiangzhen-test-"
        try
            mkdirSync (pathJoin temp ".squad")
            let stateJson = """{"sessionId":"s1","requirement":"req","tasks":[{"id":"squad-a1b2","title":"Task A","description":"desc","dependsOn":[],"status":"pending","sessionId":"s1"}]}"""
            writeFile (pathJoin temp ".squad/state.json") stateJson
            let rt = mkRuntime temp (Wanxiangzhen.Kernel.Dag.empty "" "")
            loadStateFallback rt
            equal 1 (rt.Dag.Tasks.Count)
            let task = rt.Dag.Tasks.["squad-a1b2"]
            equal "Task A" task.Title
            equal "pending" (Wanxiangzhen.Kernel.Task.statusToString task.Status)
        finally
            cleanup temp)
]
