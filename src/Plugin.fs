module Wanxiangzhen.Plugin

open Fable.Core
open Fable.Core.JsInterop
open Wanxiangzhen.Kernel.Task
open Wanxiangzhen.Kernel.Dag
open Wanxiangzhen.Kernel.SquadEvent
open Wanxiangzhen.Kernel.SquadPrompts
open Wanxiangzhen.Shell.Dyn
open Wanxiangzhen.Shell.CoordinatorRuntime
open Wanxiangzhen.Shell.CoordinatorOps
open Wanxiangzhen.Shell.CoordinatorLifecycle
open Wanxiangzhen.Shell.SlaveRuntime
open Wanxiangzhen.Shell.PidMonitor
open Wanxiangzhen.Shell.HttpCodec
open Wanxiangzhen.Shell.SessionIo
open Wanxiangzhen.Shell.SerialQueue
open Wanxiangzhen.Shell.EventCodec

[<Global("process")>]
let private nodeProcess : obj = jsNative

let private envVar (key: string) : string =
    let e = nodeProcess?("env")
    if isNullish e then "" else str e key

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

let private squadUpdateToolDef (rt: CoordinatorRuntime) : obj =
    let args = createObj [
        "events", box (createObj [
            "type", box "array"
            "minItems", box 1
            "items", box (createObj [
                "type", box "object"
                "properties", box (createObj [
                    "type", box (createObj [ "type", box "string"; "enum", box [| "task_created"; "squad_cancelled" |] ])
                    "taskId", box (createObj [ "type", box "string" ])
                    "title", box (createObj [ "type", box "string" ])
                    "description", box (createObj [ "type", box "string" ])
                    "dependsOn", box (createObj [ "type", box "array"; "items", box (createObj [ "type", box "string" ]) ])
                ])
                "required", box [| "type" |]
            ])
        ])
    ]
    let executeDef = createObj [
        "description", box "Submit task decomposition or status update for the current squad session."
        "args", box args
        "execute", box (fun (a: obj) -> handleSquadUpdate rt a)
    ]
    createObj [ "squad_update", box executeDef ]

let internal handleCommandExecuteBefore (rt: CoordinatorRuntime) (input: obj) (output: obj) : JS.Promise<unit> =
    promise {
        let command = str input "command"
        match command with
        | "squad" ->
            let sessionId = str input "sessionID"
            if sessionId <> "" && rt.MasterSessionId = "" then
                rt.MasterSessionId <- sessionId
                do! replayFromHistory rt
            let requirement = str input "arguments"
            if not rt.Dag.Tasks.IsEmpty && rt.Dag.SessionId <> "" then
                rt.Sessions <- rt.Sessions.Add(rt.Dag.SessionId, rt.Dag)
            let newSid = "squad-session-" + (rt.Deps.Now ()).Substring(0, 19).Replace("T", "-").Replace(":", "-")
            rt.Dag <- empty newSid requirement
            let evt = SquadCreated (newSid, requirement)
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

let private coordinatorPlugin (ctx: obj) : JS.Promise<obj> =
    promise {
        let client = get ctx "client"
        let directory = str ctx "directory"
        let! rt = create client directory
        let result = createObj []
        setKey result "id" (box "wanxiangzhen")
        setKey result "name" (box "wanxiangzhen")
        setKey result "tool" (squadUpdateToolDef rt)
        setKey result "config" (box (fun (cfg: obj) ->
            promise {
                let commands = get cfg "command"
                if not (isNullish commands) then
                    setKey commands "squad" (box {| template = "/squad <requirement>"; description = "Decompose requirement into parallel task DAG" |})
                    setKey commands "squad-kill" (box {| template = "/squad-kill [session_id]"; description = "Kill squad slave processes" |})
                    setKey commands "squad-status" (box {| template = "/squad-status"; description = "Show current squad DAG status" |})
                return cfg
            }))
        setKey result "command.execute.before" (twoArgHook (fun input output ->
            handleCommandExecuteBefore rt input output))
        setKey result "dispose" (box (fun () ->
            rt.Server.Close ()
            rt.PidPollHandle |> Option.iter rt.Deps.StopPolling))
        return result
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
            setKey result "dispose" (box (fun () -> doneBeacon cfg |> Promise.start |> ignore))
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
