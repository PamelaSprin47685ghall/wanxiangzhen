module Wanxiangzhen.Tests.MockE2eTests

open Fable.Core
open Fable.Core.JsInterop
open Wanxiangzhen.Plugin
open Wanxiangzhen.Kernel.Task
open Wanxiangzhen.Kernel.Dag
open Wanxiangzhen.Kernel.SquadConfig
open Wanxiangzhen.Shell.Dyn
open Wanxiangzhen.Shell.CoordinatorRuntime
open Wanxiangzhen.Shell.CoordinatorOps
open Wanxiangzhen.Shell.CoordinatorLifecycle
open Wanxiangzhen.Shell.HttpCodec
open Wanxiangzhen.Shell.SerialQueue
open Wanxiangzhen.Shell.HttpServer
open Wanxiangzhen.Tests.Assert

[<Emit("fetch($0, $1)")>]
let private fetch (url: string) (init: obj) : JS.Promise<obj> = nativeOnly

[<Global>]
let private JSON : obj = jsNative

// ══════════════════════════════════════════════════════════════════════════════
// Flat fake state → CoordinatorDeps (20 function fields, one per deps capability)
// ══════════════════════════════════════════════════════════════════════════════

type FakeState = {
    // git ops
    mutable mergeFfOnlyCalled   : bool
    mergeBaseTrueForFirstN      : int
    mutable mergeBaseCallCount  : int
    revParseRefResult           : string
    revParseBranchResult        : string
    statusClean                 : bool
    mutable createSymlinksCount : int
    // non-git ops
    detectVibeFsResult          : bool
    isPidAliveResult            : bool
    mutable killPidCalled       : bool
    mutable killPidPid          : int option
    mutable killPidSignal       : obj option
    mutable waitForPidDeathCalls: (int * int) list
    mutable startPollingCalls   : (int * (unit -> unit)) list
    mutable stopPollingCalls    : obj list
    mutable promptSessionCalls  : (string * string) list
    mutable readAllTextsCalls   : (string * string) list
    mutable tryWorktreeAddCalls : (string * string * string * string) list
    mutable tryWorktreeRemoveForceCalls : (string * string) list
    mutable tryBranchDeleteForceCalls   : (string * string) list
    mutable showRefExistsCalls  : (string * string) list
    mutable revParseHeadCalls   : string list
    mutable revParseRefCalls    : (string * string) list
    mutable revParseBranchCalls : string list
    mutable isDetachedCalls     : string list
    mutable statusIsCleanCalls  : string list
    mutable mergeBaseIsAncestorCalls : (string * string * string) list
    mutable mergeFfOnlyCalls    : (string * string) list
    mutable spawnSlaveCalls     : (string * string * obj * string) list
    mutable nowResults          : string list
    // per-branch SHA overrides so RevParseRef returns a different value per branch,
    // preventing the stale-commit guard from short-circuiting before merge/rebase logic
    mutable revParseRefOverrides : Map<string, string>
    log                         : string list ref
}

let private mkFake () : FakeState =
    let log = ref []
    { mergeFfOnlyCalled        = false
      mergeBaseTrueForFirstN   = 1
      mergeBaseCallCount       = 0
      revParseRefResult        = "deadbeef"
      revParseBranchResult     = "main"
      statusClean              = true
      createSymlinksCount      = 0
      detectVibeFsResult       = false
      isPidAliveResult         = true
      killPidCalled            = false
      killPidPid               = None
      killPidSignal            = None
      waitForPidDeathCalls     = []
      startPollingCalls        = []
      stopPollingCalls         = []
      promptSessionCalls       = []
      readAllTextsCalls        = []
      tryWorktreeAddCalls      = []
      tryWorktreeRemoveForceCalls = []
      tryBranchDeleteForceCalls   = []
      showRefExistsCalls       = []
      revParseHeadCalls        = []
      revParseRefCalls         = []
      revParseBranchCalls      = []
      isDetachedCalls          = []
      statusIsCleanCalls       = []
      mergeBaseIsAncestorCalls = []
      mergeFfOnlyCalls         = []
      spawnSlaveCalls          = []
      nowResults               = []
      revParseRefOverrides     = Map.empty
      log                      = log }

