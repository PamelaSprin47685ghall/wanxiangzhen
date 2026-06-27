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
    match e with
    | TaskCreated (_, tid, title, desc, deps) ->
        setKey o "task_id" (box tid)
        setKey o "title" (box title)
        setKey o "description" (box desc)
        if deps <> [] then setKey o "depends_on" (box (List.toArray deps))
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
    | SquadCancelled _ -> ()
    | SquadCreated (_, req) -> setKey o "requirement" (box req)
    o

let encodeEvent (e: SquadEvent) : string =
    let fmObj = fmKey e
    let yamlText = Yaml.stringify fmObj
    let prose = eventProse e
    "---\n" + yamlText + "---\n\n" + prose

let decodeEvent (text: string) : SquadEvent option =
    let trimmed = text.TrimStart()
    if not (trimmed.StartsWith "---") then None
    else
        let afterFirst = trimmed.Substring 3
        let endIdx = afterFirst.IndexOf "---"
        if endIdx < 0 then None
        else
            let yamlText = afterFirst.Substring(0, endIdx).Trim()
            try
                let parsed = Yaml.parse yamlText
                let typeName = str parsed "squad_event"
                match eventTypeNameFromString typeName with
                | None -> None
                | Some _ ->
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
                    match typeName with
                    | "squad_created" ->
                        let req = strField "requirement" |> Option.defaultValue ""
                        Some (SquadCreated (sid, req))
                    | "task_created" ->
                        let tid = strField "task_id" |> Option.defaultValue ""
                        let title = strField "title" |> Option.defaultValue ""
                        let desc = strField "description" |> Option.defaultValue ""
                        let deps = arrField "depends_on" |> Option.defaultValue []
                        Some (TaskCreated (sid, tid, title, desc, deps))
                    | "task_started" ->
                        let tid = strField "task_id" |> Option.defaultValue ""
                        let wt = strField "worktree_path" |> Option.defaultValue ""
                        let branch = strField "branch_name" |> Option.defaultValue ""
                        Some (TaskStarted (sid, tid, wt, branch))
                    | "task_submitted" ->
                        let tid = strField "task_id" |> Option.defaultValue ""
                        let sha = strField "commit_sha" |> Option.defaultValue ""
                        Some (TaskSubmitted (sid, tid, sha))
                    | "task_merged" ->
                        let tid = strField "task_id" |> Option.defaultValue ""
                        let sha = strField "master_sha" |> Option.defaultValue ""
                        Some (TaskMerged (sid, tid, sha))
                    | "task_done" ->
                        let tid = strField "task_id" |> Option.defaultValue ""
                        let merged = boolField "merged" |> Option.defaultValue false
                        Some (TaskDone (sid, tid, merged))
                    | "squad_cancelled" -> Some (SquadCancelled sid)
                    | _ -> None
            with _ -> None
