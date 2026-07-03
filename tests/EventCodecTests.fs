module Wanxiangzhen.Tests.EventCodecTests

open Wanxiangzhen.Kernel.SquadEvent
open Wanxiangzhen.Shell.EventCodec
open Wanxiangzhen.Tests.Assert

let entries () : (string * (unit -> unit)) list = [
    ("Codec.TasksCreated round-trip", fun () ->
        let tasks = [("a1", "title1", "desc1", []); ("a2", "title2", "desc2", ["a1"])]
        let encoded = encodeEvent (TasksCreated ("s1", tasks))
        checkBare (encoded.StartsWith "---")
        match decodeEvent encoded with
        | Some (TasksCreated (_, decoded)) ->
            equal 2 decoded.Length
            equal "a1" (decoded.[0] |> fun (id,_,_,_) -> id)
            equal "title1" (decoded.[0] |> fun (_,t,_,_) -> t)
            equal ["a1"] (decoded.[1] |> fun (_,_,_,d) -> d)
        | _ -> checkBare false)

    ("Codec.TaskMerged round-trip", fun () ->
        let encoded = encodeEvent (TaskMerged ("s1", "a1", "sha123"))
        match decodeEvent encoded with
        | Some (TaskMerged (_, _, sha)) ->
            equal "sha123" sha
        | _ -> checkBare false)

    ("Codec.no frontmatter", fun () ->
        isNone (decodeEvent "just some text"))

    ("Codec.empty string", fun () ->
        isNone (decodeEvent ""))

    ("Codec.SquadCreated round-trip includes requirement", fun () ->
        let encoded = encodeEvent (SquadCreated ("s1", "decompose this requirement"))
        checkBare (encoded.Contains "decompose this requirement")
        match decodeEvent encoded with
        | Some (SquadCreated (_, req)) ->
            equal "decompose this requirement" req
        | _ -> checkBare false)

    ("Codec.TaskStarted round-trip includes worktree_path and branch_name", fun () ->
        let encoded = encodeEvent (TaskStarted ("s1", "t1", "/wt/path", "branch-x"))
        checkBare (encoded.Contains "/wt/path")
        checkBare (encoded.Contains "branch-x")
        match decodeEvent encoded with
        | Some (TaskStarted (_, tid, wt, branch)) ->
            equal "t1" tid
            equal "/wt/path" wt
            equal "branch-x" branch
        | _ -> checkBare false)

    ("Codec.TaskSubmitted round-trip includes commit_sha", fun () ->
        let encoded = encodeEvent (TaskSubmitted ("s1", "t1", "abc123sha"))
        checkBare (encoded.Contains "abc123sha")
        match decodeEvent encoded with
        | Some (TaskSubmitted (_, tid, sha)) ->
            equal "t1" tid
            equal "abc123sha" sha
        | _ -> checkBare false)

    ("Codec.TaskDone round-trip merged=true", fun () ->
        let encoded = encodeEvent (TaskDone ("s1", "t1", true))
        match decodeEvent encoded with
        | Some (TaskDone (_, _, merged)) ->
            equal true merged
        | _ -> checkBare false)

    ("Codec.TaskDone round-trip merged=false", fun () ->
        let encoded = encodeEvent (TaskDone ("s1", "t1", false))
        match decodeEvent encoded with
        | Some (TaskDone (_, _, merged)) ->
            equal false merged
        | _ -> checkBare false)

    ("Codec.SquadCancelled round-trip", fun () ->
        let encoded = encodeEvent (SquadCancelled "s1")
        match decodeEvent encoded with
        | Some (SquadCancelled sid) ->
            equal "s1" sid
        | _ -> checkBare false)

    ("Codec.corrupted frontmatter no trailing ---", fun () ->
        isNone (decodeEvent "---\nsquad_event: tasks_created\nsession_id: s1"))

    ("Codec.TaskMerged prose includes sha", fun () ->
        let prose = eventProse (TaskMerged ("s1", "t1", "sha999"))
        checkBare (prose.Contains "sha999"))

    ("Codec.multi frontmatter decodes first event", fun () ->
        let ev1 = SquadCreated ("s1", "req one")
        let ev2 = TasksCreated ("s2", [("t1", "title1", "desc1", [])])
        let combined = encodeEvent ev1 + "\n" + encodeEvent ev2
        match decodeEvent combined with
        | Some (SquadCreated (_, req)) ->
            equal "req one" req
        | _ -> checkBare false)

    ("Codec.encodeEvents two events has two frontmatter blocks", fun () ->
        let ev1 = SquadCreated ("s1", "req one")
        let ev2 = TasksCreated ("s2", [("t1", "title1", "desc1", [])])
        let encoded = encodeEvents [ev1; ev2]
        let eventLines = encoded.Split('\n') |> Array.filter (fun l -> l.StartsWith "squad_event:")
        equal 2 eventLines.Length
        checkBare (encoded.Contains "squad_event: squad_created")
        checkBare (encoded.Contains "squad_event: tasks_created"))

    ("Codec.decodeEvents parses multiple frontmatter events", fun () ->
        let ev1 = SquadCreated ("s1", "req one")
        let ev2 = TasksCreated ("s2", [("t1", "title1", "desc1", [])])
        let combined = encodeEvent ev1 + "\n" + encodeEvent ev2
        let decoded = decodeEvents combined
        equal 2 decoded.Length
        match (decoded.[0], decoded.[1]) with
        | SquadCreated (_, req), TasksCreated (_, tasks) ->
            equal "req one" req
            equal 1 tasks.Length
            equal "t1" (tasks.[0] |> fun (id,_,_,_) -> id)
        | _ -> checkBare false)

    ("Codec.decodeEvents skips unrecognized blocks", fun () ->
        let preamble = "This is just a paragraph with no frontmatter.\n\n"
        let ev1 = SquadCreated ("s1", "req one")
        let ev2 = TaskMerged ("s1", "t1", "sha999")
        let combined = preamble + encodeEvent ev1 + "\n" + encodeEvent ev2
        let decoded = decodeEvents combined
        equal 2 decoded.Length
        match (decoded.[0], decoded.[1]) with
        | SquadCreated (_, req), TaskMerged (_, _, sha) ->
            equal "req one" req
            equal "sha999" sha
        | _ -> checkBare false)

    ("Codec.decodeEvents empty string returns empty list", fun () ->
        let decoded = decodeEvents ""
        equal 0 decoded.Length)

    ("Codec.encodeEvents/decodeEvents round-trip", fun () ->
        let events = [
            SquadCreated ("s1", "req one")
            TasksCreated ("s1", [("t1", "title1", "desc1", []); ("t2", "title2", "desc2", ["t1"])])
            TaskStarted ("s1", "t1", "/wt/path", "branch-x")
            TaskSubmitted ("s1", "t1", "abc123")
            TaskMerged ("s1", "t1", "sha999")
            TaskDone ("s1", "t1", true)
            SquadCancelled "s1"
        ]
        let encoded = encodeEvents events
        let decoded = decodeEvents encoded
        equal events.Length decoded.Length
        List.iter2 (fun e1 e2 -> equal e1 e2) events decoded)
]
