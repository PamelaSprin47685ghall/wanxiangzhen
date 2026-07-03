module Wanxiangzhen.Plugin

open Fable.Core
open Fable.Core.JsInterop
open Wanxiangzhen.Kernel.Task
open Wanxiangzhen.Kernel.Dag
open Wanxiangzhen.Kernel.SquadEvent
open Wanxiangzhen.Kernel.SquadPrompts
open Wanxiangzhen.Kernel.SquadConfig
open Wanxiangzhen.Shell.Dyn
open Wanxiangzhen.Shell.CoordinatorRuntime
open Wanxiangzhen.Shell.CoordinatorOps
open Wanxiangzhen.Shell.CoordinatorLifecycle
open Wanxiangzhen.Shell.CoordinatorReplay
open Wanxiangzhen.Shell.SlaveRuntime
open Wanxiangzhen.Shell.PidMonitor
open Wanxiangzhen.Shell.HttpCodec
open Wanxiangzhen.Shell.SessionIo
open Wanxiangzhen.Shell.SerialQueue
open Wanxiangzhen.Shell.EventCodec
open Wanxiangzhen.Shell.SquadEventLogRuntime
open Wanxiangzhen.Shell.HttpServer
open Wanxiangzhen.Shell.ConfigReader
open Wanxiangzhen.Shell.GitShell
open Wanxiangzhen.Shell.SlaveSpawn
open Wanxiangzhen.Shell.SymlinkShell

[<Global("process")>]
let private nodeProcess : obj = jsNative

[<Global>]
let private JSON : obj = jsNative

[<Import("writeFileSync", "node:fs")>]
let private writeFileSync (path: string) (data: string) : unit = jsNative

[<Import("join", "node:path")>]
let private pathJoin (path: string) (seg: string) : string = jsNative

let private envVar (key: string) : string =
    let e = nodeProcess?("env")
    if isNullish e then "" else str e key

let private writeE2eMetaIfEnabled (rt: CoordinatorRuntime) : unit =
    if envVar "WANXIANGZHEN_E2E" = "1" || envVar "WANXIANGZHEN_E2E_INPROCESS" = "1" then
        let meta = {| coordinatorUrl = rt.CoordinatorUrl; token = rt.Token; masterSessionId = rt.MasterSessionId; sessionId = rt.Dag.SessionId |}
        let fullPath = pathJoin rt.ProjectRoot ".wanxiangzhen-e2e-meta.json"
        writeFileSync fullPath (string (JSON?stringify(meta)))

let private twoArgHook (f: obj -> obj -> JS.Promise<unit>) =
    box (System.Func<obj, obj, JS.Promise<unit>>(f))

let internal mutateOutputParts (output: obj) (part: obj) : unit =
    let existing = get output "parts"
    if isNullish existing then
        setKey output "parts" (box [| part |])
    else
        let list = existing :?> System.Collections.Generic.List<obj>
        list.Clear()
        list.Add(part)
        setKey output "parts" (box list)

let internal handleCommandExecuteBefore (rt: CoordinatorRuntime) (input: obj) (output: obj) : JS.Promise<unit> =
    promise {
        let command = str input "command"
        match command with
        | "squad" ->
            let sessionId = str input "sessionID"
            if sessionId <> "" && rt.MasterSessionId = "" then
                rt.MasterSessionId <- sessionId
                do! replayFromEventLog rt
            let requirement = str input "arguments"
            if not rt.Dag.Tasks.IsEmpty && rt.Dag.SessionId <> "" then
                rt.Sessions <- rt.Sessions.Add(rt.Dag.SessionId, rt.Dag)
            let newSid = "squad-session-" + (rt.Deps.Now ()).Substring(0, 19).Replace("T", "-").Replace(":", "-")
            let evt = SquadCreated (newSid, requirement)
            let! cr = commitEvent rt evt
            match cr with
            | Error err -> rt.InjectError <- Some (sprintf "SquadCreated append failed: %s" err)
            | Ok () ->
                rt.Dag <- empty newSid requirement
                writeE2eMetaIfEnabled rt
            let part = box {| ``type`` = "text"; text = encodeEvent evt |}
            mutateOutputParts output part
        | "squad-kill" ->
            let args = str input "arguments"
            let sidOpt = if args = "" then None else Some args
            do! handleSquadKill rt sidOpt
        | "squad-status" ->
            let dagText = formatDagText rt
            let part = box {| ``type`` = "text"; text = dagText |}
            mutateOutputParts output part
        | _ -> ()
    }

