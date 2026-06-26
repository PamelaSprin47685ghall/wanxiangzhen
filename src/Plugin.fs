module Wanxiangzhen.Plugin

open Fable.Core
open Fable.Core.JsInterop
open Wanxiangzhen.Kernel.SquadPrompts
open Wanxiangzhen.Shell.Dyn
open Wanxiangzhen.Shell.CoordinatorRuntime
open Wanxiangzhen.Shell.CoordinatorOps
open Wanxiangzhen.Shell.SlaveRuntime
open Wanxiangzhen.Shell.PidMonitor
open Wanxiangzhen.Shell.HttpCodec
open Wanxiangzhen.Shell.SessionIo
open Wanxiangzhen.Shell.SerialQueue

[<Global("process")>]
let private nodeProcess : obj = jsNative

let private envVar (key: string) : string =
    let e = nodeProcess?("env")
    if isNullish e then "" else str e key

let private twoArgHook (f: obj -> obj -> JS.Promise<unit>) =
    box (System.Func<obj, obj, JS.Promise<unit>>(f))

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
        "execute", box (fun (a: obj) ->
            promise { return handleSquadUpdate rt a } : JS.Promise<string>)
    ]
    createObj [ "squad_update", box executeDef ]

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
                return cfg
            }))
        setKey result "command.execute.before" (twoArgHook (fun input output ->
            promise {
                let command = str input "command"
                match command with
                | "squad" ->
                    let sessionId = str input "sessionID"
                    if sessionId <> "" && rt.MasterSessionId = "" then
                        rt.MasterSessionId <- sessionId
                        do! replayFromHistory rt
                    let requirement = str input "arguments"
                    do! injectSquadCommand rt requirement sessionId
                | "squad-kill" ->
                    do! handleSquadKill rt
                | _ -> ()
            }))
        setKey result "dispose" (box (fun () ->
            rt.Server.Close ()
            rt.PidPollHandle |> Option.iter stopPolling))
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

[<ExportDefault>]
let plugin (ctx: obj) : JS.Promise<obj> =
    if envVar "SQUAD_COORDINATOR_URL" <> "" then slavePlugin ctx
    else coordinatorPlugin ctx
