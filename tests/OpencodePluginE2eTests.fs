module Wanxiangzhen.Tests.OpencodePluginE2eTests

open Fable.Core
open Fable.Core.JsInterop
open Wanxiangzhen.Plugin
open Wanxiangzhen.Shell.CoordinatorRuntime
open Wanxiangzhen.Shell.CoordinatorOps
open Wanxiangzhen.Shell.Dyn
open Wanxiangzhen.Shell.HttpServer
open Wanxiangzhen.Shell.HttpCodec
open Wanxiangzhen.Shell.SerialQueue
open Wanxiangzhen.Kernel.Task
open Wanxiangzhen.Kernel.Dag
open Wanxiangzhen.Kernel.SquadConfig
open Wanxiangzhen.Shell.SlaveRuntime
open Wanxiangzhen.Tests.Assert
open Wanxiangzhen.Tests.TestFixtures

// ══════════════════════════════════════════════════════════════════════════════
// Process / env helpers (slave mode needs SQUAD_* env vars)
// ══════════════════════════════════════════════════════════════════════════════

[<Global("process")>]
let private nodeProcess : obj = jsNative

let private setEnv (key: string) (value: string) : unit =
    setKey (get nodeProcess "env") key (box value)

let private clearEnv (key: string) : unit =
    setKey (get nodeProcess "env") key (box "")

// ══════════════════════════════════════════════════════════════════════════════
// MockCaptures — records calls made through the mock PluginInput client
// ══════════════════════════════════════════════════════════════════════════════

type MockCaptures = {
    mutable prompts   : obj list
    mutable commands  : obj list
    mutable messages  : obj list
}

// ══════════════════════════════════════════════════════════════════════════════
// mkMockInput — builds an opencode-shaped PluginInput with captured client calls
// ══════════════════════════════════════════════════════════════════════════════

let mkMockInput (captures: MockCaptures) : obj =
    let client = createObj [
        "session", box (createObj [
            "prompt",   box (fun (p: obj) ->
                captures.prompts <- captures.prompts @ [ p ]
                Promise.lift (box null))
            "messages", box (fun (m: obj) ->
                captures.messages <- captures.messages @ [ m ]
                Promise.lift (box {| data = [||] |}))
            "command",  box (fun (c: obj) ->
                captures.commands <- captures.commands @ [ c ]
                Promise.lift (box null))
        ])
    ]
    createObj [
        "client",                box client
        "directory",             box "/tmp/project"
        "worktree",              box "/tmp/project"
        "serverUrl",             box "http://localhost:0"
        "experimental_workspace", box (createObj [ "register", box (fun _ _ -> ()) ])
        "project",               box (createObj [])
        "$",                     box (createObj [])
    ]

// ══════════════════════════════════════════════════════════════════════════════
// ObservableDeps — mirrors CoordinatorDeps, each field records the call
// ══════════════════════════════════════════════════════════════════════════════

type ObservableDeps = {
    // call-records
    mutable spawnSlaveCalls           : (string * string * obj * string) list
    mutable killPidCalls              : (int * obj) list
    mutable worktreeAddCalls          : (string * string * string * string) list
    mutable worktreeRemoveCalls       : (string * string) list
    mutable branchDeleteCalls         : (string * string) list
    mutable mergeBaseCalls            : (string * string * string) list
    mutable mergeFfCalls              : (string * string) list
    mutable revParseRefCalls          : (string * string) list
    mutable showRefExistsCalls        : (string * string) list
    // configurable return values
    mutable revParseBranchResult      : string
    mutable revParseRefResult         : string
    mutable revParseRefOverrides      : Map<string, string>
    mutable showRefExistsResult       : bool
    mutable mergeBaseResult           : bool
    mutable mergeFfResult             : string
    mutable isPidAliveResult          : bool
    mutable nowResult                 : string
}

let mkDefaultObs () : ObservableDeps =
    { spawnSlaveCalls       = []
      killPidCalls          = []
      worktreeAddCalls      = []
      worktreeRemoveCalls   = []
      branchDeleteCalls     = []
      mergeBaseCalls        = []
      mergeFfCalls          = []
      revParseRefCalls      = []
      showRefExistsCalls    = []
      revParseBranchResult  = "main"
      revParseRefResult     = "deadbeef"
      revParseRefOverrides  = Map.empty
      showRefExistsResult   = false
      mergeBaseResult       = true
      mergeFfResult         = "merged-sha"
      isPidAliveResult      = true
      nowResult             = "2025-01-01T00:00:00Z" }

