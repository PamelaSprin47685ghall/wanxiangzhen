module Wanxiangzhen.Shell.StateBackup

open Fable.Core
open Fable.Core.JsInterop
open Wanxiangzhen.Kernel.Task
open Wanxiangzhen.Kernel.Dag
open Wanxiangzhen.Shell.Dyn
open Wanxiangzhen.Shell.ConfigReader
open Wanxiangzhen.Shell.CoordinatorRuntime

[<Global>]
let private console : obj = jsNative

[<Import("existsSync", "node:fs")>]
let existsSync (p: string) : bool = jsNative

[<Import("writeFileSync", "node:fs")>]
let writeFileSync (path: string) (data: string) : unit = jsNative

[<Import("mkdirSync", "node:fs")>]
let mkdirSync (path: string) : unit = jsNative

[<Import("readFileSync", "node:fs")>]
let readFileSync (path: string) : string = jsNative

[<Import("watch", "node:fs")>]
let fsWatch (path: string) (listener: obj -> unit) : obj = jsNative

let detectVibeFs (directory: string) : bool =
    try existsSync (directory + "/node_modules/wanxiangshu")
    with _ -> false

let private squadDir (rt: CoordinatorRuntime) : string = rt.ProjectRoot + "/.squad"

let saveState (rt: CoordinatorRuntime) : unit =
    try
        let dir = squadDir rt
        if not (existsSync dir) then mkdirSync dir
        let tasks =
            rt.Dag.Tasks |> Map.toList |> List.map snd
            |> List.map (fun t ->
                box {| id = t.Id; title = t.Title; description = t.Description
                       dependsOn = List.toArray t.DependsOn
                       status = statusToString t.Status
                       sessionId = rt.Dag.SessionId |})
        let snapshot = box {| sessionId = rt.Dag.SessionId; requirement = rt.Dag.RootRequirement; tasks = List.toArray tasks |}
        writeFileSync (squadDir rt + "/state.json") (JS.JSON.stringify snapshot)
    with ex -> console?error (sprintf "saveState failed: %s" (string ex)) |> ignore

let loadStateFallback (rt: CoordinatorRuntime) : unit =
    try
        let path = squadDir rt + "/state.json"
        if not (existsSync path) then ()
        else
            let data = JS.JSON.parse (readFileSync path)
            let sid = str data "sessionId"
            if sid <> "" then rt.Dag <- { rt.Dag with SessionId = sid; RootRequirement = str data "requirement" }
            let tasksArr = get data "tasks"
            if not (isNullish tasksArr) && isArray tasksArr then
                for t in tasksArr :?> obj array do
                    let tid = str t "id"
                    let status = statusFromString (str t "status")
                    match status with
                    | Some s ->
                        let depsRaw = get t "dependsOn"
                        let deps = if isNullish depsRaw then [] else (depsRaw :?> obj array) |> Array.map string |> Array.toList
                        let task = create tid (str t "title") (str t "description") deps ""
                        rt.Dag <- rt.Dag |> addTask ({ task with Status = s })
                    | None -> ()
    with ex -> console?error (sprintf "loadStateFallback failed: %s" (string ex)) |> ignore

let startConfigWatch (rt: CoordinatorRuntime) : unit =
    try
        fsWatch (rt.ProjectRoot + "/AGENTS.md") (fun _ ->
            try rt.Config <- readConfig rt.ProjectRoot with ex -> console?error (sprintf "config reload failed: %s" (string ex)) |> ignore)
        |> ignore
    with ex -> console?error (sprintf "startConfigWatch failed: %s" (string ex)) |> ignore
