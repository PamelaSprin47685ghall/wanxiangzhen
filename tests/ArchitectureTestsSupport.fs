module Wanxiangzhen.Tests.ArchitectureTestsSupport

open Fable.Core
open Fable.Core.JsInterop
open Wanxiangzhen.Tests.Assert

[<Import("readFileSync", "node:fs")>]
let readFileSync (path: string) (encoding: string) : string = jsNative

[<Import("existsSync", "node:fs")>]
let existsSync (path: string) : bool = jsNative

let requireFile (path: string) : string =
    chk ("arch: exists " + path) (existsSync path)
    if existsSync path then
        let content = readFileSync path "utf-8"
        chk ("arch: non-empty " + path) (not (System.String.IsNullOrEmpty content))
        content
    else ""

[<Import("readdirSync", "node:fs")>]
let readdirSync (path: string) : string array = jsNative

[<Import("statSync", "node:fs")>]
let statSync (path: string) : obj = jsNative

let isDirectory (path: string) : bool =
    let s = statSync path
    s?isDirectory () |> unbox

let rec collectFsFiles (dir: string) : string list =
    let entries = readdirSync dir |> Array.toList
    entries |> List.collect (fun name ->
        let full = dir + "/" + name
        if isDirectory full then collectFsFiles full
        elif name.EndsWith(".fs") then [full]
        else []
    )
