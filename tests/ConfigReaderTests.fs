module Wanxiangzhen.Tests.ConfigReaderTests

open Fable.Core
open Fable.Core.JsInterop
open Wanxiangzhen.Kernel.SquadConfig
open Wanxiangzhen.Shell.ConfigReader
open Wanxiangzhen.Tests.Assert

[<Import("mkdtempSync", "node:fs")>]
let private mkdtemp (prefix: string) : string = jsNative

[<Import("writeFileSync", "node:fs")>]
let private writeFile (path: string) (data: string) : unit = jsNative

[<Import("rmSync", "node:fs")>]
let private rmSync (path: string) (opts: obj) : unit = jsNative

[<Import("existsSync", "node:fs")>]
let private existsSync (path: string) : bool = jsNative

[<Import("join", "node:path")>]
let private pathJoin (a: string) (b: string) : string = jsNative

let private cleanup (dir: string) : unit =
    try rmSync dir (createObj [ "recursive", box true; "force", box true ]) with _ -> ()

let entries () : (string * (unit -> unit)) list = [
    ("readConfig non-existent path returns defaults", fun () ->
        let config = readConfig "/tmp/nonexistent-wanxiangzhen-test"
        equal 3 config.MaxConcurrent
        equal [] config.SharedDirs)

    ("readConfig with full squad config parses all fields", fun () ->
        let temp = mkdtemp "wanxiangzhen-test-"
        let yamlContent = "---\nsquad:\n  maxConcurrent: 5\n  terminal: kitty\n  masterBranch: main\n  sharedDirs:\n    - node_modules\n    - .venv\n---"
        writeFile (pathJoin temp "AGENTS.md") yamlContent
        let config = readConfig temp
        equal 5 config.MaxConcurrent
        equal "kitty" config.Terminal
        match config.MasterBranch with
        | Some b -> equal "main" b
        | None -> check false
        equal ["node_modules"; ".venv"] config.SharedDirs
        cleanup temp)

    ("readConfig with invalid yaml returns defaults", fun () ->
        let temp = mkdtemp "wanxiangzhen-test-"
        writeFile (pathJoin temp "AGENTS.md") "---\nsquad: {{broken yaml\n---"
        let config = readConfig temp
        equal 3 config.MaxConcurrent
        cleanup temp)

    ("readConfig with empty file returns defaults", fun () ->
        let temp = mkdtemp "wanxiangzhen-test-"
        writeFile (pathJoin temp "AGENTS.md") ""
        let config = readConfig temp
        equal 3 config.MaxConcurrent
        cleanup temp)

    ("readConfig with file missing frontmatter returns defaults", fun () ->
        let temp = mkdtemp "wanxiangzhen-test-"
        writeFile (pathJoin temp "AGENTS.md") "just text\nno frontmatter"
        let config = readConfig temp
        equal 3 config.MaxConcurrent
        cleanup temp)
]
