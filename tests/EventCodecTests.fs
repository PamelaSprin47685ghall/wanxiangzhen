module Wanxiangzhen.Tests.EventCodecTests

open Wanxiangzhen.Kernel.SquadEvent
open Wanxiangzhen.Shell.EventCodec
open Wanxiangzhen.Tests.Assert

let entries () : (string * (unit -> unit)) list = [
    ("Codec.TaskCreated round-trip", fun () ->
        let e = { Type = TaskCreated; SessionId = "s1"; TaskId = Some "a1";
                  Title = Some "title"; Description = Some "desc"
                  DependsOn = Some []; WorktreePath = None; BranchName = None
                  SlavePid = None; CommitSha = None; MasterSha = None; Merged = None }
        let encoded = encodeEvent e
        check (encoded.StartsWith "---")
        match decodeEvent encoded with
        | Some d ->
            equal TaskCreated d.Type
            equal "s1" d.SessionId
            equal (Some "a1") d.TaskId
            equal (Some "title") d.Title
        | None -> check false)

    ("Codec.TaskMerged round-trip", fun () ->
        let e = { Type = TaskMerged; SessionId = "s1"; TaskId = Some "a1"
                  Title = None; Description = None; DependsOn = None
                  WorktreePath = None; BranchName = None; SlavePid = None
                  CommitSha = None; MasterSha = Some "sha123"; Merged = None }
        let encoded = encodeEvent e
        match decodeEvent encoded with
        | Some d ->
            equal TaskMerged d.Type
            equal (Some "sha123") d.MasterSha
        | None -> check false)

    ("Codec.TaskCreated with deps", fun () ->
        let e = { Type = TaskCreated; SessionId = "s1"; TaskId = Some "a1";
                  Title = Some "t"; Description = None
                  DependsOn = Some ["b1"; "c1"]
                  WorktreePath = None; BranchName = None; SlavePid = None
                  CommitSha = None; MasterSha = None; Merged = None }
        let encoded = encodeEvent e
        match decodeEvent encoded with
        | Some d ->
            equal (Some ["b1"; "c1"]) d.DependsOn
        | None -> check false)

    ("Codec.no frontmatter", fun () ->
        isNone (decodeEvent "just some text"))

    ("Codec.empty string", fun () ->
        isNone (decodeEvent ""))
]
