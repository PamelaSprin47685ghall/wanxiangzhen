module Wanxiangzhen.E2eTests.HarnessHelpers

open Fable.Core
open Fable.Core.JsInterop
open Wanxiangzhen.Kernel.Task
open Wanxiangzhen.Kernel.Dag
open Wanxiangzhen.Shell.CoordinatorRuntime
open Wanxiangzhen.Shell.Dyn

[<Global>]
let console : obj = jsNative

[<Emit("new Promise(r => setTimeout(r, $0))")>]
let sleep (ms: int) : Fable.Core.JS.Promise<unit> = jsNative

type Harness =
    abstract mode: string
    abstract hooks: obj
    abstract runtime: obj
    abstract tmpDir: string
    abstract token: string
    abstract url: string
    abstract runCommand: string -> string -> string -> Fable.Core.JS.Promise<obj>
    abstract toolRound: string -> obj -> Fable.Core.JS.Promise<string>
    abstract coordinatorGet: string -> string -> Fable.Core.JS.Promise<obj>
    abstract coordinatorPost: string -> obj -> string -> Fable.Core.JS.Promise<obj>
    abstract readMeta: unit -> string
    abstract waitForMeta: unit -> Fable.Core.JS.Promise<string>
    abstract waitForScheduler: string -> unit -> Fable.Core.JS.Promise<unit>
    abstract ensureSchedulerCapacity: unit -> Fable.Core.JS.Promise<unit>
    abstract getLog: unit -> obj
    abstract getSquadEvents: unit -> obj
    abstract getPromptCalls: unit -> obj
    abstract getSpawnCalls: unit -> obj
    abstract getKillCalls: unit -> obj
    abstract getWorktreeAddCalls: unit -> obj
    abstract getWorktreeRemoveCalls: unit -> obj
    abstract getBranchDeleteCalls: unit -> obj
    abstract clearCallSpies: unit -> unit
    abstract setRevParseRef: string -> string -> unit
    abstract setMergeBaseResult: bool -> unit
    abstract setMergeFfResult: string -> unit
    abstract setStatusClean: bool -> unit
    abstract setHasCommits: bool -> unit
    abstract setShowRefExists: bool -> unit
    abstract setIsPidAlive: bool -> unit
    abstract setNowResult: string -> unit
    abstract callSlavePlugin: obj -> string -> string -> string -> string -> string -> Fable.Core.JS.Promise<obj>
    abstract dispose: unit -> Fable.Core.JS.Promise<unit>

let harnessFromObj (o: obj) : Harness = unbox o
let emptyObj = createObj []
let partsToList (parts: obj) : obj list =
    if isNullish parts then []
    else Seq.toList (parts :?> System.Collections.Generic.IEnumerable<obj>)

let spinUntil (predicate: unit -> Fable.Core.JS.Promise<bool>) (timeoutMs: int) : Fable.Core.JS.Promise<bool> =
    let rec loop steps =
        promise {
            let! ok = predicate ()
            if ok then return true
            elif steps * 10 >= timeoutMs then return false
            else
                do! sleep 10
                return! loop (steps + 1)
        }
    loop 0

let mkTask (taskId: string) (title: string) (desc: string) (deps: string array) : obj =
    createObj [
        "taskId", box taskId
        "title", box title
        "description", box desc
        "dependsOn", box deps
    ]

let mkTasksCreated (tasks: obj array) : obj =
    createObj [
        "type", box "tasks_created"
        "tasks", box tasks
    ]

let mkUpdateArgs (events: obj array) : obj =
    createObj [ "events", box events ]

let getBranchName (rt: CoordinatorRuntime) (taskId: string) : string =
    match findTask taskId rt.Dag with
    | Some t -> t.BranchName |> Option.defaultValue taskId
    | None -> taskId
