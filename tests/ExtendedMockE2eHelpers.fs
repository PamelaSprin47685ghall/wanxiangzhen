module Wanxiangzhen.Tests.ExtendedMockE2eHelpers

open Fable.Core
open Fable.Core.JsInterop
open Wanxiangzhen.Plugin
open Wanxiangzhen.Kernel.Task
open Wanxiangzhen.Kernel.Dag
open Wanxiangzhen.Kernel.SquadConfig
open Wanxiangzhen.Kernel.SquadEvent
open Wanxiangzhen.Shell.Dyn
open Wanxiangzhen.Shell.CoordinatorRuntime
open Wanxiangzhen.Shell.CoordinatorOps
open Wanxiangzhen.Shell.CoordinatorLifecycle
open Wanxiangzhen.Shell.HttpCodec
open Wanxiangzhen.Shell.SerialQueue
open Wanxiangzhen.Shell.HttpServer
open Wanxiangzhen.Tests.Assert
open Wanxiangzhen.Tests.TestDoubles
open Wanxiangzhen.Tests.SpinWait

let mkRunningTaskServer () =
    promise {
        let s    = mkFake ()
        let deps = mkDeps s
        let rt   = mkRuntime deps
        rt.MasterSessionId <- "squad-session-001"

        let evts  = [| mkTaskEvent "squad-a1b2" "A" "desc A" [] |]
        let args  = mkSquadUpdateArgs evts
        rt.Scheduling <- true
        let! _    = handleSquadUpdate rt args
        rt.Scheduling <- false
        do! schedulerTick rt
        let! _ = routeHandler rt "POST" "/task/squad-a1b2/register" (createObj [ "pid", box 111 ])
        let! server = startServer rt.Token (routeHandler rt)
        return s, rt, server
    }

let waitUntil (predicate: unit -> bool) (_timeoutMs: int) : JS.Promise<unit> =
    spinUntilFail predicate 500
