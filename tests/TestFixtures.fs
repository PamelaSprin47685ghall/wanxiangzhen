module Wanxiangzhen.Tests.TestFixtures

open Fable.Core
open Fable.Core.JsInterop
open Wanxiangzhen.Kernel.Dag
open Wanxiangzhen.Kernel.SquadConfig
open Wanxiangzhen.Shell.SerialQueue
open Wanxiangzhen.Shell.CoordinatorRuntime

// ══════════════════════════════════════════════════════════════════════════════
// Shared no-op CoordinatorDeps (20 fields, all stubs)
// StartPolling returns null handle; override if test needs non-null.
// ══════════════════════════════════════════════════════════════════════════════

let stubDeps () : CoordinatorDeps =
    { PromptSession        = fun _ _ _ -> Promise.lift ()
      ReadAllTexts         = fun _ _ _ -> Promise.lift []
      TryWorktreeAdd       = fun _ _ _ _ -> Ok ""
      TryWorktreeRemoveForce = fun _ _ -> Ok ""
      TryBranchDeleteForce = fun _ _ -> Ok ""
      ShowRefExists        = fun _ _ -> false
      RevParseHead         = fun _ -> ""
      RevParseRef          = fun _ _ -> ""
      RevParseBranch       = fun _ -> ""
      IsDetached           = fun _ -> false
      StatusIsClean        = fun _ -> true
      MergeBaseIsAncestor  = fun _ _ _ -> false
      MergeFfOnly          = fun _ _ -> ""
      CreateSymlinks       = fun _ _ _ -> ()
      DetectVibeFs         = fun _ -> false
      SpawnSlave           = fun _ _ _ _ -> ()
      IsPidAlive           = fun _ -> false
      KillPid              = fun _ _ -> ()
      WaitForPidDeath      = fun _ _ -> Promise.lift ()
      StartPolling         = fun _ _ -> box null
      StopPolling          = fun _ -> ()
      Now                  = fun () -> System.DateTime.UtcNow.ToString("o") }

// ══════════════════════════════════════════════════════════════════════════════
// mkRuntimeWithDeps — full factory accepting caller-supplied CoordinatorDeps.
// GitQueue and InjectQueue are always two independent SerialQueue instances.
// mkRuntime () is the zero-arg convenience form using stubDeps ().
// ══════════════════════════════════════════════════════════════════════════════

let mkRuntimeWithDeps (deps: CoordinatorDeps) : CoordinatorRuntime =
    { Dag          = empty "" ""
      Sessions     = Map.empty
      Config       = defaults
      MasterBranch = "main"
      ProjectRoot  = "/tmp"
      MasterSessionId = ""
      Client       = createObj []
      Token        = "test-token"
      CoordinatorUrl = "http://localhost:0"
      GitQueue     = SerialQueue ()
      InjectQueue  = SerialQueue ()
      Server       = { Port = 0; Url = ""; Close = fun () -> () }
      Scheduling   = false
      PidPollHandle = None
      GitError     = None
      InjectError  = None
      Deps         = deps }

let mkRuntime () : CoordinatorRuntime =
    mkRuntimeWithDeps (stubDeps ())
