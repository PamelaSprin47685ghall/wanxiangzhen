module Wanxiangzhen.Shell.GitShell

open Fable.Core
open Wanxiangzhen.Shell.Dyn

[<Import("spawnSync", "node:child_process")>]
let private spawnSync (cmd: string) (args: obj) (opts: obj) : obj = jsNative

let private readResult (x: obj) (k: string) : string =
    let v = get x k
    if isNullish v then "" else string v

let private runStdout (cwd: string) (args: string array) : string =
    let result = spawnSync "git" (box args) (box {| cwd = cwd; encoding = "utf-8" |})
    let st = unbox<int> (get result "status")
    if st <> 0 then failwith (readResult result "stderr")
    else (readResult result "stdout").TrimEnd()

let tryRun (cwd: string) (args: string array) : Result<string, string> =
    let result = spawnSync "git" (box args) (box {| cwd = cwd; encoding = "utf-8" |})
    let st = unbox<int> (get result "status")
    if st <> 0 then Error ((readResult result "stderr").TrimEnd())
    else Ok ((readResult result "stdout").TrimEnd())

let tryWorktreeAdd (cwd: string) (branch: string) (path: string) (baseBranch: string) : Result<string, string> =
    tryRun cwd [| "worktree"; "add"; "-b"; branch; path; baseBranch |]

let tryWorktreeRemoveForce (cwd: string) (path: string) : Result<string, string> =
    tryRun cwd [| "worktree"; "remove"; "--force"; path |]

let tryBranchDeleteForce (cwd: string) (branch: string) : Result<string, string> =
    tryRun cwd [| "branch"; "-D"; branch |]

let revParseHead (cwd: string) : string = runStdout cwd [| "rev-parse"; "HEAD" |]

let revParseRef (cwd: string) (ref: string) : string = runStdout cwd [| "rev-parse"; ref |]

let revParseBranch (cwd: string) : string = runStdout cwd [| "rev-parse"; "--abbrev-ref"; "HEAD" |]

let isDetached (cwd: string) : bool = revParseBranch cwd = "HEAD"

let hasCommits (cwd: string) : bool =
    try
        revParseHead cwd |> ignore
        true
    with _ -> false

let verifyBaseBranch (cwd: string) (baseBranch: string) : Result<string, string> =
    try
        let resolved = runStdout cwd [| "rev-parse"; "--verify"; "refs/heads/" + baseBranch |]
        Ok resolved
    with ex ->
        Error (string ex.Message)

let mergeBaseIsAncestor (cwd: string) (ancestor: string) (descendant: string) : bool =
    try
        runStdout cwd [| "merge-base"; "--is-ancestor"; ancestor; descendant |] |> ignore
        true
    with _ -> false

let statusIsClean (cwd: string) : bool =
    let out = runStdout cwd [| "status"; "--porcelain" |]
    System.String.IsNullOrEmpty out

let mergeFfOnly (cwd: string) (branch: string) : string =
    runStdout cwd [| "merge"; "--ff-only"; branch |] |> ignore
    revParseHead cwd

let worktreeAdd (cwd: string) (branch: string) (path: string) (baseBranch: string) : unit =
    runStdout cwd [| "worktree"; "add"; "-b"; branch; path; baseBranch |] |> ignore

let worktreeRemoveForce (cwd: string) (path: string) : unit =
    runStdout cwd [| "worktree"; "remove"; "--force"; path |] |> ignore

let branchDeleteForce (cwd: string) (branch: string) : unit =
    runStdout cwd [| "branch"; "-D"; branch |] |> ignore

let showRefExists (cwd: string) (branch: string) : bool =
    try
        runStdout cwd [| "show-ref"; "--verify"; "refs/heads/" + branch |] |> ignore
        true
    with _ -> false
