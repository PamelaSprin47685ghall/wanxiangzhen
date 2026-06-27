module Wanxiangzhen.Shell.ConfigReader

open Fable.Core
open Wanxiangzhen.Kernel.SquadConfig
open Wanxiangzhen.Shell.Yaml
open Wanxiangzhen.Shell.Dyn

[<Import("readFileSync", "node:fs")>]
let private readFileSync (path: string) (encoding: string) : string = jsNative

[<Import("existsSync", "node:fs")>]
let private existsSync (path: string) : bool = jsNative

[<Import("join", "node:path")>]
let private pathJoin (a: string) (b: string) : string = jsNative

let private extractFrontmatter (text: string) : string option =
    let trimmed = text.TrimStart()
    if not (trimmed.StartsWith "---") then None
    else
        let afterFirst = trimmed.Substring 3
        let endIdx = afterFirst.IndexOf "---"
        if endIdx < 0 then None
        else Some (afterFirst.Substring(0, endIdx).Trim())

let private parseSquadConfig (parsed: obj) : SquadConfig =
    let squad = get parsed "squad"
    if isNullish squad then defaults
    else
        let mc = get squad "maxConcurrent"
        let term = str squad "terminal"
        let mb = get squad "masterBranch"
        let sd = get squad "sharedDirs"
        { MaxConcurrent = if isNullish mc then defaults.MaxConcurrent else unbox<int> mc
          Terminal = if System.String.IsNullOrEmpty term then defaults.Terminal else term
          MasterBranch = if isNullish mb then None else Some (string mb)
          SharedDirs =
            if isNullish sd || not (isArray sd) then []
            else (sd :?> obj array) |> Array.map string |> Array.toList }

let readConfig (worktree: string) : SquadConfig =
    let path = pathJoin worktree "AGENTS.md"
    if not (existsSync path) then defaults
    else
        let text = readFileSync path "utf-8"
        match extractFrontmatter text with
        | None -> defaults
        | Some fm ->
            try
                let parsed = Yaml.parse fm
                if isNullish parsed then defaults
                else mergeWithDefaults (Some (parseSquadConfig parsed))
            with _ -> defaults

let detectVibeFs (directory: string) : bool =
    try existsSync (pathJoin (pathJoin directory "node_modules") "wanxiangshu")
    with _ -> false
