module Wanxiangzhen.Tests.CommandHookTests

open Fable.Core
open Fable.Core.JsInterop
open Wanxiangzhen.Plugin
open Wanxiangzhen.Shell.Dyn
open Wanxiangzhen.Tests.Assert

let entries () : (string * (unit -> unit)) list = [
    ("mutateOutputParts clears existing array in place", fun () ->
        let original = System.Collections.Generic.List<obj>()
        original.Add(box {| ``type`` = "text"; text = "/squad-status" |})
        let output = createObj [ "parts", box original ]
        let replacement = box {| ``type`` = "text"; text = "replaced" |}
        mutateOutputParts output replacement
        let result = get output "parts" :?> System.Collections.Generic.List<obj>
        check (result.Count = 1)
        equal "replaced" (str result.[0] "text")
        // The same array reference must be retained so opencode sees the mutation.
        check (obj.ReferenceEquals(original, result)))

    ("mutateOutputParts creates array when absent", fun () ->
        let output = createObj []
        let replacement = box {| ``type`` = "text"; text = "new" |}
        mutateOutputParts output replacement
        let result = get output "parts" :?> System.Collections.Generic.List<obj>
        check (result.Count = 1)
        equal "new" (str result.[0] "text"))
]
