module Wanxiangzhen.Tests.EventCodecTests

open Wanxiangzhen.Kernel.SquadEvent
open Wanxiangzhen.Shell.EventCodec
open Wanxiangzhen.Tests.Assert

let entries () : (string * (unit -> unit)) list = [
    ("Codec.TasksCreated round-trip", fun () ->
        let tasks = [("a1", "title1", "desc1", []); ("a2", "title2", "desc2", ["a1"])]
        let encoded = encodeEvent (TasksCreated ("s1", tasks))
        check (encoded.StartsWith "---")
        match decodeEvent encoded with
        | Some (TasksCreated (_, decoded)) ->
            equal 2 decoded.Length
            equal "a1" (decoded.[0] |> fun (id,_,_,_) -> id)
            equal "title1" (decoded.[0] |> fun (_,t,_,_) -> t)
            equal ["a1"] (decoded.[1] |> fun (_,_,_,d) -> d)
        | _ -> check false)

    ("Codec.TaskMerged round-trip", fun () ->
        let encoded = encodeEvent (TaskMerged ("s1", "a1", "sha123"))
        match decodeEvent encoded with
        | Some (TaskMerged (_, _, sha)) ->
            equal "sha123" sha
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

    ("Codec.corrupted frontmatter no trailing ---", fun () ->
        isNone (decodeEvent "---\nsquad_event: tasks_created\nsession_id: s1"))

    ("Codec.TaskMerged prose includes sha", fun () ->
        let prose = eventProse (TaskMerged ("s1", "t1", "sha999"))
        check (prose.Contains "sha999"))
]
