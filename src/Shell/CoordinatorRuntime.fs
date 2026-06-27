module Wanxiangzhen.Shell.CoordinatorRuntime

open Fable.Core
open Fable.Core.JsInterop
open Wanxiangzhen.Kernel.Task
open Wanxiangzhen.Kernel.Dag
open Wanxiangzhen.Kernel.SquadEvent
open Wanxiangzhen.Kernel.SquadConfig
open Wanxiangzhen.Kernel.FfDecision
open Wanxiangzhen.Shell.Dyn
open Wanxiangzhen.Shell.SerialQueue
open Wanxiangzhen.Shell.GitShell
open Wanxiangzhen.Shell.HttpServer
open Wanxiangzhen.Shell.EventCodec
open Wanxiangzhen.Shell.SessionIo
open Wanxiangzhen.Shell.PidMonitor

[<Emit("process.kill($0, $1)")>]
let killPid (pid: int) (signal: obj) : unit = jsNative

// Each SquadEvent case carries exactly its fields; callers construct cases directly.
// No record-update builder is needed — sessionId is always known at the call site.

type CoordinatorDeps = {
    PromptSession       : obj -> string -> string -> JS.Promise<unit>
    ReadAllTexts        : obj -> string -> string -> JS.Promise<string list>
    TryWorktreeAdd      : string -> string -> string -> string -> Result<string, string>
    TryWorktreeRemoveForce : string -> string -> Result<string, string>
    TryBranchDeleteForce  : string -> string -> Result<string, string>
    ShowRefExists       : string -> string -> bool
    RevParseHead        : string -> string
    RevParseRef         : string -> string -> string
    RevParseBranch      : string -> string
    IsDetached          : string -> bool
    StatusIsClean       : string -> bool
    MergeBaseIsAncestor : string -> string -> string -> bool
    MergeFfOnly         : string -> string -> string
    CreateSymlinks      : string -> string -> string list -> unit
    DetectVibeFs        : string -> bool
    SpawnSlave          : string -> string -> obj -> string -> unit
    IsPidAlive          : int -> bool
    KillPid             : int -> obj -> unit
    WaitForPidDeath     : int -> int -> JS.Promise<unit>
    StartPolling        : int -> (unit -> unit) -> obj
    StopPolling         : obj -> unit
    Now                 : unit -> string
}

let generateTaskId () : string =
    let hex = "0123456789abcdef"
    let chars = [| for _ in 0..3 -> hex[int (JS.Math.random () * 16.0)] |]
    "squad-" + System.String(chars)

type CoordinatorRuntime = {
    mutable Dag: Dag
    mutable Sessions: Map<string, Dag>
    mutable Config: SquadConfig
    MasterBranch: string
    ProjectRoot: string
    mutable MasterSessionId: string
    Client: obj
    Token: string
    CoordinatorUrl: string
    GitQueue: SerialQueue
    InjectQueue: SerialQueue
    Server: StartedServer
    mutable Scheduling: bool
    mutable PidPollHandle: obj option
    mutable GitError: string option
    mutable InjectError: string option
    Deps: CoordinatorDeps
}

let rec private tryPromptWithRetry (rt: CoordinatorRuntime) (sessionId: string) (msg: string) (delay: int) (remaining: int) : JS.Promise<unit> =
    if remaining <= 0 then Promise.lift ()
    else
        promise {
            try
                do! rt.Deps.PromptSession rt.Client sessionId msg
            with ex ->
                if remaining > 1 then
                    do! Promise.sleep delay
                    do! tryPromptWithRetry rt sessionId msg (delay * 2) (remaining - 1)
                else
                    rt.InjectError <- Some (sprintf "Prompt injection failed for session %s: %s" sessionId (string ex))
                    return ()
        }

let injectEvent (rt: CoordinatorRuntime) (e: SquadEvent) : JS.Promise<unit> =
    let msg = encodeEvent e
    rt.InjectQueue.Enqueue(fun () ->
        if rt.MasterSessionId = "" then Promise.lift ()
        else tryPromptWithRetry rt rt.MasterSessionId msg 500 3)
    |> Promise.map ignore

let injectEventFire (rt: CoordinatorRuntime) (e: SquadEvent) : unit =
    injectEvent rt e |> Promise.start

let rec waitForPidDeath (deps: CoordinatorDeps) (pid: int) (remaining: int) : JS.Promise<unit> =
    if remaining <= 0 then Promise.lift ()
    elif not (deps.IsPidAlive pid) then Promise.lift ()
    else
        promise {
            do! Promise.sleep 200
            return! waitForPidDeath deps pid (remaining - 1)
        }

let cleanupTask (rt: CoordinatorRuntime) (task: Task) : unit =
    task.WorktreePath |> Option.iter (fun p ->
        match rt.Deps.TryWorktreeRemoveForce rt.ProjectRoot p with
        | Error e when not (e.Contains "does not exist" || e.Contains "not found") ->
            rt.GitError <- Some e
        | _ -> ())
    task.BranchName |> Option.iter (fun b ->
        match rt.Deps.TryBranchDeleteForce rt.ProjectRoot b with
        | Error e when not (e.Contains "not found" || e.Contains "does not exist") ->
            rt.GitError <- Some e
        | _ -> ())
