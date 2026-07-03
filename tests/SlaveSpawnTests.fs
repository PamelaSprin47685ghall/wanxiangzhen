module Wanxiangzhen.Tests.SlaveSpawnTests

open Wanxiangzhen.Shell.SlaveSpawn
open Wanxiangzhen.Tests.Assert

let wt = "/home/user/project/../worktree-squad-a1b2"
let prompt = "Implement login remember-me feature"

let private checkBareCmd (expectedCmd: string) (cmd: string) =
    equal expectedCmd cmd

let private checkBareArgs (expectedFlags: string list) (args: string array) =
    for flag in expectedFlags do
        checkBare (Array.contains flag args)

let entries () : (string * (unit -> unit)) list = [
    ("buildSlaveCommand.alacritty", fun () ->
        let cmd, args = buildSlaveCommand "alacritty" wt prompt
        checkBareCmd "alacritty" cmd
        checkBareArgs ["--working-directory"; wt; "-e"; "opencode"; "tui"; "--prompt"; prompt] args)

    ("buildSlaveCommand.kitty", fun () ->
        let cmd, args = buildSlaveCommand "kitty" wt prompt
        checkBareCmd "kitty" cmd
        checkBareArgs ["--directory"; wt; "opencode"; "tui"; "--prompt"; prompt] args)

    ("buildSlaveCommand.gnome-terminal", fun () ->
        let cmd, args = buildSlaveCommand "gnome-terminal" wt prompt
        checkBareCmd "gnome-terminal" cmd
        checkBareArgs ["--working-directory=" + wt; "--"; "opencode"; "tui"; "--prompt"; prompt] args)

    ("buildSlaveCommand.konsole", fun () ->
        let cmd, args = buildSlaveCommand "konsole" wt prompt
        checkBareCmd "konsole" cmd
        checkBareArgs ["--workdir"; wt; "-e"; "opencode"; "tui"; "--prompt"; prompt] args)

    ("buildSlaveCommand.wezterm", fun () ->
        let cmd, args = buildSlaveCommand "wezterm" wt prompt
        checkBareCmd "wezterm" cmd
        checkBareArgs ["start"; "--cwd"; wt; "--"; "opencode"; "tui"; "--prompt"; prompt] args)

    ("buildSlaveCommand.wt", fun () ->
        let cmd, args = buildSlaveCommand "wt" wt prompt
        checkBareCmd "wt.exe" cmd
        checkBareArgs ["-d"; wt; "opencode"; "tui"; "--prompt"; prompt] args)

    ("buildSlaveCommand.iterm2", fun () ->
        let cmd, args = buildSlaveCommand "iterm2" wt prompt
        checkBareCmd "osascript" cmd
        checkBare (Array.length args = 4)
        equal "-e" args.[0]
        equal "-e" args.[2])

    ("buildSlaveCommand.headless", fun () ->
        let cmd, args = buildSlaveCommand "headless" wt prompt
        checkBareCmd "opencode" cmd
        checkBareArgs ["tui"; "--prompt"; prompt] args)

    ("buildSlaveCommand.unknown-terminal-falls-back-to-alacritty", fun () ->
        let cmd, args = buildSlaveCommand "foot" wt prompt
        checkBareCmd "foot" cmd
        checkBareArgs ["--working-directory"; wt; "-e"; "opencode"; "tui"; "--prompt"; prompt] args)
]
