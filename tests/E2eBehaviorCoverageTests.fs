module Wanxiangzhen.Tests.E2eBehaviorCoverageTests

open Wanxiangzhen.Tests.Assert

/// Registry test: aggregate entry-list lengths from the three e2e mock suites so
/// the full behavior surface is documented and CI fails if a suite regresses.
let entries () : (string * (unit -> unit)) list = [
    ("e2e_behavior_coverage.registry", fun () ->
        let mockLen   = Wanxiangzhen.Tests.MockE2eTests.entriesAsync () |> List.length
        let openLen   = Wanxiangzhen.Tests.OpencodePluginE2eTests.entriesAsync () |> List.length
        let extLen    = Wanxiangzhen.Tests.ExtendedMockE2eTests.entriesAsync () |> List.length
        chk "coverage.mock_e2e_ge_6"   (mockLen >= 6)
        chk "coverage.opencode_e2e_ge_10" (openLen >= 10)
        chk "coverage.ext_mock_e2e_ge_25" (extLen >= 25)
        printfn "  e2e coverage: mock=%d opencode=%d ext=%d" mockLen openLen extLen
        printfn "  gap doc: see E2eBehaviorGapTests.entries () for behavior coverage registry")
]
