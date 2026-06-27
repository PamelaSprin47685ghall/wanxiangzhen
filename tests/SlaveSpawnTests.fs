module Wanxiangzhen.Tests.SlaveSpawnTests

open Wanxiangzhen.Shell.SlaveSpawn
open Wanxiangzhen.Tests.Assert

let wt = "/home/user/project/../worktree-squad-a1b2"
let prompt = "Implement login remember-me feature"

let private checkCmd (expectedCmd: string) (cmd: string) =
    equal expectedCmd cmd

let private checkArgs (expectedFlags: string list) (args: string array) =
    for flag in expectedFlags do
        check (Array.contains flag args)

let entries () : (string * (unit -> unit)) list = [
    ("buildSlaveCommand.alacritty", fun () ->
        let cmd, args = buildSlaveCommand "alacritty" wt prompt
        checkCmd "alacritty" cmd
        checkArgs ["--working-directory"; wt; "-e"; "opencode"; "tui"; "--prompt"; prompt] args)

    ("buildSlaveCommand.kitty", fun () ->
        let cmd, args = buildSlaveCommand "kitty" wt prompt
        checkCmd "kitty" cmd
        checkArgs ["--directory"; wt; "opencode"; "tui"; "--prompt"; prompt] args)

    ("buildSlaveCommand.gnome-terminal", fun () ->
        let cmd, args = buildSlaveCommand "gnome-terminal" wt prompt
        checkCmd "gnome-terminal" cmd
        checkArgs ["--working-directory=" + wt; "--"; "opencode"; "tui"; "--prompt"; prompt] args)

    ("buildSlaveCommand.konsole", fun () ->
        let cmd, args = buildSlaveCommand "konsole" wt prompt
        checkCmd "konsole" cmd
        checkArgs ["--workdir"; wt; "-e"; "opencode"; "tui"; "--prompt"; prompt] args)

    ("buildSlaveCommand.wezterm", fun () ->
        let cmd, args = buildSlaveCommand "wezterm" wt prompt
        checkCmd "wezterm" cmd
        checkArgs ["start"; "--cwd"; wt; "--"; "opencode"; "tui"; "--prompt"; prompt] args)

    ("buildSlaveCommand.wt", fun () ->
        let cmd, args = buildSlaveCommand "wt" wt prompt
        checkCmd "wt.exe" cmd
        checkArgs ["-d"; wt; "opencode"; "tui"; "--prompt"; prompt] args)

    ("buildSlaveCommand.iterm2", fun () ->
        let cmd, args = buildSlaveCommand "iterm2" wt prompt
        checkCmd "osascript" cmd
        check (Array.length args = 4)
        equal "-e" args.[0]
        equal "-e" args.[2])

    ("buildSlaveCommand.headless", fun () ->
        let cmd, args = buildSlaveCommand "headless" wt prompt
        checkCmd "opencode" cmd
        checkArgs ["tui"; "--prompt"; prompt] args)

    ("buildSlaveCommand.unknown-terminal-falls-back-to-alacritty", fun () ->
        let cmd, args = buildSlaveCommand "foot" wt prompt
        checkCmd "foot" cmd
        checkArgs ["--working-directory"; wt; "-e"; "opencode"; "tui"; "--prompt"; prompt] args)
]
