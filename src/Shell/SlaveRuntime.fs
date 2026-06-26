module Wanxiangzhen.Shell.SlaveRuntime

open Fable.Core
open Fable.Core.JsInterop
open Wanxiangzhen.Kernel.FfDecision
open Wanxiangzhen.Shell.Dyn
open Wanxiangzhen.Shell.GitShell
open Wanxiangzhen.Shell.HttpCodec

[<Global("process")>]
let private nodeProcess : obj = jsNative

[<Global>]
let private JSON : obj = jsNative

[<Emit("fetch($0, $1)")>]
let private fetch (url: string) (init: obj) : JS.Promise<obj> = jsNative

[<Emit("$0.status")>]
let private resStatus (res: obj) : int = jsNative

[<Emit("$0.text()")>]
let private resText (res: obj) : JS.Promise<string> = jsNative

type SlaveConfig = {
    CoordinatorUrl: string
    TaskId: string
    WorktreePath: string
    MasterBranch: string
    Token: string
}

let private env (key: string) : string =
    let e = nodeProcess?("env")
    if isNullish e then "" else str e key

let readSlaveConfig () : SlaveConfig option =
    let url = env "SQUAD_COORDINATOR_URL"
    if url = "" then None
    else Some {
        CoordinatorUrl = url
        TaskId = env "SQUAD_TASK_ID"
        WorktreePath = env "SQUAD_WORKTREE_PATH"
        MasterBranch = env "SQUAD_MASTER_BRANCH"
        Token = env "SQUAD_TOKEN"
    }

let private authHeaders (cfg: SlaveConfig) : obj =
    createObj [ "Authorization", box ("Bearer " + cfg.Token) ]

let registerPid (cfg: SlaveConfig) : JS.Promise<unit> =
    promise {
        let url = cfg.CoordinatorUrl + "/task/" + cfg.TaskId + "/register"
        let body = createObj [ "pid", box nodeProcess?("pid") ]
        let init = createObj [
            "method", box "POST"
            "headers", box (authHeaders cfg)
            "body", box (string (JSON?("stringify")(body))) ]
        try
            let! _ = fetch url init
            ()
        with _ -> ()
    }

let doneBeacon (cfg: SlaveConfig) : JS.Promise<unit> =
    promise {
        let url = cfg.CoordinatorUrl + "/task/" + cfg.TaskId + "/done"
        let init = createObj [
            "method", box "POST"
            "headers", box (authHeaders cfg)
            "body", box "{}"
        ]
        try
            let! _ = fetch url init
            ()
        with _ -> ()
    }

let submitToSquad (cfg: SlaveConfig) : JS.Promise<string> =
    promise {
        let commitSha = revParseHead cfg.WorktreePath
        let url = cfg.CoordinatorUrl + "/task/" + cfg.TaskId + "/submit"
        let body = createObj [ "commitSha", box commitSha ]
        let init = createObj [
            "method", box "POST"
            "headers", box (authHeaders cfg)
            "body", box (string (JSON?("stringify")(body)))
        ]
        try
            let! res = fetch url init
            if resStatus res = 404 then
                return "Task not found on coordinator. Report to user and stop."
            else
                let! bodyText = resText res
                let parsed = JSON?("parse")(bodyText)
                match decodeFfResult parsed with
                | Some (Merged sha) ->
                    return sprintf "Merged into %s @ %s. Task complete." cfg.MasterBranch sha
                | Some (RebaseNeeded sha) ->
                    return sprintf "Cannot fast-forward. %s at %s. Run: git rebase %s. Then submit_to_squad again." cfg.MasterBranch sha cfg.MasterBranch
                | Some StaleCommit ->
                    return "Branch HEAD differs. Commit latest work, then submit_to_squad again."
                | Some (CoordinatorNotReady _) ->
                    return "Coordinator not ready. Wait and submit_to_squad again."
                | Some (NotSubmittable s) ->
                    return sprintf "Not submittable (status: %s). Report to user." s
                | None -> return "Unexpected coordinator response. Report to user."
        with _ ->
            return "Coordinator unreachable. Report to user and wait."
    }

let querySquad (cfg: SlaveConfig) (query: string) : JS.Promise<string> =
    promise {
        let url =
            if query = "state" then cfg.CoordinatorUrl + "/state"
            else cfg.CoordinatorUrl + "/task/" + query
        let init = createObj [ "headers", box (authHeaders cfg) ]
        try
            let! res = fetch url init
            let! body = resText res
            return body
        with _ ->
            return "Coordinator unreachable. Proceeding without global context."
    }

let slaveToolDefs (cfg: SlaveConfig) : obj =
    let submitDef = createObj [
        "description", box "Submit completed work to squad coordinator for fast-forward merge into the integration branch. Prerequisites: changes committed, review passed (if /loop is available). Success → merged. Failure → rebase needed."
        "execute", box (fun (_: obj) -> submitToSquad cfg)
    ]
    let queryDef = createObj [
        "description", box "Query squad coordinator for current DAG state or a specific task's details."
        "args", box (createObj [
            "query", box (createObj [
                "type", box "string"
                "description", box "'state' for full DAG view, or a task ID for that task's details"
            ])
        ])
        "execute", box (fun (args: obj) ->
            let q = str args "query"
            querySquad cfg q
        )
    ]
    createObj [
        "submit_to_squad", box submitDef
        "query_squad", box queryDef
    ]
