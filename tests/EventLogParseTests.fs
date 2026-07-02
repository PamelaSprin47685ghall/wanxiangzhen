module Wanxiangzhen.Tests.EventLogParseTests

open Wanxiangzhen.Kernel.EventLog.Parse
open Wanxiangzhen.Tests.Assert

let entries () : (string * (unit -> unit)) list = [
    ("Parse.truncate on bad line", fun () ->
        let parse s = if s = "ok" then Some 1 else None
        equal [ 1 ] (parseLinesWithTruncate parse [ "ok"; "bad"; "ok" ]))

    ("Parse.skip empty lines", fun () ->
        let parse s = Some s
        equal [ "a"; "b" ] (parseLinesWithTruncate parse [ ""; "  "; "a"; "b" ]))
]