module Wanxiangzhen.Shell.SlaveSpawn

open Fable.Core
open Wanxiangzhen.Shell.Dyn

[<Import("spawn", "node:child_process")>]
let private childSpawn (cmd: string) (args: string array) (opts: obj) : obj = jsNative

let buildSlaveCommand (terminal: string) (worktree: string) (prompt: string) : string * string array =
    let ocArgs = [| "tui"; "--prompt"; prompt |]
    match terminal with
    | "alacritty" ->
        "alacritty", Array.append [| "--working-directory"; worktree; "-e"; "opencode" |] ocArgs
    | "kitty" ->
        "kitty", Array.append [| "--directory"; worktree; "opencode" |] ocArgs
    | "gnome-terminal" ->
        "gnome-terminal", Array.append [| "--working-directory=" + worktree; "--"; "opencode" |] ocArgs
    | "konsole" ->
        "konsole", Array.append [| "--workdir"; worktree; "-e"; "opencode" |] ocArgs
    | "wezterm" ->
        "wezterm", Array.append [| "start"; "--cwd"; worktree; "--"; "opencode" |] ocArgs
    | "wt" ->
        "wt.exe", Array.append [| "-d"; worktree; "opencode" |] ocArgs
    | "iterm2" ->
        let script = sprintf "cd %s && opencode tui --prompt '%s'" worktree prompt
        "osascript", [| "-e"; "tell application \"iTerm\" to create window with default profile"; "-e"; sprintf "tell application \"iTerm\" to tell current session of current window to write text \"%s\"" script |]
    | "headless" ->
        "opencode", ocArgs
    | _ ->
        terminal, Array.append [| "--working-directory"; worktree; "-e"; "opencode" |] ocArgs

let spawnSlave (terminal: string) (worktree: string) (env: obj) (prompt: string) : unit =
    let cmd, args = buildSlaveCommand terminal worktree prompt
    let opts = box {| cwd = worktree; env = env; detached = false; stdio = "ignore" |}
    childSpawn cmd args opts |> ignore