let mkObservableDeps (captures: MockCaptures) (obs: ObservableDeps) : CoordinatorDeps =
    let baseDeps = stubDeps ()
    { baseDeps with
        PromptSession       = fun (client: obj) (sessionId: string) (msg: string) ->
            let part  = createObj [ "type", box "text"; "text", box msg ]
            let arg   = createObj [
                "path",  box (createObj [ "id", box sessionId ])
                "body",  box (createObj [ "parts", box [| part |] ]) ]
            let session = get client "session"
            session?("prompt")(arg) |> unbox<JS.Promise<obj>> |> Promise.map ignore
        SpawnSlave          = fun t wt e p -> obs.spawnSlaveCalls <- obs.spawnSlaveCalls @ [ (t, wt, e, p) ]
        KillPid             = fun p signal  -> obs.killPidCalls      <- obs.killPidCalls      @ [ (p, signal) ]
        TryWorktreeAdd      = fun c b p b2 -> obs.worktreeAddCalls   <- obs.worktreeAddCalls   @ [ (c, b, p, b2) ]; Ok ""
        TryWorktreeRemoveForce = fun c p -> obs.worktreeRemoveCalls <- obs.worktreeRemoveCalls @ [ (c, p) ]; Ok ""
        TryBranchDeleteForce  = fun c b -> obs.branchDeleteCalls   <- obs.branchDeleteCalls   @ [ (c, b) ]; Ok ""
        MergeBaseIsAncestor = fun c a d ->
            obs.mergeBaseCalls <- obs.mergeBaseCalls @ [ (c, a, d) ]; obs.mergeBaseResult
        MergeFfOnly         = fun c b ->
            obs.mergeFfCalls   <- obs.mergeFfCalls   @ [ (c, b) ]; obs.mergeFfResult
        RevParseRef         = fun c r ->
            obs.revParseRefCalls <- obs.revParseRefCalls @ [ (c, r) ]
            match obs.revParseRefOverrides.TryGetValue r with
            | true, v -> v
            | false, _ -> obs.revParseRefResult
        ShowRefExists       = fun c b ->
            obs.showRefExistsCalls <- obs.showRefExistsCalls @ [ (c, b) ]; obs.showRefExistsResult
        RevParseBranch      = fun _ -> obs.revParseBranchResult
        IsPidAlive          = fun _ -> obs.isPidAliveResult
        Now                 = fun () -> obs.nowResult }

// ══════════════════════════════════════════════════════════════════════════════
// waitForScheduler — polls rt.Dag until a task transitions Pending→Running
// ══════════════════════════════════════════════════════════════════════════════

let private waitForScheduler (rt: CoordinatorRuntime) (taskId: string) : JS.Promise<unit> =
    let rec loop remaining =
        if remaining <= 0 then
            check false
            Promise.lift ()
        else
            match rt.Dag.Tasks |> Map.tryFind taskId with
            | Some t ->
                if t.Status = Running then Promise.lift ()
                else Promise.sleep 10 |> Promise.bind (fun () -> loop (remaining - 1))
            | None ->
                Promise.sleep 10 |> Promise.bind (fun () -> loop (remaining - 1))
    loop 50

// ══════════════════════════════════════════════════════════════════════════════
// Test 1 — plugin_with_deps returns hooks containing expected keys
// ══════════════════════════════════════════════════════════════════════════════

let testPluginHooksShape () : JS.Promise<unit> =
    promise {
        let captures = { prompts=[]; commands=[]; messages=[] }
        let input    = mkMockInput captures
        let obs = mkDefaultObs ()
        let deps = mkObservableDeps captures obs
        let! result = pluginWithDeps input deps
        let h = result.hooks
        check (not (isNullish (get h "tool")))
        check (not (isNullish (get h "config")))
        check (not (isNullish (get h "command.execute.before")))
        check (not (isNullish (get h "dispose")))
    }

// ══════════════════════════════════════════════════════════════════════════════
// Test 2 — config hook registers /squad /squad-kill /squad-status commands
// ══════════════════════════════════════════════════════════════════════════════

