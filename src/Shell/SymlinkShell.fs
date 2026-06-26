module Wanxiangzhen.Shell.SymlinkShell

open Fable.Core
open Wanxiangzhen.Shell.Dyn

[<Import("symlinkSync", "node:fs")>]
let private symlinkSync (target: string) (path: string) (ty: string) : unit = jsNative

[<Import("existsSync", "node:fs")>]
let private existsSync (path: string) : bool = jsNative

[<Import("join", "node:path")>]
let private pathJoin (a: string) (b: string) : string = jsNative

let createSymlinks (worktree: string) (projectRoot: string) (sharedDirs: string list) : unit =
    sharedDirs |> List.iter (fun dir ->
        let target = pathJoin projectRoot dir
        let linkPath = pathJoin worktree dir
        if existsSync target && not (existsSync linkPath) then
            try symlinkSync target linkPath "dir"
            with _ -> ())
