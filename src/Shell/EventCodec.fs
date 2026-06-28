module Wanxiangzhen.Shell.EventCodec

open Fable.Core
open Fable.Core.JsInterop
open Wanxiangzhen.Kernel.SquadEvent
open Wanxiangzhen.Kernel.SquadPrompts
open Wanxiangzhen.Shell.Yaml
open Wanxiangzhen.Shell.Dyn

let private fmKey (e: SquadEvent) =
    let o = createObj [
        "squad_event", box (eventTypeName e)
        "session_id",  box (eventSessionId e)
    ]
    (match e with
    | TasksCreated (_, tasks) ->
        let items = System.Collections.Generic.List<obj>()
        for (tid, title, desc, deps) in tasks do
            let o2 = createObj [ "task_id", box tid; "title", box title; "description", box desc ]
            if deps <> [] then setKey o2 "depends_on" (box (List.toArray deps))
            items.Add o2
        setKey o "tasks" (box (items.ToArray()))
    | TaskStarted (_, tid, wt, branch) ->
        setKey o "task_id" (box tid)
        setKey o "worktree_path" (box wt)
        setKey o "branch_name" (box branch)
    | TaskSubmitted (_, tid, sha) ->
        setKey o "task_id" (box tid)
        setKey o "commit_sha" (box sha)
    | TaskMerged (_, tid, sha) ->
        setKey o "task_id" (box tid)
        setKey o "master_sha" (box sha)
    | TaskDone (_, tid, merged) ->
        setKey o "task_id" (box tid)
        setKey o "merged" (box merged)
    | TaskError (_, tid, err) ->
        setKey o "task_id" (box tid)
        setKey o "error" (box err)
    | SquadCancelled _ -> ()
    | SquadCreated (_, req) -> setKey o "requirement" (box req));
    o

let encodeEvent (e: SquadEvent) : string =
    let fmObj = fmKey e
    let yamlText = Yaml.stringify fmObj
    let prose = eventProse e
    "---\n" + yamlText + "---\n\n" + prose

let encodeEvents (events: SquadEvent list) : string =
    events |> List.map encodeEvent |> String.concat "\n"

let private parseEvent (parsed: obj) (typeName: string) : SquadEvent option =
    let sid = str parsed "session_id"
    let strField k =
        let v = get parsed k
        if isNullish v then None else Some (string v)
    let intField k =
        let v = get parsed k
        if isNullish v then None else Some (unbox<int> v)
    let boolField k =
        let v = get parsed k
        if isNullish v then None else Some (unbox<bool> v)
    let arrField k =
        let v = get parsed k
        if isNullish v || not (isArray v) then None
        else Some ((v :?> obj array) |> Array.map string |> Array.toList)
    let optStr k = strField k |> Option.defaultValue ""
    let optBool k = boolField k |> Option.defaultValue false
    match typeName with
    | "squad_created" ->
        let req = strField "requirement" |> Option.defaultValue ""
        Some (SquadCreated (sid, req))
    | "tasks_created" ->
        let tasksRaw = get parsed "tasks"
        let tasks =
            if isNullish tasksRaw || not (isArray tasksRaw) then []
            else (tasksRaw :?> obj array) |> Array.toList |> List.choose (fun o ->
                let tid = str o "task_id"
                if tid = "" then None
                else
                    let title = str o "title"
                    let desc = str o "description"
                    let depsArr = get o "depends_on"
                    let deps =
                        if isNullish depsArr || not (isArray depsArr) then []
                        else (depsArr :?> obj array) |> Array.map string |> Array.toList
                    Some (tid, title, desc, deps))
        Some (TasksCreated (sid, tasks))
    | "task_started" ->
        let tid = optStr "task_id"
        let wt = optStr "worktree_path"
        let branch = optStr "branch_name"
        Some (TaskStarted (sid, tid, wt, branch))
    | "task_submitted" ->
        let tid = optStr "task_id"
        let sha = optStr "commit_sha"
        Some (TaskSubmitted (sid, tid, sha))
    | "task_merged" ->
        let tid = optStr "task_id"
        let sha = optStr "master_sha"
        Some (TaskMerged (sid, tid, sha))
    | "task_done" ->
        let tid = optStr "task_id"
        let merged = optBool "merged"
        Some (TaskDone (sid, tid, merged))
    | "task_error" ->
        let tid = optStr "task_id"
        let err = optStr "error"
        Some (TaskError (sid, tid, err))
    | "squad_cancelled" -> Some (SquadCancelled sid)
    | _ -> None

let decodeEvents (text: string) : SquadEvent list =
    let rec scan (s: string) (acc: SquadEvent list) : SquadEvent list =
        let startIdx = s.IndexOf "---\n"
        if startIdx < 0 then acc
        else
            let afterStart = s.Substring (startIdx + 4)
            let endIdx = afterStart.IndexOf "\n---"
            if endIdx < 0 then acc
            else
                let yamlText = afterStart.Substring(0, endIdx).Trim()
                let rest = afterStart.Substring (endIdx + 4)
                let evOpt =
                    try
                        let parsed = Yaml.parse yamlText
                        let typeName = str parsed "squad_event"
                        match eventTypeNameFromString typeName with
                        | None -> None
                        | Some _ -> parseEvent parsed typeName
                    with _ -> None
                match evOpt with
                | Some ev -> scan rest (ev :: acc)
                | None -> scan rest acc
    scan text [] |> List.rev

let decodeEvent (text: string) : SquadEvent option =
    decodeEvents text |> List.tryHead
