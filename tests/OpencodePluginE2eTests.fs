module Wanxiangzhen.Tests.OpencodePluginE2eTests

let entriesAsync () : (string * (unit -> Fable.Core.JS.Promise<unit>)) list =
    Wanxiangzhen.Tests.OpencodePluginE2eHooksTests.entriesAsync ()
    @ Wanxiangzhen.Tests.OpencodePluginE2eFlowTests.entriesAsync ()
    @ Wanxiangzhen.Tests.OpencodePluginE2eCancelKillTests.entriesAsync ()
    @ Wanxiangzhen.Tests.OpencodePluginE2eSlaveQueryTests.entriesAsync ()
    @ Wanxiangzhen.Tests.OpencodePluginE2eIdTests.entriesAsync ()