let testConfigHookRegistersCommands () : JS.Promise<unit> =
    promise {
        let captures = { prompts=[]; commands=[]; messages=[] }
        let input    = mkMockInput captures
        let obs = mkDefaultObs ()
        let deps = mkObservableDeps captures obs
        let! result = pluginWithDeps input deps
        let configHook = get result.hooks "config"
        let cfg = createObj [ "command", box (createObj []) ]
        do! unbox<JS.Promise<unit>> (configHook $ (cfg))
        let cmds = get cfg "command"
        check (not (isNullish (get cmds "squad")))
        check (not (isNullish (get cmds "squad-kill")))
        check (not (isNullish (get cmds "squad-status")))
        let squadCmd = get cmds "squad"
        check ((str squadCmd "template") <> "")
        check ((str squadCmd "description") <> "")
    }

// ══════════════════════════════════════════════════════════════════════════════
// Test 3 — dispose returns a thenable
// ══════════════════════════════════════════════════════════════════════════════

let testDisposeReturnsPromise () : JS.Promise<unit> =
    promise {
        let captures = { prompts=[]; commands=[]; messages=[] }
        let input    = mkMockInput captures
        let obs = mkDefaultObs ()
        let deps = mkObservableDeps captures obs
        let! result = pluginWithDeps input deps
        let dispose = get result.hooks "dispose"
        do! unbox<JS.Promise<unit>> (dispose $ ())
    }

// ══════════════════════════════════════════════════════════════════════════════
// Test 4 — /squad command writes a squad_created frontmatter event
// ══════════════════════════════════════════════════════════════════════════════

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
        check (parts.Count = 1)
        let text = str parts.[0] "text"
        check (text.Contains "squad_event: squad_created")
        check (text.Contains "add remember-me")

        check (rt.MasterSessionId = "sess-e2e")
    }

// ══════════════════════════════════════════════════════════════════════════════
// Test 5 — full flow: /squad → squad_update → schedule → register → merged
// ══════════════════════════════════════════════════════════════════════════════

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
        let evtObj = createObj [
            "type",        box "task_created"
            "taskId",      box "squad-e2e-01"
            "title",       box "T"
            "description", box "D"
            "dependsOn",   box (Array.empty<string>)
        ]
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

        check (obs.worktreeAddCalls.Length = 1)
        check (obs.spawnSlaveCalls.Length = 1)

        // ④ POST /task/squad-e2e-01/register
        let! regResp = routeHandler rt "POST" "/task/squad-e2e-01/register" (createObj [ "pid", box 12345 ])
        check (regResp.StatusCode = 200)
        check ((str regResp.Body "result") = "registered")

        // ⑤ set up git stubs for ff
        obs.revParseRefOverrides <- obs.revParseRefOverrides.Add("squad-e2e-01", "abc")
        obs.mergeBaseResult  <- true
        obs.mergeFfResult    <- "merged-sha"

        // ⑥ POST /task/squad-e2e-01/submit
        let! subResp = routeHandler rt "POST" "/task/squad-e2e-01/submit" (createObj [ "commitSha", box "abc" ])

        check (subResp.StatusCode = 200)
        check ((str subResp.Body "result") = "merged")

        // ⑦ assertions on DAG + side-effects

        match rt.Dag.Tasks |> Map.tryFind "squad-e2e-01" with
        | Some t -> check (t.Status = Merged)
        | None   -> check false

        check (obs.worktreeRemoveCalls.Length = 1)
        check (obs.branchDeleteCalls.Length   = 1)

        // ⑧ verify task_merged was injected into prompts
        let mergedPrompt =
            captures.prompts
            |> List.tryFind (fun p ->
                let parts = get p "body" |> fun b -> get b "parts" :?> obj array
                parts |> Array.exists (fun part -> (str part "text").Contains "task_merged"))

        check (mergedPrompt.IsSome)
    }

// ══════════════════════════════════════════════════════════════════════════════
// Test 6 — squad_update with squad_cancelled cancels a running task,
//           KillPid called, exactly one squad_cancelled event in prompts
// ══════════════════════════════════════════════════════════════════════════════

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
        let evtObj = createObj [
            "type",        box "task_created"
            "taskId",      box "squad-cancel-01"
            "title",       box "Cancel-Test"
            "description", box "desc"
            "dependsOn",   box (Array.empty<string>)
        ]
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
        | Some t -> check (t.Status = Cancelled)
        | None   -> check false

        // ⑦ verify KillPid was called (non-empty)
        check (obs.killPidCalls.Length > 0)

        // ⑧ verify exactly one squad_cancelled event in prompts
        let cancelPrompts =
            captures.prompts
            |> List.choose (fun p ->
                let parts = get p "body" |> fun b -> get b "parts" :?> obj array
                parts |> Array.tryPick (fun part ->
                    let text = str part "text"
                    if text.Contains "squad_cancelled" then Some text else None))
        check (cancelPrompts.Length = 1)
    }

