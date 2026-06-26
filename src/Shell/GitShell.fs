module Wanxiangzhen.Shell.GitShell

open Fable.Core
open Wanxiangzhen.Shell.Dyn

[<Import("execSync", "node:child_process")>]
let private execSync (cmd: string) (opts: obj) : obj = jsNative

let private run (cwd: string) (args: string array) : string =
    let full = "git " + (args |> String.concat " ")
    let result = execSync full (box {| cwd = cwd; encoding = "utf-8" |})
    string result |> (fun s -> s.TrimEnd())

let tryRun (cwd: string) (args: string array) : Result<string, string> =
    try Ok (run cwd args)
    with ex -> Error (string ex.Message)

let revParseHead (cwd: string) : string = run cwd [| "rev-parse"; "HEAD" |]

let revParseRef (cwd: string) (ref: string) : string = run cwd [| "rev-parse"; ref |]

let revParseBranch (cwd: string) : string = run cwd [| "rev-parse"; "--abbrev-ref"; "HEAD" |]

let isDetached (cwd: string) : bool = revParseBranch cwd = "HEAD"

let mergeBaseIsAncestor (cwd: string) (ancestor: string) (descendant: string) : bool =
    try
        run cwd [| "merge-base"; "--is-ancestor"; ancestor; descendant |] |> ignore
        true
    with _ -> false

let statusIsClean (cwd: string) : bool =
    let out = run cwd [| "status"; "--porcelain" |]
    System.String.IsNullOrEmpty out

let mergeFfOnly (cwd: string) (branch: string) : string =
    run cwd [| "merge"; "--ff-only"; branch |] |> ignore
    revParseHead cwd

let worktreeAdd (cwd: string) (branch: string) (path: string) (baseBranch: string) : unit =
    run cwd [| "worktree"; "add"; "-b"; branch; path; baseBranch |] |> ignore

let worktreeRemoveForce (cwd: string) (path: string) : unit =
    run cwd [| "worktree"; "remove"; "--force"; path |] |> ignore

let branchDeleteForce (cwd: string) (branch: string) : unit =
    run cwd [| "branch"; "-D"; branch |] |> ignore

let showRefExists (cwd: string) (branch: string) : bool =
    try
        run cwd [| "show-ref"; "--verify"; "refs/heads/" + branch |] |> ignore
        true
    with _ -> false
