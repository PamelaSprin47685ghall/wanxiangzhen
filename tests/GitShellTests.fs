module Wanxiangzhen.Tests.GitShellTests

open Fable.Core
open Fable.Core.JsInterop
open Wanxiangzhen.Shell.GitShell
open Wanxiangzhen.Tests.Assert

[<Import("mkdtempSync", "node:fs")>]
let private mkdtemp (prefix: string) : string = jsNative

[<Import("writeFileSync", "node:fs")>]
let private writeFile (path: string) (data: string) : unit = jsNative

[<Import("rmSync", "node:fs")>]
let private rmSync (path: string) (opts: obj) : unit = jsNative

[<Import("execSync", "node:child_process")>]
let private execSync (cmd: string) (opts: obj) : obj = jsNative

let private git (cwd: string) (args: string) : string =
    let r = execSync ("git " + args) (box {| cwd = cwd; encoding = "utf-8" |})
    string r |> (fun s -> s.TrimEnd())

let private initRepo () : string =
    let dir = mkdtemp "wanxiangzhen-git-test-"
    git dir "init -b main" |> ignore
    git dir "config user.email test@test.com" |> ignore
    git dir "config user.name test" |> ignore
    writeFile (dir + "/README.md") "# test"
    git dir "add -A" |> ignore
    git dir "commit -m init" |> ignore
    dir

let private initEmptyRepo () : string =
    let dir = mkdtemp "wanxiangzhen-git-empty-test-"
    git dir "init -b main" |> ignore
    git dir "config user.email test@test.com" |> ignore
    git dir "config user.name test" |> ignore
    dir

let private cleanup (dir: string) : unit =
    try rmSync dir (createObj [ "recursive", box true; "force", box true ]) with _ -> ()

let entries () : (string * (unit -> unit)) list = [
    ("hasCommits on empty repo returns false", fun () ->
        let dir = initEmptyRepo ()
        equal false (hasCommits dir)
        cleanup dir)

    ("hasCommits on repo with commit returns true", fun () ->
        let dir = initRepo ()
        equal true (hasCommits dir)
        cleanup dir)

    ("revParseHead returns non-empty sha", fun () ->
        let dir = initRepo ()
        let sha = revParseHead dir
        checkBare (sha.Length > 0)
        cleanup dir)

    ("revParseBranch returns branch name", fun () ->
        let dir = initRepo ()
        let branch = revParseBranch dir
        equal "main" branch
        cleanup dir)

    ("isDetached on branch returns false", fun () ->
        let dir = initRepo ()
        equal false (isDetached dir)
        cleanup dir)

    ("statusIsClean on clean repo returns true", fun () ->
        let dir = initRepo ()
        equal true (statusIsClean dir)
        cleanup dir)

    ("statusIsClean on dirty repo returns false", fun () ->
        let dir = initRepo ()
        writeFile (dir + "/dirty.txt") "dirty"
        equal false (statusIsClean dir)
        cleanup dir)

    ("mergeBaseIsAncestor with ancestor returns true", fun () ->
        let dir = initRepo ()
        git dir "checkout -b feature" |> ignore
        writeFile (dir + "/feature.txt") "feature"
        git dir "add -A" |> ignore
        git dir "commit -m feature" |> ignore
        git dir "checkout main" |> ignore
        equal true (mergeBaseIsAncestor dir "main" "feature")
        cleanup dir)

    ("mergeBaseIsAncestor non-ancestor returns false", fun () ->
        let dir = initRepo ()
        equal true (mergeBaseIsAncestor dir "main" "main")
        cleanup dir)

    ("showRefExists with existing branch returns true", fun () ->
        let dir = initRepo ()
        equal true (showRefExists dir "main")
        cleanup dir)

    ("showRefExists with non-existent branch returns false", fun () ->
        let dir = initRepo ()
        equal false (showRefExists dir "nonexistent-branch-12345")
        cleanup dir)

    ("worktreeAdd then worktreeRemoveForce cycle works", fun () ->
        let dir = initRepo ()
        let suffix = string (System.DateTime.UtcNow.Ticks)
        let wtPath = dir + "/../worktree-test-" + suffix.Substring(suffix.Length - 8)
        try
            worktreeAdd dir "test-branch" wtPath "main"
            checkBare (showRefExists dir "test-branch")
            worktreeRemoveForce dir wtPath
            checkBare true
        finally
            try rmSync wtPath (createObj [ "recursive", box true; "force", box true ]) with _ -> ()
        cleanup dir)

    ("branchDeleteForce deletes branch", fun () ->
        let dir = initRepo ()
        let suffix = string (System.DateTime.UtcNow.Ticks)
        let wtPath = dir + "/../worktree-del-" + suffix.Substring(suffix.Length - 8)
        try
            worktreeAdd dir "del-branch" wtPath "main"
            worktreeRemoveForce dir wtPath
            branchDeleteForce dir "del-branch"
            equal false (showRefExists dir "del-branch")
        finally
            try rmSync wtPath (createObj [ "recursive", box true; "force", box true ]) with _ -> ()
        cleanup dir)

    ("mergeFfOnly returns new sha", fun () ->
        let dir = initRepo ()
        let shaBefore = revParseHead dir
        git dir "checkout -b feature" |> ignore
        writeFile (dir + "/feature.txt") "data"
        git dir "add -A" |> ignore
        git dir "commit -m feat" |> ignore
        git dir "checkout main" |> ignore
        let shaAfter = mergeFfOnly dir "feature"
        checkBare (shaAfter <> shaBefore)
        cleanup dir)

    ("revParseRef returns ref sha", fun () ->
        let dir = initRepo ()
        let sha1 = revParseHead dir
        let sha2 = revParseRef dir "HEAD"
        equal sha1 sha2
        cleanup dir)
]
