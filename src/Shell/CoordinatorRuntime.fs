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

[<Global>]
let nodeProcess : obj = jsNative

[<Emit("process.kill($0, $1)")>]
let killPid (pid: int) (signal: obj) : unit = jsNative

// Each SquadEvent case carries exactly its fields; callers construct cases directly.
// No record-update builder is needed — sessionId is always known at the call site.

let nowUtc () : string = System.DateTime.UtcNow.ToString("o")

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
}

let rec private tryPromptWithRetry (client: obj) (sessionId: string) (msg: string) (delay: int) (remaining: int) : JS.Promise<unit> =
    if remaining <= 0 then Promise.lift ()
    else
        promise {
            try
                do! promptSession client sessionId msg
            with _ ->
                if remaining > 1 then
                    do! Promise.sleep delay
                    do! tryPromptWithRetry client sessionId msg (delay * 2) (remaining - 1)
        }

let injectEvent (rt: CoordinatorRuntime) (e: SquadEvent) : JS.Promise<unit> =
    let msg = encodeEvent e
    rt.InjectQueue.Enqueue(fun () ->
        if rt.MasterSessionId = "" then Promise.lift ()
        else tryPromptWithRetry rt.Client rt.MasterSessionId msg 500 3)
    |> Promise.map ignore

let injectEventFire (rt: CoordinatorRuntime) (e: SquadEvent) : unit =
    injectEvent rt e |> Promise.start

let rec waitForPidDeath (pid: int) (remaining: int) : JS.Promise<unit> =
    if remaining <= 0 then Promise.lift ()
    elif not (isPidAlive pid) then Promise.lift ()
    else
        promise {
            do! Promise.sleep 200
            return! waitForPidDeath pid (remaining - 1)
        }

let cleanupTask (rt: CoordinatorRuntime) (task: Task) : unit =
    task.WorktreePath |> Option.iter (fun p ->
        match tryWorktreeRemoveForce rt.ProjectRoot p with
        | Error e when not (e.Contains "does not exist" || e.Contains "not found") ->
            rt.GitError <- Some e
        | _ -> ())
    task.BranchName |> Option.iter (fun b ->
        match tryBranchDeleteForce rt.ProjectRoot b with
        | Error e when not (e.Contains "not found" || e.Contains "does not exist") ->
            rt.GitError <- Some e
        | _ -> ())
