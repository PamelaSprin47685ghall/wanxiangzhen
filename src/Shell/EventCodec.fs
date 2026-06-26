module Wanxiangzhen.Shell.EventCodec

open Fable.Core
open Fable.Core.JsInterop
open Wanxiangzhen.Kernel.SquadEvent
open Wanxiangzhen.Kernel.SquadPrompts
open Wanxiangzhen.Shell.Yaml
open Wanxiangzhen.Shell.Dyn

let private fmKey (e: SquadEvent) =
    let o = createObj [
        "squad_event", box (eventTypeName e.Type)
        "session_id", box e.SessionId
    ]
    let addOpt (key: string) (v: 'a option) =
        match v with Some x -> setKey o key (box x) | None -> ()
    addOpt "task_id" e.TaskId
    addOpt "title" e.Title
    match e.DependsOn with
    | Some xs when xs.Length > 0 -> setKey o "depends_on" (box (List.toArray xs))
    | _ -> ()
    addOpt "worktree_path" e.WorktreePath
    addOpt "branch_name" e.BranchName
    addOpt "slave_pid" e.SlavePid
    addOpt "commit_sha" e.CommitSha
    addOpt "master_sha" e.MasterSha
    addOpt "merged" e.Merged
    o

let encodeEvent (e: SquadEvent) : string =
    let fmObj = fmKey e
    let yamlText = Yaml.stringify fmObj
    let prose = eventProse e.Type
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
                match eventTypeFromString typeName with
                | None -> None
                | Some et ->
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
                    Some {
                        Type = et
                        SessionId = str parsed "session_id"
                        TaskId = strField "task_id"
                        Title = strField "title"
                        Description = None
                        DependsOn = arrField "depends_on"
                        WorktreePath = strField "worktree_path"
                        BranchName = strField "branch_name"
                        SlavePid = intField "slave_pid"
                        CommitSha = strField "commit_sha"
                        MasterSha = strField "master_sha"
                        Merged = boolField "merged"
                    }
            with _ -> None
