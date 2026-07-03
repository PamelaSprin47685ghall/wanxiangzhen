module Wanxiangzhen.Tests.SquadEventLogFsTests

open Fable.Core
open Fable.Core.JsInterop
open Wanxiangzhen.Kernel.SquadEvent
open Wanxiangzhen.Shell.SquadEventLogCodec
open Wanxiangzhen.Shell.SquadEventLogFiles
open Wanxiangzhen.Tests.Assert

[<Import("mkdtempSync", "node:fs")>]
let private mkdtempSync (template: string) : string = jsNative

[<Import("rmSync", "node:fs")>]
let private rmSync (path: string) (opts: obj) : unit = jsNative

[<Import("writeFileSync", "node:fs")>]
let private writeFileSync (path: string) (data: string) : unit = jsNative

let private withTempDir (f: string -> JS.Promise<unit>) : JS.Promise<unit> =
    promise {
        let dir = mkdtempSync "wanxiangzhen-el-"
        try do! f dir
        finally rmSync dir (createObj [ "recursive", box true; "force", box true ])
    }

let entriesAsync () : (string * (unit -> JS.Promise<unit>)) list = [
    ("SquadEventLogFs.append and read round-trip", fun () ->
        withTempDir (fun dir ->
            promise {
                let store = SquadEventLogStore dir
                let ev = SquadCreated ("s1", "req")
                let! w = store.AppendEvent "t1" ev
                match w with
                | Error e -> failwith e
                | Ok () -> ()
                let! events = store.ReadAllEvents()
                equal 1 events.Length
                match events.[0] with
                | SquadCreated (sid, req) ->
                    equal "s1" sid
                    equal "req" req
                | _ -> check "" false
            }))

    ("SquadEventLogFs.truncate on corrupt tail line", fun () ->
        withTempDir (fun dir ->
            promise {
                let good = squadEventToLine "t" (TasksCreated ("s1", [ ("a", "t", "d", []) ]))
                let bad = "{not-json"
                writeFileSync (eventPath dir) (good + "\n" + bad + "\n")
                let store = SquadEventLogStore dir
                let! events = store.ReadAllEvents()
                equal 1 events.Length
            }))
]