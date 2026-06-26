module Shell.SlaveSpawn
open System
open Kernel
open Fable.Core.JsInterop
open Shell.NodeInterop

type SpawnResult = { childPid: int option; error: string option }

let resolveTerminal (terminalName: string) (worktreePath: string) : string[] * int =
    let name = terminalName.ToLowerInvariant()
    match name with
    | "alacritty" ->
        [| "alacritty"; "--working-directory"; worktreePath; "-e"; "opencode"; "tui"; "--prompt"; "" |], 7
    | "kitty" ->
        [| "kitty"; "--directory"; worktreePath; "opencode"; "tui"; "--prompt"; "" |], 6
    | "gnome-terminal" ->
        [| "gnome-terminal"; "--working-directory" + "=" + worktreePath; "--"; "opencode"; "tui"; "--prompt"; "" |], 7
    | "konsole" ->
        [| "konsole"; "--workdir"; worktreePath; "-e"; "opencode"; "tui"; "--prompt"; "" |], 7
    | "wezterm" ->
        [| "wezterm"; "start"; "--cwd"; worktreePath; "--"; "opencode"; "tui"; "--prompt"; "" |], 8
    | "headless" ->
        [| "opencode"; "tui"; "--prompt"; "" |], 3
    | _ ->
        [| "opencode"; "tui"; "--prompt"; "" |], 3

let createSymlinks (sharedDirs: string list) (projectRoot: string) (worktreePath: string) : unit =
    for dir in sharedDirs do
        try
            // Use NodeInterop.fsExistsSync instead of System.IO.Directory.Exists / File.Exists
            let src = NodeInterop.pathJoin [| projectRoot; dir |]
            let dst = NodeInterop.pathJoin [| worktreePath; dir |]
            if NodeInterop.fsExistsSync src && not (NodeInterop.fsExistsSync dst) then
                // 用 NodeInterop.execSync 执行 ln -s，避免 System.Diagnostics.Process
                let relative = NodeInterop.pathRelative worktreePath src
                // ln -s <relative> <dst>
                NodeInterop.execSync $"ln -s \"{relative}\" \"{dst}\"" (NodeInterop.mkExecOptions "" null) |> ignore
                // 写入 .git/info/exclude
                let excludePath = NodeInterop.pathJoin [| worktreePath; ".git"; "info"; "exclude" |]
                if NodeInterop.fsExistsSync excludePath then
                    NodeInterop.fsWriteFileSync excludePath ("\n" + dir + "\n")
        with _ -> ()

let spawnSlave (env: System.Collections.Generic.IDictionary<string, string>) (terminalName: string) (worktreePath: string) (initialPrompt: string) : SpawnResult =
    let argsTemplate, promptIdx = resolveTerminal terminalName worktreePath
    let args = argsTemplate |> Array.copy
    if promptIdx >= 0 && promptIdx < args.Length then
        args.[promptIdx] <- initialPrompt
    try
        // Use NodeInterop.spawn: first arg = command, rest = args array, mkSpawnOptions worktreePath envObj
        // env: IDictionary<string,string> -> obj
        let envObj : obj =
            let dict = System.Collections.Generic.Dictionary<string, obj>()
            for kv in env do dict.Add(kv.Key, box kv.Value)
            dict :> obj
        let spawnOpts = NodeInterop.mkSpawnOptions worktreePath envObj
        let child = NodeInterop.spawn args.[0] args.[1..] spawnOpts
        let pid = getChildPid(child)
        { childPid = Some pid; error = None }
    with ex ->
        { childPid = None; error = Some ex.Message }