let internal assembleCoordinatorHooks (rt: CoordinatorRuntime) : obj =
    let squadUpdateToolDef () : obj =
        let args = createObj [
            "events", box (createObj [
                "type", box "array"
                "minItems", box 1
                "items", box (createObj [
                    "type", box "object"
                    "properties", box (createObj [
                        "type", box (createObj [ "type", box "string"; "enum", box [| "tasks_created"; "squad_cancelled" |] ])
                        "tasks", box (createObj [
                            "type", box "array"
                            "items", box (createObj [
                                "type", box "object"
                                "properties", box (createObj [
                                    "taskId", box (createObj [ "type", box "string" ])
                                    "title", box (createObj [ "type", box "string" ])
                                    "description", box (createObj [ "type", box "string" ])
                                    "dependsOn", box (createObj [ "type", box "array"; "items", box (createObj [ "type", box "string" ]) ])
                                ])
                                "required", box [| "title"; "description" |]
                            ])
                        ])
                    ])
                    "required", box [| "type" |]
                ])
            ])
        ]
        let executeDef = createObj [
            "description", box "Submit task decomposition or status update for the current squad session."
            "args", box args
            "execute", box (System.Func<obj, obj, JS.Promise<string>>(fun a _ -> handleSquadUpdate rt a))
        ]
        createObj [ "squad_update", box executeDef ]

    let result = createObj []
    setKey result "id" (box "wanxiangzhen")
    setKey result "name" (box "wanxiangzhen")
    setKey result "tool" (squadUpdateToolDef ())
    setKey result "config" (box (fun (cfg: obj) ->
        promise {
            let commands = get cfg "command"
            if not (isNullish commands) then
                setKey commands "squad" (box {| template = "/squad <requirement>"; description = "Decompose requirement into parallel task DAG" |})
                setKey commands "squad-kill" (box {| template = "/squad-kill [session_id]"; description = "Kill squad slave processes" |})
                setKey commands "squad-status" (box {| template = "/squad-status"; description = "Show current squad DAG status" |})
            return cfg
        }))
    setKey result "command.execute.before" (twoArgHook (handleCommandExecuteBefore rt))
    setKey result "chat.message" (twoArgHook (fun input _output ->
        promise {
            if rt.MasterSessionId = "" then
                let sid = str input "sessionID"
                if sid <> "" then
                    rt.MasterSessionId <- sid
                    do! replayFromEventLog rt
        }))
    setKey result "dispose" (box (fun () ->
        promise {
            rt.Server.Close ()
            rt.PidPollHandle |> Option.iter rt.Deps.StopPolling
        }))
    result

let pluginWithDeps (ctx: obj) (deps: CoordinatorDeps) : JS.Promise<{| hooks: obj; runtime: CoordinatorRuntime |}> =
    promise {
        let client = get ctx "client"
        let directory = str ctx "directory"
        let config = readConfig directory
        let mb, gitError =
            match config.MasterBranch with
            | Some b -> b, None
            | None ->
                try
                    if not (deps.HasCommits directory) then
                        "master", Some "Repository has no commits. Run 'git commit --allow-empty -m \"Initial commit\"' before using /squad."
                    elif deps.IsDetached directory then
                        "master", Some "Detached HEAD detected. Please configure squad.masterBranch in AGENTS.md frontmatter."
                    else
                        deps.RevParseBranch directory, None
                with ex ->
                    "master", Some (string ex.Message)
        let! rt = createWithDeps client directory config mb gitError deps
        let hooks = assembleCoordinatorHooks rt
        return {| hooks = hooks; runtime = rt |}
    }

let private realCoordinatorDeps () : CoordinatorDeps =
    let depsRef = ref {
        PromptSession        = fun _ _ _ -> Promise.lift ()
        ReadAllSquadEvents   = readAllSquadEvents
        AppendSquadEvent     = appendSquadEvent
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
        HasCommits           = fun _ -> false
        CreateSymlinks       = fun _ _ _ -> ()
        SpawnSlave           = fun _ _ _ _ -> ()
        IsPidAlive           = fun _ -> false
        KillPid              = fun _ _ -> ()
        WaitForPidDeath      = fun _ _ -> Promise.lift ()
        StartPolling         = fun _ _ -> box null
        StopPolling          = fun _ -> ()
        Now                  = fun () -> System.DateTime.UtcNow.ToString("o") }
    let deps = {
        PromptSession        = promptSession
        ReadAllSquadEvents   = readAllSquadEvents
        AppendSquadEvent     = appendSquadEvent
        TryWorktreeAdd       = tryWorktreeAdd
        TryWorktreeRemoveForce = tryWorktreeRemoveForce
        TryBranchDeleteForce = tryBranchDeleteForce
        ShowRefExists        = showRefExists
        RevParseHead         = revParseHead
        RevParseRef          = revParseRef
        RevParseBranch       = revParseBranch
        IsDetached           = isDetached
        StatusIsClean        = statusIsClean
        MergeBaseIsAncestor  = mergeBaseIsAncestor
        MergeFfOnly          = mergeFfOnly
        HasCommits           = hasCommits
        CreateSymlinks        = createSymlinks
        SpawnSlave            = spawnSlave
        IsPidAlive           = isPidAlive
        KillPid              = killPid
        WaitForPidDeath      = fun pid r -> waitForPidDeath depsRef.Value pid r
        StartPolling         = startPolling
        StopPolling          = stopPolling
        Now                  = fun () -> System.DateTime.UtcNow.ToString("o") }
    depsRef.Value <- deps
    deps

let private coordinatorPlugin (ctx: obj) : JS.Promise<obj> =
    promise {
        let! result = pluginWithDeps ctx (realCoordinatorDeps ())
        return result.hooks
    }

let private slavePlugin (_: obj) : JS.Promise<obj> =
    promise {
        match readSlaveConfig () with
        | None -> return createObj []
        | Some cfg ->
            do! registerPid cfg
            let result = createObj []
            setKey result "id" (box "wanxiangzhen-slave")
            setKey result "name" (box "wanxiangzhen-slave")
            setKey result "tool" (slaveToolDefs cfg)
            setKey result "dispose" (box (fun () -> doneBeacon cfg |> Promise.start |> ignore; Promise.lift ()))
            return result
    }

let plugin (ctx: obj) : JS.Promise<obj> =
    if envVar "SQUAD_COORDINATOR_URL" <> "" then slavePlugin ctx
    else coordinatorPlugin ctx

[<ExportDefault>]
let pluginModule : obj =
    createObj [
        "id", box "wanxiangzhen"
        "server", box plugin
    ]