let private mkDeps (s: FakeState) : CoordinatorDeps =
    { PromptSession        = fun c m p -> s.promptSessionCalls <- s.promptSessionCalls @ [(m, p)]; Promise.lift ()
      ReadAllTexts         = fun _ sessionId dir -> s.readAllTextsCalls <- s.readAllTextsCalls @ [(sessionId, dir)]; Promise.lift []
      TryWorktreeAdd       = fun c b p b2 -> s.tryWorktreeAddCalls <- s.tryWorktreeAddCalls @ [(c, b, p, b2)]; s.log.Value <- s.log.Value @ ["tryWorktreeAdd"; b]; Ok ""
      TryWorktreeRemoveForce = fun c p -> s.tryWorktreeRemoveForceCalls <- s.tryWorktreeRemoveForceCalls @ [(c, p)]; s.log.Value <- s.log.Value @ ["tryWorktreeRemoveForce"; p]; Ok ""
      TryBranchDeleteForce = fun c b -> s.tryBranchDeleteForceCalls <- s.tryBranchDeleteForceCalls @ [(c, b)]; s.log.Value <- s.log.Value @ ["tryBranchDeleteForce"; b]; Ok ""
      ShowRefExists        = fun c b -> s.showRefExistsCalls <- s.showRefExistsCalls @ [(c, b)]; false
      RevParseHead         = fun c -> s.revParseHeadCalls <- s.revParseHeadCalls @ [c]; s.revParseRefResult
      RevParseRef          = fun c r ->
                                s.revParseRefCalls <- s.revParseRefCalls @ [(c, r)]
                                let overrideVal =
                                    match s.revParseRefOverrides.TryGetValue r with
                                    | true, v -> v
                                    | false, _ -> s.revParseRefResult
                                overrideVal
      RevParseBranch       = fun c -> s.revParseBranchCalls <- s.revParseBranchCalls @ [c]; s.revParseBranchResult
      IsDetached           = fun c -> s.isDetachedCalls <- s.isDetachedCalls @ [c]; false
      StatusIsClean        = fun c -> s.statusIsCleanCalls <- s.statusIsCleanCalls @ [c]; s.statusClean
      MergeBaseIsAncestor  = fun c a d ->
                                s.mergeBaseIsAncestorCalls <- s.mergeBaseIsAncestorCalls @ [(c, a, d)]
                                s.mergeBaseCallCount <- s.mergeBaseCallCount + 1
                                s.mergeBaseCallCount <= s.mergeBaseTrueForFirstN
      MergeFfOnly          = fun c b ->
                                s.mergeFfOnlyCalls <- s.mergeFfOnlyCalls @ [(c, b)]
                                s.mergeFfOnlyCalled <- true
                                s.revParseRefOverrides <- s.revParseRefOverrides.Add(s.revParseBranchResult, "merged-sha")
                                s.revParseRefResult
      CreateSymlinks       = fun _ _ _ -> s.createSymlinksCount <- s.createSymlinksCount + 1
      DetectVibeFs         = fun _ -> s.detectVibeFsResult
      SpawnSlave           = fun t wt e p -> s.spawnSlaveCalls <- s.spawnSlaveCalls @ [(t, wt, e, p)]; s.log.Value <- s.log.Value @ ["spawnSlave"; t]
      IsPidAlive           = fun _ -> s.isPidAliveResult
      KillPid              = fun p signal -> s.killPidCalled <- true; s.killPidPid <- Some p; s.killPidSignal <- Some signal
      WaitForPidDeath      = fun p r -> s.waitForPidDeathCalls <- s.waitForPidDeathCalls @ [(p, r)]; Promise.lift ()
      StartPolling         = fun ms f -> s.startPollingCalls <- s.startPollingCalls @ [(ms, f)]; box "poll-handle"
      StopPolling          = fun h -> s.stopPollingCalls <- s.stopPollingCalls @ [h]
      Now                  = fun () -> System.DateTime.UtcNow.ToString("o") }

// ══════════════════════════════════════════════════════════════════════════════
// Typed CoordinatorRuntime factory (direct record literal, no createObj/setKey)
// ══════════════════════════════════════════════════════════════════════════════

let private mkRuntime (deps: CoordinatorDeps) : CoordinatorRuntime =
    { Dag          = empty "squad-session-001" ""
      Sessions     = Map.empty
      Config       = { defaults with MasterBranch = Some "main" }
      MasterBranch = "main"
      ProjectRoot  = "/tmp/project"
      MasterSessionId = ""
      Client       = createObj []
      Token        = "test-token"
      CoordinatorUrl = "http://127.0.0.1:0"
      GitQueue     = SerialQueue ()
      InjectQueue  = SerialQueue ()
      Server       = { Port = 0; Url = ""; Close = fun () -> () }
      Scheduling   = false
      PidPollHandle = None
      GitError     = None
      InjectError  = None
      Deps         = deps }

// ══════════════════════════════════════════════════════════════════════════════
// Helpers
// ══════════════════════════════════════════════════════════════════════════════

