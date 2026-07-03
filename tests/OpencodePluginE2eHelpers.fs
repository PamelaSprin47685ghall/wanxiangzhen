module Wanxiangzhen.Tests.OpencodePluginE2eHelpers

open Fable.Core
open Fable.Core.JsInterop
open Wanxiangzhen.Kernel.Task
open Wanxiangzhen.Kernel.SquadEvent
open Wanxiangzhen.Shell.Dyn
open Wanxiangzhen.Shell.CoordinatorRuntime
open Wanxiangzhen.Tests.Assert
open Wanxiangzhen.Tests.TestFixtures

// Process / env helpers (slave mode needs SQUAD_* env vars)
[<Global("process")>]
let private nodeProcess : obj = jsNative

let internal setEnv (key: string) (value: string) : unit =
    setKey (get nodeProcess "env") key (box value)

let internal clearEnv (key: string) : unit =
    setKey (get nodeProcess "env") key (box "")

// MockCaptures — records calls made through the mock PluginInput client
type MockCaptures = {
    mutable prompts   : obj list
    mutable commands  : obj list
    mutable messages  : obj list
}

// mkMockInput — builds an opencode-shaped PluginInput with captured client calls
let internal mkMockInput (captures: MockCaptures) : obj =
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

// ObservableDeps — mirrors CoordinatorDeps, each field records the call
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
    mutable squadEventLog             : SquadEvent list
}

let internal mkDefaultObs () : ObservableDeps =
    { spawnSlaveCalls = []
      killPidCalls = []
      worktreeAddCalls = []
      worktreeRemoveCalls = []
      branchDeleteCalls = []
      mergeBaseCalls = []
      mergeFfCalls = []
      revParseRefCalls = []
      showRefExistsCalls = []
      revParseBranchResult = "main"
      revParseRefResult = "deadbeef"
      revParseRefOverrides = Map.empty
      showRefExistsResult = false
      mergeBaseResult = true
      mergeFfResult = "merged-sha"
      isPidAliveResult = true
      nowResult = "2025-01-01T00:00:00Z"
      squadEventLog = [] }

let internal mkObservableDeps (captures: MockCaptures) (obs: ObservableDeps) : CoordinatorDeps =
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
        Now                 = fun () -> obs.nowResult
        ReadAllSquadEvents  = fun _ -> Promise.lift obs.squadEventLog
        AppendSquadEvent    = fun _ _ e ->
            obs.squadEventLog <- obs.squadEventLog @ [ e ]
            Promise.lift (Ok ()) }

// waitForScheduler — polls rt.Dag until a task transitions Pending→Running
let internal waitForScheduler (rt: CoordinatorRuntime) (taskId: string) : JS.Promise<unit> =
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
