module Wanxiangzhen.Shell.Yaml

open Fable.Core
open Fable.Core.JsInterop

[<ImportAll("yaml")>]
let private yamlLib: obj = jsNative

let private stringifyOptions = createObj [ "lineWidth", box 0 ]

let parse (text: string) : obj = yamlLib?parse text
let stringify (value: obj) : string = yamlLib?stringify(value, stringifyOptions)
