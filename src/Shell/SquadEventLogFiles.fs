module Wanxiangzhen.Shell.SquadEventLogFiles

open Fable.Core
open Fable.Core.JsInterop
open Wanxiangzhen.Kernel.SquadEvent
open Wanxiangzhen.Kernel.EventLog.Parse
open Wanxiangzhen.Shell.SquadEventLogCodec
open Wanxiangzhen.Shell.SerialQueue
open Wanxiangzhen.Shell.Dyn

[<Import("appendFile", "node:fs/promises")>]
let private appendFileAsync (path: string) (data: string) : JS.Promise<unit> = jsNative

[<Import("readFile", "node:fs/promises")>]
let private readFileAsync (path: string) (encoding: string) : JS.Promise<string> = jsNative

[<Import("open", "node:fs/promises")>]
let private openAsync (path: string) (flags: string) : JS.Promise<obj> = jsNative

[<Import("unlink", "node:fs/promises")>]
let private unlinkAsync (path: string) : JS.Promise<unit> = jsNative

type SquadEventLogStore(workspaceRoot: string) =
    let queue = SerialQueue()
    let root = workspaceRoot

    member _.ReadAllEvents() : JS.Promise<SquadEvent list> =
        queue.Enqueue(fun () ->
            promise {
                let path = eventPath root
                let! text =
                    promise {
                        try return! readFileAsync path "utf-8"
                        with _ -> return ""
                    }
                if text = "" then return []
                else
                    let lines = text.Split('\n') |> Array.toList
                    return parseLinesWithTruncate tryParseLine lines
            })

    member _.AppendEvent (at: string) (e: SquadEvent) : JS.Promise<Result<unit, string>> =
        queue.Enqueue(fun () ->
            promise {
                let lock = lockPath root
                let dataPath = eventPath root
                let! handle =
                    promise {
                        try return! openAsync lock "wx"
                        with _ -> return null
                    }
                if isNullish handle then return Error "event log lock held"
                else
                    let line = squadEventToLine at e + "\n"
                    let! appendRes =
                        promise {
                            try
                                do! appendFileAsync dataPath line
                                return Ok ()
                            with ex ->
                                return Error (string ex.Message)
                        }
                    try do! handle?close() |> unbox<JS.Promise<unit>> with _ -> ()
                    try do! unlinkAsync lock with _ -> ()
                    return appendRes
            })