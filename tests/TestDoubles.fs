module Wanxiangzhen.Tests.TestDoubles

open Fable.Core
open Fable.Core.JsInterop
open Wanxiangzhen.Kernel.Task
open Wanxiangzhen.Kernel.Dag
open Wanxiangzhen.Kernel.SquadConfig
open Wanxiangzhen.Shell.CoordinatorRuntime
open Wanxiangzhen.Shell.SerialQueue

type FakeState = {
    mutable mergeFfOnlyCalled      : bool
    mutable mergeBaseTrueForFirstN : int
    mutable mergeBaseCallCount     : int
    revParseRefResult              : string
    revParseBranchResult           : string
    statusClean                    : bool
    mutable createSymlinksCount    : int
    detectVibeFsResult             : bool
    mutable isPidAliveResult       : bool
    mutable killPidCalled          : bool
    mutable killPidPid             : int option
    mutable killPidSignal          : obj option
    mutable waitForPidDeathCalls   : (int * int) list
    mutable startPollingCalls      : (int * (unit -> unit)) list
    mutable stopPollingCalls       : obj list
    mutable promptSessionCalls     : (string * string) list
    mutable readAllTextsCalls      : (string * string) list
    mutable tryWorktreeAddCalls    : (string * string * string * string) list
    mutable tryWorktreeRemoveForceCalls : (string * string) list
    mutable tryBranchDeleteForceCalls   : (string * string) list
    mutable showRefExistsCalls     : (string * string) list
    mutable revParseHeadCalls      : string list
    mutable revParseRefCalls       : (string * string) list
    mutable revParseBranchCalls    : string list
    mutable isDetachedCalls        : string list
    mutable statusIsCleanCalls     : string list
    mutable mergeBaseIsAncestorCalls : (string * string * string) list
    mutable mergeFfOnlyCalls       : (string * string) list
    mutable spawnSlaveCalls        : (string * string * obj * string) list
    mutable revParseRefOverrides   : Map<string, string>
    log                            : string list ref
    mutable orphanWarningSent      : bool
    mutable mergeBaseOverride      : (string -> string -> string -> bool) option
    mutable revParseRefOverride    : (string -> string -> string) option
    mutable revParseBranchOverride : (string -> string) option
    mutable statusIsCleanOverride  : (string -> bool) option
    mutable tryWorktreeAddOverride : (string -> string -> string -> string -> Result<string,string>) option
    mutable promptSessionOverride  : (obj -> string -> string -> JS.Promise<unit>) option
    mutable readAllTextsOverride   : (obj -> string -> string -> JS.Promise<string list>) option
    mutable startPollingOverride   : (int -> (unit -> unit) -> obj) option
    mutable stopPollingOverride    : (obj -> unit) option
    mutable killPidOverride        : (int -> obj -> unit) option
    mutable detectVibeFsOverride   : (string -> bool) option
    }

let mkFake () : FakeState =
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
      revParseRefOverrides     = Map.empty
      log                      = log
      orphanWarningSent        = false
      mergeBaseOverride        = None
      revParseRefOverride      = None
      revParseBranchOverride   = None
      statusIsCleanOverride    = None
      tryWorktreeAddOverride   = None
      promptSessionOverride    = None
      readAllTextsOverride     = None
      startPollingOverride     = None
      stopPollingOverride      = None
      killPidOverride          = None
      detectVibeFsOverride     = None }

