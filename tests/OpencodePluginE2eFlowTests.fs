module Wanxiangzhen.Tests.OpencodePluginE2eFlowTests

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

// Test 4 — /squad command writes a squad_created frontmatter event
let testSquadCommandCreatesSession () : JS.Promise<unit> =
    promise {
        let captures = { prompts=[]; commands=[]; messages=[] }
        let input    = mkMockInput captures
        let obs = mkDefaultObs ()
        let deps = mkObservableDeps captures obs
        let! result = pluginWithDeps input deps
        let rt = result.runtime

        let cmdInput  = createObj [ "command", box "squad"; "sessionID", box "sess-e2e"; "arguments", box "add remember-me" ]
        let cmdOutput = createObj [ "parts", box (System.Collections.Generic.List<obj>()) ]
        let cmdHook = get result.hooks "command.execute.before"
        do! unbox<JS.Promise<unit>> (cmdHook $ (cmdInput, cmdOutput))

        let parts = get cmdOutput "parts" :?> System.Collections.Generic.List<obj>
        checkBare (parts.Count = 1)
        let text = str parts.[0] "text"
        checkBare (text.Contains "squad_event: squad_created")
        checkBare (text.Contains "add remember-me")

        checkBare (rt.MasterSessionId = "sess-e2e")
    }

// Test 5 — full flow: /squad → squad_update → schedule → register → merged
let testFullFlowSquadUpdateToMerged () : JS.Promise<unit> =
    promise {
        let captures = { prompts=[]; commands=[]; messages=[] }
        let input    = mkMockInput captures
        let obs = mkDefaultObs ()
        let deps = mkObservableDeps captures obs
        let! result = pluginWithDeps input deps
        let rt = result.runtime
        let hooks = result.hooks

        // ① /squad command
        let cmdInput  = createObj [ "command", box "squad"; "sessionID", box "sess-e2e-01"; "arguments", box "add remember-me" ]
        let cmdOutput = createObj [ "parts", box (System.Collections.Generic.List<obj>()) ]
        let cmdHook = get hooks "command.execute.before"
        do! unbox<JS.Promise<unit>> (cmdHook $ (cmdInput, cmdOutput))

        // ② squad_update with one task
        let evtObj = mkTasksCreated [ mkTask "squad-e2e-01" "T" "D" [] ]
        let updateArgs = createObj [ "events", box [| evtObj |] ]
        let tool = get hooks "tool"
        let sqUp = get tool "squad_update"
        let execute = get sqUp "execute"
        let executeFn = unbox<System.Func<obj, obj, JS.Promise<string>>> execute
        rt.Scheduling <- true
        let! _ = executeFn.Invoke(updateArgs, createObj [])
        rt.Scheduling <- false
        do! schedulerTick rt

        ()

        // ③ wait for scheduler to start the task
        do! waitForScheduler rt "squad-e2e-01"

        checkBare (obs.worktreeAddCalls.Length = 1)
        checkBare (obs.spawnSlaveCalls.Length = 1)

        // ④ POST /task/squad-e2e-01/register
        let! regResp = routeHandler rt "POST" "/task/squad-e2e-01/register" (createObj [ "pid", box 12345 ])
        checkBare (regResp.StatusCode = 200)
        checkBare ((str regResp.Body "result") = "registered")

        // ⑤ set up git stubs for ff
        obs.revParseRefOverrides <- obs.revParseRefOverrides.Add("squad-e2e-01", "abc")
        obs.mergeBaseResult  <- true
        obs.mergeFfResult    <- "merged-sha"

        // ⑥ POST /task/squad-e2e-01/submit
        let! subResp = routeHandler rt "POST" "/task/squad-e2e-01/submit" (createObj [ "commitSha", box "abc" ])

        checkBare (subResp.StatusCode = 200)
        checkBare ((str subResp.Body "result") = "merged")

        match rt.Dag.Tasks |> Map.tryFind "squad-e2e-01" with
        | Some t -> checkBare (t.Status = Merged)
        | None   -> checkBare false

        checkBare (obs.worktreeRemoveCalls.Length = 1)
        checkBare (obs.branchDeleteCalls.Length   = 1)
        checkBare (obs.squadEventLog |> List.exists (function TaskMerged _ -> true | _ -> false))
    }

let entriesAsync () : (string * (unit -> JS.Promise<unit>)) list = [
    ("E2E.squad_command_creates_session: /squad command injects squad_created frontmatter",
     testSquadCommandCreatesSession)

    ("E2E.full_flow_squad_update_to_merged: /squad → squad_update → schedule → register → submit → merged",
     testFullFlowSquadUpdateToMerged)
]
