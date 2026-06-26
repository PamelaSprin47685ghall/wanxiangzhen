module ShellConfigReader

open System
open System.IO
open YamlDotNet.Serialization
open Kernel

let defaultConfig = {
    maxConcurrent = 3
    masterBranch = ""
    terminal = "alacritty"
    sharedDirs = []
}

// ─── 读取 AGENTS.md squad: 节 ─────────────────────────────────
let readSquadConfig (worktreePath: string) : SquadConfig =
    let agentsPath = Path.Combine(worktreePath, "AGENTS.md")
    if not (File.Exists agentsPath) then
        defaultConfig
    else
        try
            let text = File.ReadAllText(agentsPath)
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
                let d = DeserializerBuilder().IgnoreUnmatchedProperties().Build()
                let dict = d.Deserialize<System.Collections.Generic.Dictionary<string, obj>>(yaml)
                let getStr key = if dict.ContainsKey key then dict.[key] :?> string else ""
                let getInt key = if dict.ContainsKey key then dict.[key] :?> int else 0
                let getStringList key =
                    if dict.ContainsKey key then
                        match dict.[key] with
                        | :? System.Collections.Generic.List<string> as l -> List.ofSeq l
                        | _ -> []
                    else []
                let getSquad () =
                    if dict.ContainsKey("squad") then
                        match dict.["squad"] with
                        | :? System.Collections.Generic.Dictionary<string, obj> as d -> Some d
                        | _ -> None
                    else None
                match getSquad() with
                | Some squadDict ->
                    let tryGetInt2 (key: string) (d: System.Collections.Generic.Dictionary<string, obj>) : int =
                        match d.TryGetValue(key) with
                        | true, (:? int as i) -> i
                        | true, (:? string as s) -> match Int32.TryParse(s) with true, v -> v | _ -> 0
                        | _ -> 0
                    let tryGetStr2 (key: string) (d: System.Collections.Generic.Dictionary<string, obj>) : string =
                        match d.TryGetValue(key) with
                        | true, (:? string as s) -> s
                        | true, (:? int as i) -> string i
                        | _ -> ""
                    let tryGetList2 (key: string) (d: System.Collections.Generic.Dictionary<string, obj>) : string list =
                        match d.TryGetValue(key) with
                        | true, (:? System.Collections.Generic.List<obj> as l) ->
                            l |> Seq.map (fun o -> match o with :? string as s -> s | _ -> string o) |> List.ofSeq
                        | _ -> []
                    let maxConc = tryGetInt2 "maxConcurrent" squadDict
                    let terminal = tryGetStr2 "terminal" squadDict
                    let masterBranch = tryGetStr2 "masterBranch" squadDict
                    let sharedDirs = tryGetList2 "sharedDirs" squadDict
                    { defaultConfig with maxConcurrent = maxConc; terminal = terminal; masterBranch = masterBranch; sharedDirs = sharedDirs }
                | None -> defaultConfig
            | None -> defaultConfig
        with _ ->
            defaultConfig