let mkDeps (s: FakeState) : CoordinatorDeps =
    { PromptSession        = fun c m p ->
            match s.promptSessionOverride with
            | Some f -> f c m p
            | None -> s.promptSessionCalls <- s.promptSessionCalls @ [(m, p)]; Promise.lift ()
      ReadAllTexts         = fun c sid dir ->
            match s.readAllTextsOverride with
            | Some f -> f c sid dir
            | None -> s.readAllTextsCalls <- s.readAllTextsCalls @ [(sid, dir)]; Promise.lift []
      TryWorktreeAdd       = fun c b p b2 ->
            match s.tryWorktreeAddOverride with
            | Some f -> f c b p b2
            | None -> s.tryWorktreeAddCalls <- s.tryWorktreeAddCalls @ [(c, b, p, b2)]; s.log.Value <- s.log.Value @ ["tryWorktreeAdd"; b]; Ok ""
      TryWorktreeRemoveForce = fun c p ->
            s.tryWorktreeRemoveForceCalls <- s.tryWorktreeRemoveForceCalls @ [(c, p)]
            s.log.Value <- s.log.Value @ ["tryWorktreeRemoveForce"; p]; Ok ""
      TryBranchDeleteForce  = fun c b ->
            s.tryBranchDeleteForceCalls <- s.tryBranchDeleteForceCalls @ [(c, b)]
            s.log.Value <- s.log.Value @ ["tryBranchDeleteForce"; b]; Ok ""
      ShowRefExists        = fun c b -> s.showRefExistsCalls <- s.showRefExistsCalls @ [(c, b)]; false
      RevParseHead         = fun c -> s.revParseHeadCalls <- s.revParseHeadCalls @ [c]; s.revParseRefResult
      RevParseRef          = fun c r ->
            s.revParseRefCalls <- s.revParseRefCalls @ [(c, r)]
            match s.revParseRefOverrides.TryGetValue r with
            | true, v -> v
            | false, _ ->
                match s.revParseRefOverride with
                | Some f -> f c r
                | None -> s.revParseRefResult
      RevParseBranch       = fun c ->
            s.revParseBranchCalls <- s.revParseBranchCalls @ [c]
            match s.revParseBranchOverride with
            | Some f -> f c
            | None -> s.revParseBranchResult
      IsDetached           = fun c -> s.isDetachedCalls <- s.isDetachedCalls @ [c]; false
      StatusIsClean        = fun c ->
            match s.statusIsCleanOverride with
            | Some f -> f c
            | None -> s.statusIsCleanCalls <- s.statusIsCleanCalls @ [c]; s.statusClean
      MergeBaseIsAncestor  = fun c a d ->
            s.mergeBaseIsAncestorCalls <- s.mergeBaseIsAncestorCalls @ [(c, a, d)]
            s.mergeBaseCallCount <- s.mergeBaseCallCount + 1
            match s.mergeBaseOverride with
            | Some f -> f c a d
            | None -> s.mergeBaseCallCount <= s.mergeBaseTrueForFirstN
      MergeFfOnly          = fun c b ->
            s.mergeFfOnlyCalls <- s.mergeFfOnlyCalls @ [(c, b)]
            s.mergeFfOnlyCalled <- true
            s.revParseRefOverrides <- s.revParseRefOverrides.Add(s.revParseBranchResult, "merged-sha")
            s.revParseRefResult
      CreateSymlinks       = fun _ _ _ -> s.createSymlinksCount <- s.createSymlinksCount + 1
      DetectVibeFs         = fun c ->
            match s.detectVibeFsOverride with
            | Some f -> f c
            | None -> s.detectVibeFsResult
      SpawnSlave           = fun t wt e p -> s.spawnSlaveCalls <- s.spawnSlaveCalls @ [(t, wt, e, p)]; s.log.Value <- s.log.Value @ ["spawnSlave"; t]
      IsPidAlive           = fun _ -> s.isPidAliveResult
      KillPid              = fun p signal ->
            match s.killPidOverride with
            | Some f -> f p signal
            | None -> s.killPidCalled <- true; s.killPidPid <- Some p; s.killPidSignal <- Some signal
      WaitForPidDeath      = fun p r -> s.waitForPidDeathCalls <- s.waitForPidDeathCalls @ [(p, r)]; Promise.lift ()
      StartPolling         = fun ms callback ->
            match s.startPollingOverride with
            | Some g -> g ms callback
            | None -> s.startPollingCalls <- s.startPollingCalls @ [(ms, callback)]; box "poll-handle"
      StopPolling          = fun h ->
            match s.stopPollingOverride with
            | Some f -> f h
            | None -> s.stopPollingCalls <- s.stopPollingCalls @ [h]
      Now                  = fun () -> System.DateTime.UtcNow.ToString("o") }

let mkRuntime (deps: CoordinatorDeps) : CoordinatorRuntime =
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

let mkTaskEvent (taskId:string) (title:string) (desc:string) (deps:string list) : obj =
    createObj [
        "type",        box "task_created"
        "taskId",      box taskId
        "title",       box title
        "description", box desc
        "dependsOn",   box (Array.ofList deps)
    ]

let mkSquadUpdateArgs (events: obj array) : obj =
    createObj [ "events", box events ]

let findTask (id:string) (dag:Dag) : Task option =
    dag.Tasks |> Map.tryFind id

[<Emit("fetch($0, $1)")>]
let fetch (url: string) (init: obj) : JS.Promise<obj> = jsNative

[<Global>]
let JSON : obj = jsNative

let fetchJson (url: string) (init: obj) : JS.Promise<{| status: int; body: obj |}> =
    promise {
        let! resp = fetch url (box init)
        let! body = resp?json()
        return {| status = resp?status; body = body |}
    }
