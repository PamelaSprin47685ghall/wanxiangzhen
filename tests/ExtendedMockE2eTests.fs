module Wanxiangzhen.Tests.ExtendedMockE2eTests

open Fable.Core

let entriesAsync () : (string * (unit -> JS.Promise<unit>)) list =
    Wanxiangzhen.Tests.ExtendedMockE2eReplayTests.entriesAsync ()
    @ Wanxiangzhen.Tests.ExtendedMockE2eSchedulerTests.entriesAsync ()
    @ Wanxiangzhen.Tests.ExtendedMockE2eSubmitTests.entriesAsync ()
    @ Wanxiangzhen.Tests.ExtendedMockE2eSlaveHttpTests.entriesAsync ()
    @ Wanxiangzhen.Tests.ExtendedMockE2ePluginTests.entriesAsync ()
