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

    ("Codec.SquadCreated round-trip includes requirement", fun () ->
        let encoded = encodeEvent (SquadCreated ("s1", "decompose this requirement"))
        check (encoded.Contains "decompose this requirement")
        match decodeEvent encoded with
        | Some (SquadCreated (_, req)) ->
            equal "decompose this requirement" req
        | _ -> check false)

    ("Codec.TaskStarted round-trip includes worktree_path and branch_name", fun () ->
        let encoded = encodeEvent (TaskStarted ("s1", "t1", "/wt/path", "branch-x"))
        check (encoded.Contains "/wt/path")
        check (encoded.Contains "branch-x")
        match decodeEvent encoded with
        | Some (TaskStarted (_, tid, wt, branch)) ->
            equal "t1" tid
            equal "/wt/path" wt
            equal "branch-x" branch
        | _ -> check false)

    ("Codec.TaskSubmitted round-trip includes commit_sha", fun () ->
        let encoded = encodeEvent (TaskSubmitted ("s1", "t1", "abc123sha"))
        check (encoded.Contains "abc123sha")
        match decodeEvent encoded with
        | Some (TaskSubmitted (_, tid, sha)) ->
            equal "t1" tid
            equal "abc123sha" sha
        | _ -> check false)

    ("Codec.TaskDone round-trip merged=true", fun () ->
        let encoded = encodeEvent (TaskDone ("s1", "t1", true))
        match decodeEvent encoded with
        | Some (TaskDone (_, _, merged)) ->
            equal true merged
        | _ -> check false)

    ("Codec.TaskDone round-trip merged=false", fun () ->
        let encoded = encodeEvent (TaskDone ("s1", "t1", false))
        match decodeEvent encoded with
        | Some (TaskDone (_, _, merged)) ->
            equal false merged
        | _ -> check false)

    ("Codec.SquadCancelled round-trip", fun () ->
        let encoded = encodeEvent (SquadCancelled "s1")
        match decodeEvent encoded with
        | Some (SquadCancelled sid) ->
            equal "s1" sid
        | _ -> check false)

    ("Codec.TaskCreated with description", fun () ->
        let encoded = encodeEvent (TaskCreated ("s1", "t1", "my title", "full description here", []))
        match decodeEvent encoded with
        | Some (TaskCreated (_, _, _, desc, deps)) ->
            equal "full description here" desc
            equal [] deps
        | _ -> check false)

    ("Codec.corrupted frontmatter no trailing ---", fun () ->
        isNone (decodeEvent "---\nsquad_event: task_created\nsession_id: s1"))

    ("Codec.TaskMerged prose includes sha", fun () ->
        let prose = eventProse (TaskMerged ("s1", "t1", "sha999"))
        check (prose.Contains "sha999"))
]
