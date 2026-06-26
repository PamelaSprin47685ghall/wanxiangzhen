module Wanxiangzhen.Tests.EventCodecTests

open Wanxiangzhen.Kernel.SquadEvent
open Wanxiangzhen.Shell.EventCodec
open Wanxiangzhen.Tests.Assert

let entries () : (string * (unit -> unit)) list = [
    ("Codec.TaskCreated round-trip", fun () ->
        let encoded = encodeEvent (TaskCreated ("s1", "a1", "title", "desc", []))
        check (encoded.StartsWith "---")
        match decodeEvent encoded with
        | Some (TaskCreated (_, tid, title, _, deps)) ->
            equal "a1" tid
            equal "title" title
        | _ -> check false)

    ("Codec.TaskMerged round-trip", fun () ->
        let encoded = encodeEvent (TaskMerged ("s1", "a1", "sha123"))
        match decodeEvent encoded with
        | Some (TaskMerged (_, _, sha)) ->
            equal "sha123" sha
        | _ -> check false)

    ("Codec.TaskCreated with deps", fun () ->
        let encoded = encodeEvent (TaskCreated ("s1", "a1", "t", "d", ["b1"; "c1"]))
        match decodeEvent encoded with
        | Some (TaskCreated (_, _, _, _, deps)) ->
            equal ["b1"; "c1"] deps
        | _ -> check false)

    ("Codec.no frontmatter", fun () ->
        isNone (decodeEvent "just some text"))

    ("Codec.empty string", fun () ->
        isNone (decodeEvent ""))
]
