module Wanxiangzhen.Tests.SlaveRuntimeTests

open Fable.Core
open Fable.Core.JsInterop
open Wanxiangzhen.Kernel.FfDecision
open Wanxiangzhen.Shell.Dyn
open Wanxiangzhen.Shell.SlaveRuntime
open Wanxiangzhen.Tests.Assert

let private testConfig = {
    CoordinatorUrl = "http://127.0.0.1:54321"
    TaskId = "squad-a1b2"
    WorktreePath = "/tmp/wt-a1b2"
    MasterBranch = "main"
    Token = "test-token-xyz"
}

[<Global("process")>]
let private nodeProcess : obj = jsNative

let private getEnv () : obj = get nodeProcess "env"

let entries () : (string * (unit -> unit)) list = [
    ("SlaveRuntime.slaveToolDefs has submit_to_squad", fun () ->
        let defs = slaveToolDefs testConfig
        check (has defs "submit_to_squad"))

    ("SlaveRuntime.slaveToolDefs has query_squad", fun () ->
        let defs = slaveToolDefs testConfig
        check (has defs "query_squad"))

    ("SlaveRuntime.slaveToolDefs submit description non-empty", fun () ->
        let defs = slaveToolDefs testConfig
        let submitDef = get defs "submit_to_squad"
        let desc = str submitDef "description"
        check (desc.Length > 0))

    ("SlaveRuntime.slaveToolDefs query description non-empty", fun () ->
        let defs = slaveToolDefs testConfig
        let queryDef = get defs "query_squad"
        let desc = str queryDef "description"
        check (desc.Length > 0))

    ("SlaveRuntime.slaveToolDefs query has args schema", fun () ->
        let defs = slaveToolDefs testConfig
        let queryDef = get defs "query_squad"
        check (not (isNullish (get queryDef "args"))))

    ("readSlaveConfig without env returns None", fun () ->
        let env = getEnv ()
        let orig = str env "SQUAD_COORDINATOR_URL"
        setKey env "SQUAD_COORDINATOR_URL" (box "")
        let cfg = readSlaveConfig ()
        equal None cfg
        // restore
        if orig <> "" then setKey env "SQUAD_COORDINATOR_URL" (box orig))

    ("readSlaveConfig with env returns Some config", fun () ->
        let env = getEnv ()
        let origUrl = str env "SQUAD_COORDINATOR_URL"
        let origTid = str env "SQUAD_TASK_ID"
        let origWt = str env "SQUAD_WORKTREE_PATH"
        let origMb = str env "SQUAD_MASTER_BRANCH"
        let origTk = str env "SQUAD_TOKEN"
        // set
        setKey env "SQUAD_COORDINATOR_URL" (box "http://test:12345")
        setKey env "SQUAD_TASK_ID" (box "task-test")
        setKey env "SQUAD_WORKTREE_PATH" (box "/tmp/wt")
        setKey env "SQUAD_MASTER_BRANCH" (box "main")
        setKey env "SQUAD_TOKEN" (box "tok")
        match readSlaveConfig () with
        | Some cfg ->
            equal "http://test:12345" cfg.CoordinatorUrl
            equal "task-test" cfg.TaskId
            equal "/tmp/wt" cfg.WorktreePath
            equal "main" cfg.MasterBranch
            equal "tok" cfg.Token
        | None -> check false
        // restore
        setKey env "SQUAD_COORDINATOR_URL" (box origUrl)
        setKey env "SQUAD_TASK_ID" (box origTid)
        setKey env "SQUAD_WORKTREE_PATH" (box origWt)
        setKey env "SQUAD_MASTER_BRANCH" (box origMb)
        setKey env "SQUAD_TOKEN" (box origTk))
]

// ══════════════════════════════════════════════════════════════════════════════
// Async tests
// ══════════════════════════════════════════════════════════════════════════════

let entriesAsync () : (string * (unit -> JS.Promise<unit>)) list = [
    ("SlaveRuntime.submitToSquad returns LocalGitError for invalid worktree", fun () ->
        promise {
            let cfg = { testConfig with WorktreePath = "/nonexistent/worktree/path" }
            let! outcome = submitToSquad cfg
            match outcome with
            | LocalGitError _ -> check true
            | _               -> check false
        })
]
