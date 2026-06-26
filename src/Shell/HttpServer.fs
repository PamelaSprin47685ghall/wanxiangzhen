module Wanxiangzhen.Shell.HttpServer

open Fable.Core
open Fable.Core.JsInterop
open Wanxiangzhen.Shell.Dyn

[<Import("createServer", "node:http")>]
let private createServer (handler: System.Func<obj, obj, unit>) : obj = jsNative

[<Global>]
let private JSON : obj = jsNative

type HttpResponse = {
    StatusCode: int
    Body: obj
}

type RouteHandler = string -> string -> string -> JS.Promise<HttpResponse>

let private readBody (req: obj) : JS.Promise<string> =
    Promise.create (fun resolve reject ->
        let chunks = ResizeArray<string>()
        req?on("data", System.Func<obj, unit>(fun chunk ->
            chunks.Add(string chunk))) |> ignore
        req?on("end", System.Func<unit, unit>(fun () ->
            resolve (chunks |> System.String.Concat))) |> ignore
        req?on("error", System.Func<obj, unit>(fun e ->
            reject (exn (string e)))) |> ignore)

let private writeResponse (res: obj) (statusCode: int) (body: obj) : unit =
    let json = string (JSON?stringify(body))
    let headers = createObj [ "Content-Type", box "application/json" ]
    res?writeHead(statusCode, headers) |> ignore
    res?("end")(json) |> ignore

type StartedServer = {
    Port: int
    Url: string
    Close: unit -> unit
}

let startServer (token: string) (handler: RouteHandler) : JS.Promise<StartedServer> =
    Promise.create (fun resolve reject ->
        let server =
            createServer (System.Func<obj, obj, unit>(fun req res ->
                (promise {
                    let method = string (req?method)
                    let url = string (req?url)
                    let headers = req?headers
                    let auth =
                        if isNullish headers then ""
                        else
                            let v = str headers "authorization"
                            if v <> "" then v else str headers "Authorization"
                    if auth <> "Bearer " + token then
                        writeResponse res 401 (box {| result = "unauthorized" |})
                    else
                        let! body = readBody req
                        try
                            let parsed = if body = "" then box null else JSON?parse(body)
                            let! reply = handler method url (string parsed)
                            writeResponse res reply.StatusCode reply.Body
                        with ex ->
                            writeResponse res 400 (box {| result = "bad_request"; error = string ex.Message |})
                } |> Promise.start) : unit
            ))
        server?once("listening", System.Func<unit, unit>(fun () ->
            let addr = server?address()
            let port = unbox<int> (addr?port)
            resolve {
                Port = port
                Url = "http://127.0.0.1:" + string port
                Close = fun () -> server?close() |> ignore
            })) |> ignore
        server?once("error", System.Func<obj, unit>(fun e ->
            reject (exn (string e)))) |> ignore
        server?listen(0, "127.0.0.1") |> ignore)