// ══════════════════════════════════════════════════════════════════════════════
// Test 7 — /squad-kill cancels running task, KillPid called, no worktree/branch cleanup
// ══════════════════════════════════════════════════════════════════════════════

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

        let evtObj = createObj [
            "type",        box "task_created"
            "taskId",      box "squad-kill-01"
            "title",       box "Kill-Test"
            "description", box "desc"
            "dependsOn",   box (Array.empty<string>)
        ]
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
        check (obs.killPidCalls.Length > 0)
        check (obs.worktreeRemoveCalls = [])
        check (obs.branchDeleteCalls   = [])

        match rt.Dag.Tasks |> Map.tryFind "squad-kill-01" with
        | Some t -> check (t.Status = Cancelled)
        | None   -> check false
    }

// ══════════════════════════════════════════════════════════════════════════════
// Test 7 — slave mode: plugin returns submit_to_squad + query_squad tools;
//           query_squad "state" hits coordinator HTTP server and returns DAG
// ══════════════════════════════════════════════════════════════════════════════

let testSlaveModeQuerySquad () : JS.Promise<unit> =
    promise {
        // ── ① spin up a coordinator with one Running task ──────────────────────
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

        let evtObj = createObj [
            "type",        box "task_created"
            "taskId",      box "squad-query-01"
            "title",       box "Query-Test"
            "description", box "desc"
            "dependsOn",   box (Array.empty<string>)
        ]
        let updateArgs = createObj [ "events", box [| evtObj |] ]
        let sqUp       = get (get hooks "tool") "squad_update"
        let sqExec     = get sqUp "execute"
        let sqExecFn   = unbox<System.Func<obj, obj, JS.Promise<string>>> sqExec
        rt.Scheduling <- true
        let! _ = sqExecFn.Invoke(updateArgs, createObj [])
        rt.Scheduling <- false
        do! schedulerTick rt
        do! waitForScheduler rt "squad-query-01"

        check (obs.worktreeAddCalls.Length = 1)
        check (obs.spawnSlaveCalls.Length  = 1)

        // ── ② capture coordinator URL + token for slave env ────────────────────
        let coordinatorUrl   = rt.CoordinatorUrl   // e.g. "http://127.0.0.1:<port>"
        let coordinatorToken = rt.Token

        // ── ③ set SQUAD_* env → triggers slave mode in plugin() ───────────────────
        setEnv "SQUAD_COORDINATOR_URL" coordinatorUrl
        setEnv "SQUAD_TASK_ID"        "squad-query-01"
        setEnv "SQUAD_WORKTREE_PATH"  "/tmp/wt-query-01"
        setEnv "SQUAD_MASTER_BRANCH"  "main"
        setEnv "SQUAD_TOKEN"          coordinatorToken

        try
            // ── ⑤ call plugin() in slave mode ─────────────────────────────────
            // slavePlugin runs synchronously up to registerPid (async, fire-and-forget),
            // then returns hooks dict containing submit_to_squad + query_squad.
            let slaveCtx = createObj [
                "client",    box (createObj [])
                "directory", box "/tmp/wt-query-01"
                "worktree",  box "/tmp/wt-query-01"
            ]
            let! slaveResult = plugin slaveCtx
            let slaveHooks   = slaveResult
            let tools        = get slaveHooks "tool"

            // ── ⑥ hooks must contain both slave tools ───────────────────────────
            check (not (isNullish (get tools "submit_to_squad")))
            check (not (isNullish (get tools "query_squad")))

            // ── ⑦ execute query_squad "state" — hits coordinator HTTP server ────
            let qsTool    = get tools "query_squad"
            let qsExecute = get qsTool "execute"
            let qsExecFn  = unbox<System.Func<obj, obj, JS.Promise<string>>> qsExecute
            let qsArgs    = createObj [ "query", box "state" ]
            let! qsResp   = qsExecFn.Invoke(qsArgs, createObj [])

            // Response is the JSON text from GET /state: must contain our task id
            check (qsResp.Contains "squad-query-01")
        finally
            clearEnv "SQUAD_COORDINATOR_URL"
            clearEnv "SQUAD_TASK_ID"
            clearEnv "SQUAD_WORKTREE_PATH"
            clearEnv "SQUAD_MASTER_BRANCH"
            clearEnv "SQUAD_TOKEN"
    }

