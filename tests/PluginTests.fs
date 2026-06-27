module Wanxiangzhen.Tests.PluginTests

open Fable.Core
open Fable.Core.JsInterop
open Wanxiangzhen.Kernel.Task
open Wanxiangzhen.Kernel.Dag
open Wanxiangzhen.Shell.Dyn
open Wanxiangzhen.Shell.SerialQueue
open Wanxiangzhen.Shell.CoordinatorRuntime
open Wanxiangzhen.Plugin
open Wanxiangzhen.Tests.Assert

let entries () : (string * (unit -> unit)) list = [
    ("mutateOutputParts with null parts creates array", fun () ->
        let output = createObj []
        let part = box "hello"
        mutateOutputParts output part
        let parts = get output "parts"
        check (not (isNullish parts))
        let arr = unbox<obj array> parts
        equal 1 arr.Length
        equal "hello" (unbox<string> arr.[0]))

    ("mutateOutputParts with existing parts list replaces content", fun () ->
        let list = System.Collections.Generic.List<obj>()
        list.Add(box "old")
        let output = createObj [ "parts", box list ]
        mutateOutputParts output (box "new")
        equal 1 list.Count
        equal "new" (unbox<string> list.[0]))
]
