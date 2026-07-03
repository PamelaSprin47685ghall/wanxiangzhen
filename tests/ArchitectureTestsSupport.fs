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