let private mkTaskEvent (taskId:string) (title:string) (desc:string) (deps:string list) : obj =
    createObj [
        "type",        box "task_created"
        "taskId",      box taskId
        "title",       box title
        "description", box desc
        "dependsOn",   box (Array.ofList deps)
    ]

let private mkSquadUpdateArgs (events: obj array) : obj =
    createObj [ "events", box events ]

let private findTask (id:string) (dag:Dag) : Task option =
    dag.Tasks |> Map.tryFind id

// ══════════════════════════════════════════════════════════════════════════════
// Test 1 — Happy Path
// ══════════════════════════════════════════════════════════════════════════════

let testHappyPath () : JS.Promise<unit> =
    promise {
        let s    = mkFake ()
        let deps = mkDeps s
        let rt   = mkRuntime deps

        // ① /squad → handleCommandExecuteBefore → squad_created frontmatter
        let input  = createObj [ "command", box "squad"; "sessionID", box "squad-session-001"; "arguments", box "add remember-me" ]
        let output = createObj [ "parts", box (System.Collections.Generic.List<obj>()) ]
        do! handleCommandExecuteBefore rt input output

        let parts = get output "parts" :?> System.Collections.Generic.List<obj>
        check (parts.Count = 1)
        check ((str parts.[0] "text").Contains "squad_event: squad_created")

        // ② handleSquadUpdate → task Pending ( Scheduling=true suppresses fire-and-forget tick )
        rt.Scheduling <- true
        let evts  = [| mkTaskEvent "squad-a1b2" "add remember-me" "add remember-me to login" [] |]
        let args  = mkSquadUpdateArgs evts
        let! reply = handleSquadUpdate rt args
        check (reply.Contains "squad-a1b2")

        match findTask "squad-a1b2" rt.Dag with
        | None -> check false
        | Some t -> check (t.Status = Pending)

        // ③ schedulerTick → task Running + worktree add + spawnSlave
        rt.Scheduling <- false
        do! schedulerTick rt

        match findTask "squad-a1b2" rt.Dag with
        | None -> check false
        | Some t ->
            check (t.Status = Running)
            check (t.WorktreePath.IsSome)
            check (t.BranchName.IsSome)

        check (List.contains "tryWorktreeAdd" s.log.Value)
        check (List.exists (fun (x: string) -> x.StartsWith "spawnSlave") s.log.Value)

        // ④ POST /register → slavePid
        let! regResp = routeHandler rt "POST" "/task/squad-a1b2/register" (createObj [ "pid", box 12345 ])
        check (regResp.StatusCode = 200)
        check ((str regResp.Body "result") = "registered")

        match findTask "squad-a1b2" rt.Dag with
        | None -> check false
        | Some t -> check (t.SlavePid = Some 12345)

        // ⑤ POST /submit → merged + cleanup
        // stale-commit guard: branch HEAD SHA must equal reported commitSha;
        // set per-branch override so RevParseRef("squad-a1b2") returns "deadbeef"
        s.revParseRefOverrides <- s.revParseRefOverrides.Add("squad-a1b2", "deadbeef")
        let! subResp = routeHandler rt "POST" "/task/squad-a1b2/submit" (createObj [ "commitSha", box "deadbeef" ])
        check (subResp.StatusCode = 200)
        check ((str subResp.Body "result") = "merged")

        match findTask "squad-a1b2" rt.Dag with
        | None -> check false
        | Some t ->
            check (t.Status = Merged)
            check (t.MergedSha.IsSome)

        check (List.contains "tryWorktreeRemoveForce" s.log.Value)
        check (List.contains "tryBranchDeleteForce" s.log.Value)

        return ()
    }

// ══════════════════════════════════════════════════════════════════════════════
// Test 2 — Competing Submits → rebase_needed
// ══════════════════════════════════════════════════════════════════════════════

