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
    @ Wanxiangzhen.Tests.ExtendedFfDecisionTests.entries ()
    @ Wanxiangzhen.Tests.HttpCodecTests.entries ()
    @ Wanxiangzhen.Tests.DynTests.entries ()
    @ Wanxiangzhen.Tests.SquadPromptsTests.entries ()
    @ Wanxiangzhen.Tests.SlaveSpawnTests.entries ()
    @ Wanxiangzhen.Tests.SquadConfigTests.entries ()
    @ Wanxiangzhen.Tests.CommandHookTests.entries ()
    @ Wanxiangzhen.Tests.CoordinatorOpsTests.entries ()
    @ Wanxiangzhen.Tests.PidMonitorTests.entries ()
    @ Wanxiangzhen.Tests.SerialQueueTests.entries ()
    @ Wanxiangzhen.Tests.SlaveRuntimeTests.entries ()
    @ Wanxiangzhen.Tests.CoordinatorLifecycleTests.entries ()
    @ Wanxiangzhen.Tests.PluginTests.entries ()
    @ Wanxiangzhen.Tests.ExtendedCoordinatorOpsTests.entries ()
    @ Wanxiangzhen.Tests.GitShellTests.entries ()
    @ Wanxiangzhen.Tests.ConfigReaderTests.entries ()
    @ Wanxiangzhen.Tests.SessionIoTests.entries ()

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
