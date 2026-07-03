module Wanxiangzhen.Tests.OpencodePluginE2eCancelKillTests

open Fable.Core
open Fable.Core.JsInterop
open Wanxiangzhen.Shell.Dyn
open Wanxiangzhen.Plugin
open Wanxiangzhen.Shell.CoordinatorOps
open Wanxiangzhen.Kernel.Task
open Wanxiangzhen.Kernel.SquadEvent
open Wanxiangzhen.Tests.Assert
open Wanxiangzhen.Tests.TestDoubles
open Wanxiangzhen.Tests.OpencodePluginE2eHelpers

// Test 6 — squad_update with squad_cancelled cancels a running task,
//           KillPid called, exactly one squad_cancelled event in prompts
let testSquadUpdateCancelsRunningTask () : JS.Promise<unit> =
    promise {
        let captures = { prompts=[]; commands=[]; messages=[] }
        let input    = mkMockInput captures
        let obs      = mkDefaultObs ()
        let deps     = mkObservableDeps captures obs
        let! result  = pluginWithDeps input deps
        let rt       = result.runtime
        let hooks    = result.hooks

        // ① /squad command — captures masterSessionId
        let cmdInput  = createObj [ "command", box "squad"; "sessionID", box "sess-cancel"; "arguments", box "cancel-test" ]
        let cmdOutput = createObj [ "parts", box (System.Collections.Generic.List<obj>()) ]
        let cmdHook   = get hooks "command.execute.before"
        do! unbox<JS.Promise<unit>> (cmdHook $ (cmdInput, cmdOutput))

        // ② squad_update creates one task
        let evtObj = mkTasksCreated [ mkTask "squad-cancel-01" "Cancel-Test" "desc" [] ]
        let updateArgs = createObj [ "events", box [| evtObj |] ]
        let sqUp       = get (get hooks "tool") "squad_update"
        let sqExec     = get sqUp "execute"
        let sqExecFn   = unbox<System.Func<obj, obj, JS.Promise<string>>> sqExec
        rt.Scheduling <- true
        let! _ = sqExecFn.Invoke(updateArgs, createObj [])
        rt.Scheduling <- false
        do! schedulerTick rt

        // ③ wait for scheduler to start the task → Running
        do! waitForScheduler rt "squad-cancel-01"

        // ④ register a PID so KillPid path is exercised
        let pid = 55555
        let! _ = routeHandler rt "POST" "/task/squad-cancel-01/register" (createObj [ "pid", box pid ])

        // ⑤ squad_update with squad_cancelled event
        let cancelEvt = createObj [
            "type", box "squad_cancelled"
        ]
        let cancelArgs = createObj [ "events", box [| cancelEvt |] ]
        rt.Scheduling <- true
        let! _ = sqExecFn.Invoke(cancelArgs, createObj [])
        rt.Scheduling <- false

        // ⑥ verify task status = Cancelled
        match rt.Dag.Tasks |> Map.tryFind "squad-cancel-01" with
        | Some t -> checkBare (t.Status = Cancelled)
        | None   -> checkBare false

        // ⑦ verify KillPid was called (non-empty)
        checkBare (obs.killPidCalls.Length > 0)

        checkBare (obs.squadEventLog |> List.filter (function SquadCancelled _ -> true | _ -> false) |> List.length = 1)
    }

// Test 7 — /squad-kill cancels running task, KillPid called, no worktree/branch cleanup
let testSquadKillCancelsWithoutCleanup () : JS.Promise<unit> =
    promise {
        let captures = { prompts=[]; commands=[]; messages=[] }
        let input    = mkMockInput captures
        let obs = mkDefaultObs ()
        let deps = mkObservableDeps captures obs
        let! result = pluginWithDeps input deps
        let rt = result.runtime
        let hooks = result.hooks

        // ① create a running task and register a pid
        let cmdInput  = createObj [ "command", box "squad"; "sessionID", box "sess-kill"; "arguments", box "test" ]
        let cmdOutput = createObj [ "parts", box (System.Collections.Generic.List<obj>()) ]
        let cmdHook = get hooks "command.execute.before"
        do! unbox<JS.Promise<unit>> (cmdHook $ (cmdInput, cmdOutput))

        let evtObj = mkTasksCreated [ mkTask "squad-kill-01" "Kill-Test" "desc" [] ]
        let updateArgs = createObj [ "events", box [| evtObj |] ]
        let sqUp = get (get hooks "tool") "squad_update"
        let execute = get sqUp "execute"
        let executeFn = unbox<System.Func<obj, obj, JS.Promise<string>>> execute
        rt.Scheduling <- true
        let! _ = executeFn.Invoke(updateArgs, createObj [])
        rt.Scheduling <- false
        do! schedulerTick rt
        do! waitForScheduler rt "squad-kill-01"

        let pid = 98765
        let! _ = routeHandler rt "POST" "/task/squad-kill-01/register" (createObj [ "pid", box pid ])

        // ② /squad-kill
        let killInput  = createObj [ "command", box "squad-kill"; "sessionID", box ""; "arguments", box "" ]
        let killOutput = createObj [ "parts", box (System.Collections.Generic.List<obj>()) ]
        do! unbox<JS.Promise<unit>> (cmdHook $ (killInput, killOutput))

        // ③ assertions: Cancelled, KillPid called, no worktree/branch cleanup
        checkBare (obs.killPidCalls.Length > 0)
        checkBare (obs.worktreeRemoveCalls = [])
        checkBare (obs.branchDeleteCalls   = [])

        match rt.Dag.Tasks |> Map.tryFind "squad-kill-01" with
        | Some t -> checkBare (t.Status = Cancelled)
        | None   -> checkBare false
    }

let entriesAsync () : (string * (unit -> JS.Promise<unit>)) list = [
    ("E2E.squad_update_cancels_running_task: squad_update with squad_cancelled cancels running task; KillPid called; exactly one squad_cancelled event in prompts",
     testSquadUpdateCancelsRunningTask)

    ("E2E.squad_kill_cancels_without_cleanup: /squad-kill cancels, KillPid called, no worktree/branch deletion",
     testSquadKillCancelsWithoutCleanup)
]
