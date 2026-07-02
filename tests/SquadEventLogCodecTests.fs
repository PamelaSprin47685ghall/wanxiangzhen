module Wanxiangzhen.Tests.SquadEventLogCodecTests

open Wanxiangzhen.Kernel.SquadEvent
open Wanxiangzhen.Shell.SquadEventLogCodec
open Wanxiangzhen.Tests.Assert

let entries () : (string * (unit -> unit)) list = [
    ("SquadEventLog.TasksCreated round-trip", fun () ->
        let tasks = [ ("a1", "t", "d", [ "x" ]) ]
        let line = squadEventToLine "2025-01-01T00:00:00Z" (TasksCreated ("s1", tasks))
        match tryParseLine line with
        | Some (TasksCreated (sid, decoded)) ->
            equal "s1" sid
            equal 1 decoded.Length
            let (tid, _, _, deps) = decoded.[0]
            equal "a1" tid
            equal [ "x" ] deps
        | _ -> check false)

    ("SquadEventLog.squad_created round-trip", fun () ->
        let line = squadEventToLine "t" (SquadCreated ("s1", "req"))
        match tryParseLine line with
        | Some (SquadCreated (sid, req)) ->
            equal "s1" sid
            equal "req" req
        | _ -> check false)
]