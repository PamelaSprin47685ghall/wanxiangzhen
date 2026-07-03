module Wanxiangzhen.Tests.PidMonitorTests

open Fable.Core
open Fable.Core.JsInterop
open Wanxiangzhen.Shell.PidMonitor
open Wanxiangzhen.Tests.Assert

[<Emit("process.pid")>]
let private selfPid : int = jsNative

let entries () : (string * (unit -> unit)) list = [
    ("PidMonitor.isPidAlive self returns true", fun () ->
        checkBare (isPidAlive selfPid))

    ("PidMonitor.isPidAlive invalid PID returns false", fun () ->
        equal false (isPidAlive 2147483647))

    ("PidMonitor.startPolling/stopPolling does not throw", fun () ->
        let handle = startPolling 1000 (fun () -> ())
        stopPolling handle
        checkBare true)
]
