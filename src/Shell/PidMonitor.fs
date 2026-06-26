module Wanxiangzhen.Shell.PidMonitor

open Fable.Core

[<Emit("process.kill($0, $1)")>]
let private kill (pid: int) (signal: obj) : unit = jsNative

let isPidAlive (pid: int) : bool =
    try
        kill pid (box 0)
        true
    with ex ->
        let msg = string (ex.Message)
        msg.Contains "ESRCH" |> not

[<Emit("setInterval($0, $1)")>]
let private setInterval_ (f: unit -> unit) (ms: int) : obj = jsNative

[<Emit("clearInterval($0)")>]
let private clearInterval_ (handle: obj) : unit = jsNative

let startPolling (intervalMs: int) (check: unit -> unit) : obj =
    setInterval_ check intervalMs

let stopPolling (handle: obj) : unit =
    clearInterval_ handle