let testCompetingSubmitReturnsRebaseNeeded () : JS.Promise<unit> =
    promise {
        let s    = mkFake ()   // mergeBaseTrueForFirstN = 1 → first call true, rest false
        let deps = mkDeps s
        let rt   = mkRuntime deps

        // create two independent tasks
        let evts =
            [| mkTaskEvent "squad-a1b2" "Task A" "desc A" []
               mkTaskEvent "squad-c3d4" "Task B" "desc B" [] |]
        let args = mkSquadUpdateArgs evts
        rt.Scheduling <- true     // suppress fire-and-forget tick during creation
        let! _   = handleSquadUpdate rt args

        // start both tasks (re-enable scheduling first)
        rt.Scheduling <- false
        do! schedulerTick rt

        match findTask "squad-a1b2" rt.Dag, findTask "squad-c3d4" rt.Dag with
        | Some a, Some b ->
            check (a.Status = Running)
            check (b.Status = Running)
        | _ -> check false

        // register both slaves
        let! _ = routeHandler rt "POST" "/task/squad-a1b2/register" (createObj [ "pid", box 111 ])
        let! _ = routeHandler rt "POST" "/task/squad-c3d4/register" (createObj [ "pid", box 222 ])

        // Task A submit → merged (stale-commit guard uses default revParseRefResult="deadbeef")
        let! respA = routeHandler rt "POST" "/task/squad-a1b2/submit" (createObj [ "commitSha", box "deadbeef" ])
        check (respA.StatusCode = 200)
        check ((str respA.Body "result") = "merged")

        match findTask "squad-a1b2" rt.Dag with
        | Some a -> check (a.Status = Merged)
        | None   -> check false

        check (s.mergeFfOnlyCalled = true)

        // Task B submit → rebase_needed (stale-commit guard uses default revParseRefResult="deadbeef")
        rt.Scheduling <- false   // reset before second explicit tick
        let! respB = routeHandler rt "POST" "/task/squad-c3d4/submit" (createObj [ "commitSha", box "deadbeef" ])
        check (respB.StatusCode = 200)
        check ((str respB.Body "result") = "rebase_needed")

        // B falls back to Running
        match findTask "squad-c3d4" rt.Dag with
        | Some b -> check (b.Status = Running)
        | None   -> check false

        return ()
    }

// ══════════════════════════════════════════════════════════════════════════════
// Test 3 — DAG cycle rejected
// ══════════════════════════════════════════════════════════════════════════════

let testCycleRejected () : JS.Promise<unit> =
    promise {
        let s    = mkFake ()
        let deps = mkDeps s
        let rt   = mkRuntime deps

        let evts =
            [| mkTaskEvent "squad-a1b2" "A" "a" ["squad-c3d4"]
               mkTaskEvent "squad-c3d4" "B" "b" ["squad-a1b2"] |]
        let args = mkSquadUpdateArgs evts
        rt.Scheduling <- false
        let! result = handleSquadUpdate rt args
        check (result.Contains "cycle")
        check (rt.Dag.Tasks.IsEmpty)
        return ()
    }

// ══════════════════════════════════════════════════════════════════════════════
// Test 4 — Dangling dependency rejected
// ══════════════════════════════════════════════════════════════════════════════

let testDanglingDepsRejected () : JS.Promise<unit> =
    promise {
        let s    = mkFake ()
        let deps = mkDeps s
        let rt   = mkRuntime deps

        let evts = [| mkTaskEvent "squad-a1b2" "A" "a" ["squad-zzzz"] |]
        let args = mkSquadUpdateArgs evts
        rt.Scheduling <- false
        let! result = handleSquadUpdate rt args
        check (result.Contains "squad-zzzz")
        check (rt.Dag.Tasks.IsEmpty)
        return ()
    }

// ══════════════════════════════════════════════════════════════════════════════
// Test 5 — /squad-status command
// ══════════════════════════════════════════════════════════════════════════════

let testSquadStatusCommand () : JS.Promise<unit> =
    promise {
        let s    = mkFake ()
        let deps = mkDeps s
        let rt   = mkRuntime deps
        rt.MasterSessionId <- "squad-session-001"

        let evts  = [| mkTaskEvent "squad-a1b2" "Task A" "desc" [] |]
        let args  = mkSquadUpdateArgs evts
        rt.Scheduling <- false
        let! _    = handleSquadUpdate rt args

        let input  = createObj [ "command", box "squad-status"; "sessionID", box ""; "arguments", box "" ]
        let output = createObj [ "parts", box (System.Collections.Generic.List<obj>()) ]
        do! handleCommandExecuteBefore rt input output

        let parts = get output "parts" :?> System.Collections.Generic.List<obj>
        check (parts.Count = 1)
        let statusText = str parts.[0] "text"
        check (statusText.Contains "squad-a1b2")
        return ()
    }

// ══════════════════════════════════════════════════════════════════════════════
// Test 6 — /squad-kill: Running task → Cancelled, KillPid called, no cleanup
// ══════════════════════════════════════════════════════════════════════════════

