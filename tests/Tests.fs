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
    @ Wanxiangzhen.Tests.EventLogParseTests.entries ()
    @ Wanxiangzhen.Tests.SquadEventLogCodecTests.entries ()
    @ Wanxiangzhen.Tests.SquadUpdateIdAssignTests.entries ()
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
    @ Wanxiangzhen.Tests.PluginTests.entries ()
    @ Wanxiangzhen.Tests.ExtendedCoordinatorOpsTests.entries ()
    @ Wanxiangzhen.Tests.GitShellTests.entries ()
    @ Wanxiangzhen.Tests.ConfigReaderTests.entries ()
    @ Wanxiangzhen.Tests.SessionIoTests.entries ()

let private allAsyncTests : (string * (unit -> JS.Promise<unit>)) list =
    Wanxiangzhen.Tests.CoordinatorLifecycleTests.entries ()
    @ Wanxiangzhen.Tests.MockE2eTests.entriesAsync ()
    @ Wanxiangzhen.Tests.OpencodePluginE2eTests.entriesAsync ()
    @ Wanxiangzhen.Tests.SlaveRuntimeTests.entriesAsync ()
    @ Wanxiangzhen.Tests.ExtendedMockE2eTests.entriesAsync ()
    @ Wanxiangzhen.Tests.SquadEventLogFsTests.entriesAsync ()

let runAll (_args: string array) : JS.Promise<int> =
    promise {
        reset ()
        // synchronous tests
        for (label, body) in allSyncTests do
            setCurrentLabel label
            try body ()
            with ex -> recordException (sprintf "EXCEPTION in %s: %s" label (string ex))
        // asynchronous mock e2e tests — 顺序 await
        for (label, body) in allAsyncTests do
            setCurrentLabel label
            try
                do! body ()
            with ex -> recordException (sprintf "ASYNC EXCEPTION in %s: %s" label (string ex))
        return summary ()
    }

[<Global>]
let private console : obj = jsNative