// ══════════════════════════════════════════════════════════════════════════════
// Test 8 — squad_update without taskId generates two distinct squad- IDs
// ══════════════════════════════════════════════════════════════════════════════

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

        // squad_update with two task_created events, both omit taskId
        let mkEvt (title: string) (desc: string) : obj =
            createObj [
                "type",        box "task_created"
                "title",       box title
                "description", box desc
                "dependsOn",   box (Array.empty<string>)
            ]
        let evts = createObj [ "events", box [| mkEvt "T1" "D1"; mkEvt "T2" "D2" |] ]
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

// ══════════════════════════════════════════════════════════════════════════════
// Test 9 — collision retry exhaustion: ShowRefExists always true, taskId omitted,
//           genWithRetries exhausts 10 attempts, fallback generates task anyway
// ══════════════════════════════════════════════════════════════════════════════

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
        let evtObj = createObj [
            "type",        box "task_created"
            "title",       box "Collision-Test"
            "description", box "Verify fallback after 10 ref-collision retries"
            "dependsOn",   box (Array.empty<string>)
        ]
        let updateArgs = createObj [ "events", box [| evtObj |] ]
        let sqUp       = get (get hooks "tool") "squad_update"
        let execute    = get sqUp "execute"
        let execFn     = unbox<System.Func<obj, obj, JS.Promise<string>>> execute
        rt.Scheduling <- true
        let! _ = execFn.Invoke(updateArgs, createObj [])
        rt.Scheduling <- false
        do! schedulerTick rt

        // ③ assertions

        // genWithRetries is called once per ID attempt, 10 retries total → >= 10 calls
        check (obs.showRefExistsCalls.Length >= 10)

        // Task must still be created even though every generated ID "collided".
        // The auto-generated taskId is random; find it by title in the DAG.
        let collisionTask =
            rt.Dag.Tasks
            |> Map.toList
            |> List.tryFind (fun (_, t) -> t.Title = "Collision-Test")
        check (collisionTask.IsSome)
        match collisionTask with
        | Some (_, t) -> check (t.Status = Running)  // schedulerTick starts ready tasks → Running
        | None   -> check false
     }

// ══════════════════════════════════════════════════════════════════════════════
// Public entries — all 10 tests
// ══════════════════════════════════════════════════════════════════════════════

let entriesAsync () : (string * (unit -> JS.Promise<unit>)) list = [
    ("E2E.plugin_hooks_shape: pluginWithDeps returns hooks dict with expected keys",
     testPluginHooksShape)

    ("E2E.config_hook_registers_commands: config hook writes squad / squad-kill / squad-status",
     testConfigHookRegistersCommands)

    ("E2E.dispose_returns_promise: dispose returns thenable",
     testDisposeReturnsPromise)

    ("E2E.squad_command_creates_session: /squad command injects squad_created frontmatter",
     testSquadCommandCreatesSession)

    ("E2E.full_flow_squad_update_to_merged: /squad → squad_update → schedule → register → submit → merged",
     testFullFlowSquadUpdateToMerged)

    ("E2E.squad_update_cancels_running_task: squad_update with squad_cancelled cancels running task; KillPid called; exactly one squad_cancelled event in prompts",
     testSquadUpdateCancelsRunningTask)

    ("E2E.squad_kill_cancels_without_cleanup: /squad-kill cancels, KillPid called, no worktree/branch deletion",
     testSquadKillCancelsWithoutCleanup)

    ("E2E.slave_mode_query_squad: slave plugin returns submit_to_squad+query_squad; query_squad 'state' hits coordinator HTTP server and returns task",
     testSlaveModeQuerySquad)

    ("E2E.squad_update_generates_unique_ids: omitting taskId produces two distinct squad- IDs",
      testSquadUpdateGeneratesUniqueIds)

    ("E2E.collision_retry_exhaustion: ShowRefExists always true, genWithRetries exhausts 10 attempts, task still created via fallback",
      testSquadUpdateRetriesGeneratedIdOnRefCollision)
]
