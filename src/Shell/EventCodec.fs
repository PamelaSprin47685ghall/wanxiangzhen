module Shell.EventCodec
open System
open System.Text
open YamlDotNet.Serialization
open Kernel

let private deserializer = DeserializerBuilder().IgnoreUnmatchedProperties().Build()

let parseFrontMatter (text: string) : System.Collections.Generic.Dictionary<string, obj> =
    let lines = text.Split([|'\n'|], StringSplitOptions.None) |> Array.toList
    let rec findDelimiters lines =
        match lines with
        | "---" :: rest -> findStart rest []
        | _ -> None
    and findStart lines acc =
        match lines with
        | "---" :: _ -> Some (List.rev acc)
        | h :: t -> findStart t (h :: acc)
        | [] -> None
    match findDelimiters lines with
    | Some yamlLines ->
        let yaml = String.Join("\n", yamlLines)
        try deserializer.Deserialize<System.Collections.Generic.Dictionary<string, obj>>(yaml)
        with _ -> null
    | None -> null

let private tryGetStr (key: string) (dict: System.Collections.Generic.Dictionary<string, obj>) : string option =
    match dict.TryGetValue(key) with
    | true, (:? string as s) -> Some s
    | _ -> None

let private tryGetInt (key: string) (dict: System.Collections.Generic.Dictionary<string, obj>) : int option =
    match dict.TryGetValue(key) with
    | true, (:? int as i) -> Some i
    | _ -> None

let private tryGetBool (key: string) (dict: System.Collections.Generic.Dictionary<string, obj>) : bool option =
    match dict.TryGetValue(key) with
    | true, (:? bool as b) -> Some b
    | _ -> None

let decodeSquadEvent (s: string) : SquadEvent option =
    match s.ToLowerInvariant() with
    | "squad_created" -> Some SquadCreated
    | "task_created" -> Some TaskCreated
    | "task_started" -> Some TaskStarted
    | "task_submitted" -> Some TaskSubmitted
    | "task_merged" -> Some TaskMerged
    | "task_done" -> Some TaskDone
    | "squad_cancelled" -> Some SquadCancelled
    | _ -> None

let decodeTaskId (s: string) : TaskId option =
    if s.StartsWith("squad-") then Some (TaskId s) else None

let private parseDependsOn (fm: System.Collections.Generic.Dictionary<string, obj>) : TaskId list =
    match tryGetStr "depends_on" fm with
    | None -> []
    | Some s ->
        if s.StartsWith("[") && s.EndsWith("]") then
            try
                let inner = s.Substring(1, s.Length - 2)
                let ids = inner.Split(',') |> Array.map (fun x -> x.Trim().Trim('"')) |> Array.toList
                ids |> List.choose decodeTaskId
            with _ -> []
        else
            []

let eventPayloadFromMap (fm: System.Collections.Generic.Dictionary<string, obj>) : EventPayload option =
    match tryGetStr "squad_event" fm |> Option.bind decodeSquadEvent with
    | Some evt ->
        let sessionId = tryGetStr "session_id" fm |> Option.defaultValue ""
        let taskId = tryGetStr "task_id" fm |> Option.bind decodeTaskId
        Some {
            squadEvent = evt
            sessionId = sessionId
            taskId = taskId
            title = tryGetStr "title" fm
            description = tryGetStr "description" fm
            dependsOn = parseDependsOn fm
            masterSha = tryGetStr "master_sha" fm
            worktreePath = tryGetStr "worktree_path" fm
            branchName = tryGetStr "branch_name" fm
            slavePid = tryGetInt "slave_pid" fm
            merged = tryGetBool "merged" fm
            requirement = tryGetStr "requirement" fm
        }
    | None -> None

let encodeEvent (evt: EventPayload) (prose: string) : string =
    let sb = StringBuilder()
    sb.AppendLine("---") |> ignore
    let evtName =
        match evt.squadEvent with
        | SquadCreated -> "squad_created"
        | TaskCreated -> "task_created"
        | TaskStarted -> "task_started"
        | TaskSubmitted -> "task_submitted"
        | TaskMerged -> "task_merged"
        | TaskDone -> "task_done"
        | SquadCancelled -> "squad_cancelled"
    sb.AppendLine("squad_event: " + evtName) |> ignore
    sb.AppendLine("session_id: " + evt.sessionId) |> ignore
    match evt.taskId with
    | Some (TaskId id) -> sb.AppendLine("task_id: " + id) |> ignore
    | None -> ()
    match evt.title with Some t -> sb.AppendLine("title: \"" + t + "\"") |> ignore | None -> ()
    match evt.dependsOn with
    | [] -> ()
    | deps ->
        let ids = deps |> List.map (fun (TaskId x) -> "\"" + x + "\"") |> String.concat ", "
        sb.AppendLine("depends_on: [" + ids + "]") |> ignore
    match evt.masterSha with Some s -> sb.AppendLine("master_sha: " + s) |> ignore | None -> ()
    sb.AppendLine("---") |> ignore
    sb.AppendLine() |> ignore
    sb.Append(prose) |> ignore
    sb.ToString()
