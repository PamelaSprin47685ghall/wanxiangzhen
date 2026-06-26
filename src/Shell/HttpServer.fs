module Shell.HttpServer

open System
open System.Text
open System.Text.Json
open Fable.Core
open Fable.Core.JsInterop
open Kernel

type RouteHandler = string * string * string -> JS.Promise<string>  // tid * body * token

let private serialize (o: obj) = JsonSerializer.Serialize(o, JsonSerializerOptions(WriteIndented = false))

let jsonResponse (code: int) (body: string) : string =
    let status = match code with 200 -> "OK" | 400 -> "Bad Request" | 401 -> "Unauthorized" | 404 -> "Not Found" | _ -> "Internal Server Error"
    let bytes = Encoding.UTF8.GetBytes(body)
    $"HTTP/1.1 {code} {status}\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n{body}"

let errJson (tag: string) = jsonResponse 400 $"{{\"result\":\"{tag}\"}}"

// ─── Node interop ──────────────────────────────────────────────────
[<Import("createServer", "node:http")>]
let createHttpServer (handler: obj -> obj -> unit) : obj = jsNative
[<Import("listen", "node:http")>]
let httpListen (server: obj) (port: int) (hostname: string) (cb: obj -> unit) : unit = jsNative
[<Import("address", "node:http")>]
let serverAddress (server: obj) : {| port: int |} = jsNative

let private sendResponse (res: obj) (respStr: string) : JS.Promise<unit> =
    promise {
        let bytes = Encoding.UTF8.GetBytes(respStr)
        res?write(bytes, 0, bytes.Length) |> ignore
        res?``end``() |> ignore
    }

let private errCb (ex: exn) : JS.Promise<string> =
    let body = jsonResponse 500 (sprintf "{\"result\":\"server_error\",\"message\":\"%s\"}" ex.Message)
    Promise.lift body

let private authHeaders (req: obj) (token: string) : bool * int =
    let mutable authOk = false
    let mutable contentLength = 0
    let headers = unbox<System.Collections.Generic.IDictionary<string,string>> (req?headers)
    if headers.ContainsKey("authorization") then
        let bearer = headers.["authorization"]
        authOk <- bearer.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) &&
                  bearer.Substring("Bearer ".Length).Trim() = token
    if headers.ContainsKey("content-length") then
        Int32.TryParse(headers.["content-length"], &contentLength) |> ignore
    authOk, contentLength

let private parseUrl (url: string) : string * string list =
    let firstSpace = url.IndexOf(' ')
    let method =
        if firstSpace >= 0 then url.Substring(0, firstSpace).ToUpper()
        else url.ToUpper()
    let qIdx = url.IndexOf('?')
    let rawPath = if qIdx >= 0 then url.Substring(0, qIdx) else url
    let pathParts = rawPath.Trim('/').Split('/') |> Array.filter ((<>) "") |> Array.toList
    method, pathParts

type HttpServer(token: string, initialPort: int) =
    let mutable running = true
    let mutable actualPort = initialPort
    let mutable hSubmit: RouteHandler option = None
    let mutable hTask: (string -> JS.Promise<string>) option = None
    let mutable hRegister: (string * string -> JS.Promise<string>) option = None
    let mutable hDone: (string -> JS.Promise<string>) option = None
    let mutable hState: (unit -> JS.Promise<string>) option = None

    member _.Port with get() = actualPort

    member _.SetSubmitHandler(h: RouteHandler) = hSubmit <- Some h
    member _.SetTaskHandler(h: string -> JS.Promise<string>) = hTask <- Some h
    member _.SetRegisterHandler(h: string * string -> JS.Promise<string>) = hRegister <- Some h
    member _.SetDoneHandler(h: string -> JS.Promise<string>) = hDone <- Some h
    member _.SetStateHandler(h: unit -> JS.Promise<string>) = hState <- Some h

    member _.Start() =
        let server = createHttpServer(fun (req: obj) (res: obj) ->
            let mutable chunks = ResizeArray<byte[]>()
            let mutable total = 0
            let onData (chunk: obj) =
                let b = chunk :?> byte[]
                chunks.Add(b)
                total <- total + b.Length
            req?on("data", onData) |> ignore
            req?on("error", fun (e: obj) -> printfn "[wanxiangzhen] req error: %s" (string e)) |> ignore
            let onEnd (_: obj) =
                let body =
                    if total = 0 then ""
                    else
                        let all = chunks |> Seq.collect id |> Seq.toArray
                        Encoding.UTF8.GetString(all)
                // Route + respond
                let respond (respStr: string) : JS.Promise<unit> =
                    sendResponse res respStr

                let route () : JS.Promise<string> =
                    let url = string (req?url)
                    let method, pathParts = parseUrl url
                    let authOk, _contentLen = authHeaders req token
                    if not authOk then
                        Promise.lift (jsonResponse 401 "{\"result\":\"unauthorized\"}")
                    else
                        match method, pathParts with
                        | "POST", ["task"; tid; "submit"] ->
                            match hSubmit with
                            | Some h -> h(tid, body, token)
                            | None -> Promise.lift (jsonResponse 500 "{\"result\":\"server_error\"}")
                        | "GET", ["task"; tid] ->
                            match hTask with
                            | Some h -> h tid
                            | None -> Promise.lift (jsonResponse 500 "{\"result\":\"server_error\"}")
                        | "POST", ["task"; tid; "register"] ->
                            match hRegister with
                            | Some h -> h(tid, body)
                            | None -> Promise.lift (jsonResponse 500 "{\"result\":\"server_error\"}")
                        | "POST", ["task"; tid; "done"] ->
                            match hDone with
                            | Some h -> h tid
                            | None -> Promise.lift (jsonResponse 500 "{\"result\":\"server_error\"}")
                        | "GET", ["state"] ->
                            match hState with
                            | Some h -> h ()
                            | None -> Promise.lift (jsonResponse 500 "{\"result\":\"server_error\"}")
                        | _ -> Promise.lift (jsonResponse 404 "{\"result\":\"not_found\"}")
                route ()
                |> Promise.catch (fun ex -> jsonResponse 500 (sprintf "{\"result\":\"server_error\",\"message\":\"%s\"}" (ex.Message.Replace("\"","\\\""))))
                |> Promise.bind (fun respStr -> respond respStr)
                |> ignore
            req?on("end", onEnd) |> ignore
        )
        httpListen server initialPort "127.0.0.1" (fun (_: obj) ->
            let addr = serverAddress server
            actualPort <- addr.port
        )
        ()

    member _.Stop() = running <- false
    interface IDisposable with
        member x.Dispose() = x.Stop()

let createServer (token: string) (port: int) : HttpServer = new HttpServer(token, port)
