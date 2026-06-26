module Shell.NodeInterop
open Fable.Core
open System
open System.Text
open Fable.Core.JsInterop

// ─── Node.js globals ─────────────────────────────────────────────
[<Global("process")>]
let nodeProcess: obj = jsNative
[<Global("process.pid")>]
let nodePid: int = jsNative
[<Global("Buffer")>]
let buffer: obj = jsNative

// ─── child_process ───────────────────────────────────────────────
[<Import("execSync", "node:child_process")>]
let execSync (command: string) (options: obj): string = jsNative
[<Import("spawn", "node:child_process")>]
let spawn (command: string) (args: string[]) (options: obj): obj = jsNative
[<Emit("$0.pid || 0")>]
let getChildPid (child: obj) : int = jsNative

// ─── http ────────────────────────────────────────────────────────
[<Import("createServer", "node:http")>]
let createHttpServer (handler: obj -> obj -> unit): obj = jsNative
[<Import("ServerResponse", "node:http")>]
let serverResponse: obj = jsNative

// ─── os ──────────────────────────────────────────────────────────
[<Import("tmpdir", "node:os")>]
let osTmpdir (): string = jsNative

// ─── path ────────────────────────────────────────────────────────
[<Import("join", "node:path")>]
let pathJoin (parts: string[]): string = jsNative
[<Import("relative", "node:path")>]
let pathRelative (from: string) (to_: string): string = jsNative

// ─── fs ──────────────────────────────────────────────────────────
[<Import("existsSync", "node:fs")>]
let fsExistsSync (path: string): bool = jsNative
[<Import("readFileSync", "node:fs")>]
let fsReadFileSync (path: string) (encoding: string): string = jsNative
[<Import("writeFileSync", "node:fs")>]
let fsWriteFileSync (path: string) (data: string): unit = jsNative
[<Import("mkdirSync", "node:fs")>]
let fsMkdirSync (path: string) (options: obj): unit = jsNative

// ─── currentPid ───────────────────────────────────────────────────
/// Returns the current process PID.
/// #if FABLE_COMPILER → use Fable-emitted nodePid global (JS process.pid)
/// #else → use .NET Environment.ProcessId (native/.NET runtime)
let currentPid () : int =
#if FABLE_COMPILER
    nodePid
#else
    Environment.ProcessId
#endif

// ─── Helpers ──────────────────────────────────────────────────────
let nodeProcessKill (pid: int) (signal: string): bool =
    try
        nodeProcess?kill(pid, signal)
        true
    with _ -> false

let mkExecOptions (cwd: string) (env: obj) : obj =
    createObj [
        "cwd" ==> cwd
        "encoding" ==> "utf8"
        "env" ==> env
    ]

let mkSpawnOptions (cwd: string) (env: obj) : obj =
    createObj [
        "cwd" ==> cwd
        "env" ==> env
        "stdio" ==> "pipe"
    ]

// ─── Env ─────────────────────────────────────────────────────────
[<Global("process.env")>]
let nodeProcessEnv: obj = jsNative

let getEnv (name: string): string option =
    nodeProcessEnv?(name) |> Option.ofObj |> Option.map string

// ─── Git helper ──────────────────────────────────────────────────
let execGit (cwd: string) (args: string): string =
    let opts = mkExecOptions cwd null
    execSync("git " + args) opts

// ─── Path alias ───────────────────────────────────────────────────
let pathCombine = pathJoin

// ─── JSON helpers ─────────────────────────────────────────────────
[<Global("JSON")>]
let jsonGlobal: obj = jsNative
let jsonParse (s: string): obj = jsonGlobal?parse(s)
let jsonStringify (o: obj): string = jsonGlobal?stringify(o)

// ─── HTTP synchronous helpers for slave mode ──────────────────────
// Uses System.Net.Http.HttpClient directly, blocks with RunSynchronously.
// Returns plain string (body on success, error JSON on failure).
open System.Net.Http
open System.Threading

let private httpClient = new HttpClient()

let private httpFetch (method: string) (url: string) (token: string) (body: string) : string =
    try
        let req = new HttpRequestMessage(HttpMethod(method), url)
        req.Headers.Authorization <- System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token)
        if method = "POST" && body <> "" then
            req.Content <- new StringContent(body, Encoding.UTF8, "application/json")
        use resp = httpClient.SendAsync(req) |> Async.AwaitTask |> Async.RunSynchronously
        if resp.IsSuccessStatusCode then
            resp.Content.ReadAsStringAsync() |> Async.AwaitTask |> Async.RunSynchronously
        else
            $"{{\"result\":\"coordinator_unreachable\",\"status\":{int resp.StatusCode}}}"
    with ex ->
        $"{{\"result\":\"coordinator_unreachable\",\"message\":\"{ex.Message}\"}}"

/// httpGet: synchronous GET with Bearer token, returns body string
let httpGet (baseUrl: string) (token: string) (path: string) : string =
    httpFetch "GET" (baseUrl + path) token ""

/// httpPost: synchronous POST with Bearer token + JSON body, returns body string
let httpPost (baseUrl: string) (token: string) (path: string) (body: string) : string =
    httpFetch "POST" (baseUrl + path) token body

