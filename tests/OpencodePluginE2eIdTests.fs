module Wanxiangzhen.Tests.OpencodePluginE2eIdTests

open Fable.Core
open Fable.Core.JsInterop
open Wanxiangzhen.Shell.Dyn
open Wanxiangzhen.Plugin
open Wanxiangzhen.Shell.CoordinatorOps
open Wanxiangzhen.Tests.Assert
open Wanxiangzhen.Tests.TestDoubles
open Wanxiangzhen.Tests.OpencodePluginE2eHelpers

// Test 9 — squad_update without taskId generates two distinct squad- IDs
let testSquadUpdateGeneratesUniqueIds () : JS.Promise<unit> =
    promise {
        let captures = { prompts=[]; commands=[]; messages=[] }
        let input    = mkMockInput captures
        let obs      = mkDefaultObs ()
        let deps     = mkObservableDeps captures obs
        let! result  = pluginWithDeps input deps
        let rt       = result.runtime
        let hooks    = result.hooks

        // fire /squad to capture masterSessionId
        let cmdInput  = createObj [ "command", box "squad"; "sessionID", box "sess-unique"; "arguments", box "unique-id-test" ]
        let cmdOutput = createObj [ "parts", box (System.Collections.Generic.List<obj>()) ]
        let cmdHook   = get hooks "command.execute.before"
        do! unbox<JS.Promise<unit>> (cmdHook $ (cmdInput, cmdOutput))

        // squad_update with two tasks, both omit taskId
        let mkT (title: string) (desc: string) : obj = mkTask "" title desc []
        let evts = createObj [ "events", box [| mkTasksCreated [ mkT "T1" "D1"; mkT "T2" "D2" ] |] ]
        let sqUp     = get (get hooks "tool") "squad_update"
        let execute  = get sqUp "execute"
        let execFn   = unbox<System.Func<obj, obj, JS.Promise<string>>> execute
        rt.Scheduling <- true
        let! _ = execFn.Invoke(evts, createObj [])
        rt.Scheduling <- false
        do! schedulerTick rt

        // collect task IDs from the DAG
        let taskIds =
            rt.Dag.Tasks
            |> Map.toList
            |> List.map fst
            |> List.filter (fun id -> id.StartsWith "squad-")

        check (List.length taskIds = 2)
        check (Set.ofList taskIds |> Set.count = 2)   // distinct
     }

// Test 10 — collision retry exhaustion: ShowRefExists always true → IdExhausted error
let testSquadUpdateRetriesGeneratedIdOnRefCollision () : JS.Promise<unit> =
    promise {
        let captures = { prompts=[]; commands=[]; messages=[] }
        let input    = mkMockInput captures
        let obs      = mkDefaultObs ()
        obs.showRefExistsResult <- true          // every generated ID collides
        let deps     = mkObservableDeps captures obs
        let! result  = pluginWithDeps input deps
        let rt       = result.runtime
        let hooks    = result.hooks

        // ① /squad to capture masterSessionId
        let cmdInput  = createObj [ "command", box "squad"; "sessionID", box "sess-collision"; "arguments", box "collision-test" ]
        let cmdOutput = createObj [ "parts", box (System.Collections.Generic.List<obj>()) ]
        let cmdHook   = get hooks "command.execute.before"
        do! unbox<JS.Promise<unit>> (cmdHook $ (cmdInput, cmdOutput))

        // ② squad_update with task_created, omitting taskId → triggers auto-generation
        let evtObj = mkTasksCreated [ mkTask "" "Collision-Test" "Verify fallback after 10 ref-collision retries" [] ]
        let updateArgs = createObj [ "events", box [| evtObj |] ]
        let sqUp       = get (get hooks "tool") "squad_update"
        let execute    = get sqUp "execute"
        let execFn     = unbox<System.Func<obj, obj, JS.Promise<string>>> execute
        rt.Scheduling <- true
        let! result = execFn.Invoke(updateArgs, createObj [])
        rt.Scheduling <- false

        check (result.Contains "unique task id")
        check (obs.showRefExistsCalls.Length >= 10)
        check (rt.Dag.Tasks.IsEmpty)
     }

let entriesAsync () : (string * (unit -> JS.Promise<unit>)) list = [
    ("E2E.squad_update_generates_unique_ids: omitting taskId produces two distinct squad- IDs",
      testSquadUpdateGeneratesUniqueIds)

    ("E2E.collision_retry_exhaustion: ShowRefExists always true returns IdExhausted, DAG unchanged",
      testSquadUpdateRetriesGeneratedIdOnRefCollision)
]
