module Wanxiangzhen.Tests.E2eHarnessContractTests

open Wanxiangzhen.Tests.Assert
open Wanxiangzhen.Tests.ArchitectureTestsSupport

/// E2e harness PLUGIN_JS must resolve to build/src/Plugin.js from both
/// repo-root runs (__dirname=e2e) and build-dir runs (__dirname=build/e2e).
let pluginJsResolvesWithParentFallback () =
    let code = requireFile "e2e/harness.js"
    chk "e2e.harness.PLUGIN_JS targets build/src/Plugin.js"
        (code.Contains("build/src/Plugin.js"))
    chk "e2e.harness.PLUGIN_JS has existsSync fallback to parent"
        (code.Contains("existsSync") && (code.Contains("../..") || code.Contains("'..', '..', 'build'")))

/// E2e harness must use .wanxiangzhen-e2e.lock for exclusive run serialization.
let lockFileIsE2eLock () =
    let code = requireFile "e2e/harness.js"
    chk "e2e.harness uses .wanxiangzhen-e2e.lock"
        (code.Contains("wanxiangzhen-e2e.lock") || code.Contains("E2E_LOCK"))

/// E2e harness dispose must clean up .wanxiangzhen.ndjson event log.
let disposeCleansEventLog () =
    let code = requireFile "e2e/harness.js"
    chk "e2e.harness.dispose references .wanxiangzhen.ndjson"
        (code.Contains(".wanxiangzhen.ndjson"))

/// E2e harness must expose runCommand or session command path.
let exposesRunCommand () =
    let code = requireFile "e2e/harness.js"
    chk "e2e.harness exposes runCommand or session command"
        (code.Contains("runCommand") || code.Contains("/session/"))

/// E2e harness must use spinUntil for polling (no manual setTimeout spin loops).
/// Any setTimeout( must belong to the opencode serve timeout mechanism (matched by count).
let harnessUsesSpinUntilNoDelayPoll () =
    let code = requireFile "e2e/harness.js"
    chk "e2e.harness uses spinUntil" (code.Contains "spinUntil")
    chk "e2e.harness no delay( polling function"
        (not (code.Contains "delay("))
    let countSub (s: string) (sub: string) = s.Split([| sub |], System.StringSplitOptions.None).Length - 1
    let setTimeouts = countSub code "setTimeout("
    let opencodeTimeouts = countSub code "opencode serve timeout"
    chk "e2e.harness setTimeout only in opencode serve timeout"
        (setTimeouts <= opencodeTimeouts)

let supportsInProcessMode () =
    let code = requireFile "e2e/harness.js"
    chk "e2e.harness checks inProcess option" (code.Contains "inProcess")
    chk "e2e.harness has startInProcess entry" (code.Contains "startInProcess")
    chk "e2e.harness honors WANXIANGZHEN_E2E_INPROCESS env"
        (code.Contains "WANXIANGZHEN_E2E_INPROCESS")

let entries () : (string * (unit -> unit)) list = [
    ("pluginJsResolvesWithParentFallback", pluginJsResolvesWithParentFallback)
    ("lockFileIsE2eLock", lockFileIsE2eLock)
    ("disposeCleansEventLog", disposeCleansEventLog)
    ("exposesRunCommand", exposesRunCommand)
    ("supportsInProcessMode", supportsInProcessMode)
    ("harnessUsesSpinUntilNoDelayPoll", harnessUsesSpinUntilNoDelayPoll)
]
