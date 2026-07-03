module Wanxiangzhen.Tests.OpencodePluginE2eSlaveQueryTests

open Fable.Core
open Fable.Core.JsInterop
open Wanxiangzhen.Shell.Dyn
open Wanxiangzhen.Plugin
open Wanxiangzhen.Shell.CoordinatorOps
open Wanxiangzhen.Kernel.Task
open Wanxiangzhen.Tests.Assert
open Wanxiangzhen.Tests.TestDoubles
open Wanxiangzhen.Tests.OpencodePluginE2eHelpers

// Test 8 — slave mode: plugin returns submit_to_squad + query_squad tools;
//           query_squad "state" hits coordinator HTTP server and returns DAG
let testSlaveModeQuerySquad () : JS.Promise<unit> =
    promise {
        // ① spin up a coordinator with one Running task
        let captures = { prompts=[]; commands=[]; messages=[] }
        let obs      = mkDefaultObs ()
        let deps     = mkObservableDeps captures obs
        let input    = mkMockInput captures
        let! result  = pluginWithDeps input deps
        let rt       = result.runtime
        let hooks    = result.hooks

        // /squad + squad_update → one task
        let cmdInput  = createObj [ "command", box "squad"; "sessionID", box "sess-slave-e2e"; "arguments", box "slave e2e" ]
        let cmdOutput = createObj [ "parts", box (System.Collections.Generic.List<obj>()) ]
        let cmdHook   = get hooks "command.execute.before"
        do! unbox<JS.Promise<unit>> (cmdHook $ (cmdInput, cmdOutput))

        let evtObj = mkTasksCreated [ mkTask "squad-query-01" "Query-Test" "desc" [] ]
        let updateArgs = createObj [ "events", box [| evtObj |] ]
        let sqUp       = get (get hooks "tool") "squad_update"
        let sqExec     = get sqUp "execute"
        let sqExecFn   = unbox<System.Func<obj, obj, JS.Promise<string>>> sqExec
        rt.Scheduling <- true
        let! _ = sqExecFn.Invoke(updateArgs, createObj [])
        rt.Scheduling <- false
        do! schedulerTick rt
        do! waitForScheduler rt "squad-query-01"

        checkBare (obs.worktreeAddCalls.Length = 1)
        checkBare (obs.spawnSlaveCalls.Length  = 1)

        // ② capture coordinator URL + token for slave env
        let coordinatorUrl   = rt.CoordinatorUrl
        let coordinatorToken = rt.Token

        // ③ set SQUAD_* env → triggers slave mode in plugin()
        setEnv "SQUAD_COORDINATOR_URL" coordinatorUrl
        setEnv "SQUAD_TASK_ID"        "squad-query-01"
        setEnv "SQUAD_WORKTREE_PATH"  "/tmp/wt-query-01"
        setEnv "SQUAD_MASTER_BRANCH"  "main"
        setEnv "SQUAD_TOKEN"          coordinatorToken

        try
            // ⑤ call plugin() in slave mode
            let slaveCtx = createObj [
                "client",    box (createObj [])
                "directory", box "/tmp/wt-query-01"
                "worktree",  box "/tmp/wt-query-01"
            ]
            let! slaveResult = plugin slaveCtx
            let slaveHooks   = slaveResult
            let tools        = get slaveHooks "tool"

            // ⑥ hooks must contain both slave tools
            checkBare (not (isNullish (get tools "submit_to_squad")))
            checkBare (not (isNullish (get tools "query_squad")))

            // ⑦ execute query_squad "state" — hits coordinator HTTP server
            let qsTool    = get tools "query_squad"
            let qsExecute = get qsTool "execute"
            let qsExecFn  = unbox<System.Func<obj, obj, JS.Promise<string>>> qsExecute
            let qsArgs    = createObj [ "query", box "state" ]
            let! qsResp   = qsExecFn.Invoke(qsArgs, createObj [])

            checkBare (qsResp.Contains "squad-query-01")
        finally
            clearEnv "SQUAD_COORDINATOR_URL"
            clearEnv "SQUAD_TASK_ID"
            clearEnv "SQUAD_WORKTREE_PATH"
            clearEnv "SQUAD_MASTER_BRANCH"
            clearEnv "SQUAD_TOKEN"
    }

let entriesAsync () : (string * (unit -> JS.Promise<unit>)) list = [
    ("E2E.slave_mode_query_squad: slave plugin returns submit_to_squad+query_squad; query_squad 'state' hits coordinator HTTP server and returns task",
     testSlaveModeQuerySquad)
]
