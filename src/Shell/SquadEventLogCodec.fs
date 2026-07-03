module Wanxiangzhen.Shell.SquadEventLogCodec

open Fable.Core
open Fable.Core.JsInterop
open Wanxiangzhen.Kernel.SquadEvent
open Wanxiangzhen.Shell.Dyn

let eventLogFileName = ".wanxiangzhen.ndjson"
let lockFileName = ".wanxiangzhen.ndjson.lock"

[<Import("resolve", "node:path")>]
let resolvePath (cwd: string) (filePath: string) : string = jsNative

let eventPath (workspaceRoot: string) : string =
    resolvePath workspaceRoot eventLogFileName

let lockPath (workspaceRoot: string) : string =
    resolvePath workspaceRoot lockFileName

let private payloadTasks (tasks: (string * string * string * string list) list) : obj =
    let items = System.Collections.Generic.List<obj>()
    for (tid, title, desc, deps) in tasks do
        let o = createObj [ "task_id", box tid; "title", box title; "description", box desc ]
        if deps <> [] then setKey o "depends_on" (box (List.toArray deps))
        items.Add o
    box (items.ToArray())

let private parseTasks (raw: obj) : (string * string * string * string list) list =
    if isNullish raw || not (isArray raw) then []
    else
        (raw :?> obj array)
        |> Array.toList
        |> List.choose (fun o ->
            let tid = str o "task_id"
            if tid = "" then None
            else
                let title = str o "title"
                let desc = str o "description"
                let depsRaw = get o "depends_on"
                let deps =
                    if isNullish depsRaw || not (isArray depsRaw) then []
                    else (depsRaw :?> obj array) |> Array.map string |> Array.toList
                Some (tid, title, desc, deps))

let squadEventToLineObject (at: string) (e: SquadEvent) : obj =
    let kind = eventTypeName e
    let session = eventSessionId e
    let payload =
        match e with
        | SquadCreated (_, req) -> createObj [ "requirement", box req ]
        | TasksCreated (_, tasks) -> createObj [ "tasks", payloadTasks tasks ]
        | TaskStarted (_, tid, wt, branch) ->
            createObj [ "task_id", box tid; "worktree_path", box wt; "branch_name", box branch ]
        | TaskSubmitted (_, tid, sha) -> createObj [ "task_id", box tid; "commit_sha", box sha ]
        | TaskMerged (_, tid, sha) -> createObj [ "task_id", box tid; "master_sha", box sha ]
        | TaskDone (_, tid, merged) -> createObj [ "task_id", box tid; "merged", box merged ]
        | TaskError (_, tid, err) -> createObj [ "task_id", box tid; "error", box err ]
        | SquadCancelled _ -> createObj []
    createObj [| "v", box 1; "session", box session; "kind", box kind; "at", box at; "payload", payload |]

let squadEventToLine (at: string) (e: SquadEvent) : string =
    JS.JSON.stringify (squadEventToLineObject at e)

let tryParseLine (line: string) : SquadEvent option =
    let t = if isNull line then "" else line.Trim()
    if t = "" then None
    else
        try
            let o = unbox<obj> (JS.JSON.parse t)
            let kind = str o "kind"
            let sid = str o "session"
            let payload = get o "payload"
            if kind = "" || sid = "" then None
            else
                let optStr k =
                    if isNullish payload then None
                    else
                        let v = get payload k
                        if isNullish v then None else Some (string v)
                let req = optStr "requirement" |> Option.defaultValue ""
                match kind with
                | "squad_created" -> Some (SquadCreated (sid, req))
                | "tasks_created" ->
                    let tasks = if isNullish payload then [] else parseTasks (get payload "tasks")
                    Some (TasksCreated (sid, tasks))
                | "task_started" ->
                    Some (TaskStarted (sid, optStr "task_id" |> Option.defaultValue "", optStr "worktree_path" |> Option.defaultValue "", optStr "branch_name" |> Option.defaultValue ""))
                | "task_submitted" ->
                    Some (TaskSubmitted (sid, optStr "task_id" |> Option.defaultValue "", optStr "commit_sha" |> Option.defaultValue ""))
                | "task_merged" ->
                    Some (TaskMerged (sid, optStr "task_id" |> Option.defaultValue "", optStr "master_sha" |> Option.defaultValue ""))
                | "task_done" ->
                    let merged =
                        if isNullish payload then false
                        else
                            let v = get payload "merged"
                            if isNullish v then false
                            elif v :? bool then unbox<bool> v
                            else false
                    Some (TaskDone (sid, optStr "task_id" |> Option.defaultValue "", merged))
                | "task_error" ->
                    Some (TaskError (sid, optStr "task_id" |> Option.defaultValue "", optStr "error" |> Option.defaultValue ""))
                | "squad_cancelled" -> Some (SquadCancelled sid)
                | _ -> None
        with _ ->
            None