let testSquadKillCommand () : JS.Promise<unit> =
    promise {
        let s    = mkFake ()
        let deps = mkDeps s
        let rt   = mkRuntime deps
        rt.MasterSessionId <- "squad-session-001"

        // ① create task, suppress auto-tick
        let evts  = [| mkTaskEvent "squad-a1b2" "Task A" "desc A" [] |]
        let args  = mkSquadUpdateArgs evts
        rt.Scheduling <- true
        let! _    = handleSquadUpdate rt args

        // ② manual tick → Running + worktree + branch + spawn
        rt.Scheduling <- false
        do! schedulerTick rt

        match findTask "squad-a1b2" rt.Dag with
        | None -> check false
        | Some t ->
            check (t.Status = Running)
            check (t.WorktreePath.IsSome)
            check (t.BranchName.IsSome)

        // ③ register pid
        let pid = 98765
        let! _  = routeHandler rt "POST" "/task/squad-a1b2/register" (createObj [ "pid", box pid ])

        // ④ /squad-kill (no session id → kill current session)
        let input  = createObj [ "command", box "squad-kill"; "sessionID", box ""; "arguments", box "" ]
        let output = createObj [ "parts", box (System.Collections.Generic.List<obj>()) ]
        do! handleCommandExecuteBefore rt input output

        // ⑤ assertions
        check s.killPidCalled
        check (s.killPidPid = Some pid)
        check (s.tryWorktreeRemoveForceCalls = [])
        check (s.tryBranchDeleteForceCalls = [])

        match findTask "squad-a1b2" rt.Dag with
        | None -> check false
        | Some t -> check (t.Status = Cancelled)
        return ()
    }

// ══════════════════════════════════════════════════════════════════════════════
// Test 7 — HTTP transport: bad-token 401 + register updates SlavePid (real server)
// ══════════════════════════════════════════════════════════════════════════════

let private fetchJson (url: string) (init: obj) : JS.Promise<{| status: int; body: obj |}> =
    promise {
        let! resp = fetch url (box init)
        let! body = resp?json()
        return {| status = resp?status; body = body |}
    }

let testHttpTransportTokenAndRegister () : JS.Promise<unit> =
    promise {
        let s    = mkFake ()
        let deps = mkDeps s
        let rt   = mkRuntime deps
        rt.MasterSessionId <- "squad-session-001"

        // Create one task so /task/:id/register has a target (suppress auto-tick)
        let evts  = [| mkTaskEvent "squad-a1b2" "Task A" "desc A" [] |]
        let args  = mkSquadUpdateArgs evts
        rt.Scheduling <- true
        let! _    = handleSquadUpdate rt args
        rt.Scheduling <- false
        do! schedulerTick rt

        // Spin up a real HTTP server (not the in-process routeHandler shim)
        let! server = startServer rt.Token (routeHandler rt)

        try
            // ① Bad token → 401 + result=unauthorized
            let! badResp =
                fetchJson (server.Url + "/task/squad-a1b2/register") (createObj [
                    "method", box "POST"
                    "headers", box {| Authorization = box "Bearer wrong-token" |}
                    "body", box (JSON?stringify (createObj [ "pid", box 12345 ])) ])
            check (badResp.status = 401)
            check (str badResp.body "result" = "unauthorized")

            // ② Correct token → 200 + registered; SlavePid on task updated
            let! regResp =
                fetchJson (server.Url + "/task/squad-a1b2/register") (createObj [
                    "method", box "POST"
                    "headers", box {| Authorization = box ("Bearer " + rt.Token) |}
                    "body", box (JSON?stringify (createObj [ "pid", box 12345 ])) ])
            check (regResp.status = 200)
            check (str regResp.body "result" = "registered")

            match findTask "squad-a1b2" rt.Dag with
            | None -> check false
            | Some t -> check (t.SlavePid = Some 12345)
        finally
            server.Close ()
    }

// ══════════════════════════════════════════════════════════════════════════════
// Public entries
// ══════════════════════════════════════════════════════════════════════════════

let entriesAsync () : (string * (unit -> JS.Promise<unit>)) list = [
    ("MockE2e.happy_path: /squad → update → schedule → register → submit → merged",
     testHappyPath)

    ("MockE2e.competing_submit: second submit rebase_needed after first merged",
     testCompetingSubmitReturnsRebaseNeeded)

    ("MockE2e.cycle_rejected: handleSquadUpdate rejects DAG cycle",
     testCycleRejected)

    ("MockE2e.dangling_deps_rejected: unknown dep blocked",
     testDanglingDepsRejected)

    ("MockE2e.squad_status_command: /squad-status shows task list",
     testSquadStatusCommand)

    ("MockE2e.squad_kill_command: /squad-kill cancels running task, KillPid called, no cleanup",
     testSquadKillCommand)

    ("MockE2e.http_transport_token_register: bad-token 401 + correct-token register updates SlavePid",
     testHttpTransportTokenAndRegister)
]
