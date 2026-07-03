module Wanxiangzhen.Tests.ExtendedMockE2ePluginTests

open Fable.Core
open Fable.Core.JsInterop
open Wanxiangzhen.Kernel.Task
open Wanxiangzhen.Kernel.Dag
open Wanxiangzhen.Kernel.SquadConfig
open Wanxiangzhen.Kernel.SquadEvent
open Wanxiangzhen.Shell.Dyn
open Wanxiangzhen.Shell.CoordinatorRuntime
open Wanxiangzhen.Shell.CoordinatorLifecycle
open Wanxiangzhen.Shell.CoordinatorOps
open Wanxiangzhen.Shell.HttpCodec
open Wanxiangzhen.Shell.SerialQueue
open Wanxiangzhen.Shell.HttpServer
open Wanxiangzhen.Plugin
open Wanxiangzhen.Tests.Assert
open Wanxiangzhen.Tests.TestDoubles

let testMultiSessionSquadCommandSavesPrevious () : JS.Promise<unit> =
    promise {
        let s    = mkFake ()
        let deps = mkDeps s
        let rt   = mkRuntime deps
        rt.MasterSessionId <- "squad-session-001"

        let evts1 = [| mkTaskEvent "squad-a1b2" "A" "desc A" [] |]
        let args1 = mkSquadUpdateArgs evts1
        rt.Scheduling <- true
        let! _    = handleSquadUpdate rt args1
        rt.Scheduling <- false
        do! schedulerTick rt

        let session1 = rt.Dag.SessionId
        checkBare (session1 <> "")

        let input  = createObj [ "command", box "squad"; "sessionID", box "squad-session-002"; "arguments", box "req2" ]
        let output = createObj [ "parts", box (System.Collections.Generic.List<obj>()) ]
        do! handleCommandExecuteBefore rt input output

        let evts2 = [| mkTaskEvent "squad-c3d4" "B" "desc B" [] |]
        let args2 = mkSquadUpdateArgs evts2
        rt.Scheduling <- true
        let! _    = handleSquadUpdate rt args2

        checkBare (rt.Sessions.ContainsKey session1)
        let savedDag = rt.Sessions.[session1]
        checkBare (savedDag.Tasks.ContainsKey "squad-a1b2")
    }

let testDisposeHookClosesServerAndStopsPolling () : JS.Promise<unit> =
    promise {
        let s    = mkFake ()
        let deps = mkDeps s
        let rt   = mkRuntime deps

        let mutable closed = false
        let mutable stopped = false
        let rt = { mkRuntime deps with Server = { Port = 12345; Url = "http://127.0.0.1:12345"; Close = fun () -> closed <- true } }
        let _ = startPidPolling rt
        checkBare (rt.PidPollHandle.IsSome)
        s.stopPollingOverride <- Some (fun h -> stopped <- true)

        let dispose () : JS.Promise<unit> =
            promise {
                rt.Server.Close ()
                rt.PidPollHandle |> Option.iter (fun h -> deps.StopPolling h)
            }

        do! dispose ()

        checkBare closed
        checkBare stopped
    }

let testRealisticOpencodePluginInputMock () : JS.Promise<unit> =
    promise {
        let s    = mkFake ()
        let deps = mkDeps s
        let rt   = mkRuntime deps

        let mockClient = createObj [
            "session", box (createObj [
                "prompt",   box (System.Func<obj, JS.Promise<unit>>(fun _ -> Promise.lift ()))
                "messages", box (System.Func<obj, JS.Promise<obj>>(fun _ -> Promise.lift (createObj [ "data", box [||] ])))
                "command",  box (System.Func<obj, JS.Promise<obj>>(fun _ -> Promise.lift (createObj [])))
                "create",   box (System.Func<obj, JS.Promise<obj>>(fun _ -> Promise.lift (createObj [])))
            ])
            "event", box (createObj [
                "subscribe", box (System.Func<obj, JS.Promise<unit>>(fun _ -> Promise.lift ()))
            ])
        ]
        let mockCtx = createObj [
            "client",     box mockClient
            "directory",  box "/tmp/test-project"
            "worktree",   box "/tmp/test-project"
            "serverUrl",  box "http://localhost:0"
        ]

        s.revParseBranchOverride <- Some (fun c -> "main")

        let! result = pluginWithDeps mockCtx deps

        let hooks = get result "hooks"
        checkBare (not (isNullish hooks))
        checkBare (not (isNullish (get hooks "tool")))
        checkBare (not (isNullish (get hooks "config")))
        checkBare (not (isNullish (get hooks "command.execute.before")))
        checkBare (not (isNullish (get hooks "chat.message")))
        checkBare (not (isNullish (get hooks "dispose")))

        let runtime = get result "runtime"
        checkBare (not (isNullish runtime))

        let tools = get hooks "tool"
        checkBare (not (isNullish (get tools "squad_update")))

        let cfg = get hooks "config"
        checkBare (not (isNullish cfg))
    }

let entriesAsync () : (string * (unit -> JS.Promise<unit>)) list = [
    ("ExtendedMockE2e.multi_session_squad_command_saves_previous",
     testMultiSessionSquadCommandSavesPrevious)

    ("ExtendedMockE2e.dispose_hook_closes_server_and_stops_polling",
     testDisposeHookClosesServerAndStopsPolling)

    ("ExtendedMockE2e.realistic_opencode_plugin_input_mock",
     testRealisticOpencodePluginInputMock)
]
