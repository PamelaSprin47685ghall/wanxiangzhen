module Wanxiangzhen.Tests.Main

open Fable.Core
open Fable.Core.JsInterop
open Fable.Core.JS
open Wanxiangzhen.Tests.Assert

let private allSyncTests : (string * (unit -> unit)) list =
    Wanxiangzhen.Tests.TaskTests.entries ()
    @ Wanxiangzhen.Tests.DagTests.entries ()
    @ Wanxiangzhen.Tests.EventReplayTests.entries ()
    @ Wanxiangzhen.Tests.SchedulerTests.entries ()
    @ Wanxiangzhen.Tests.EventCodecTests.entries ()
    @ Wanxiangzhen.Tests.FfDecisionTests.entries ()

let runAll (_args: string array) : JS.Promise<int> =
    promise {
        reset ()
        for (label, body) in allSyncTests do
            setCurrentLabel label
            try body ()
            with ex -> console?error(sprintf "EXCEPTION in %s: %s" label (string ex)) |> ignore
        return summary ()
    }

[<Global>]
let private console : obj = jsNative
