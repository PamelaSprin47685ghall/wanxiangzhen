module Wanxiangzhen.Tests.OpencodePluginE2eHooksTests

open Fable.Core
open Fable.Core.JsInterop
open Wanxiangzhen.Shell.Dyn
open Wanxiangzhen.Plugin
open Wanxiangzhen.Tests.Assert
open Wanxiangzhen.Tests.OpencodePluginE2eHelpers

// Test 1 — plugin_with_deps returns hooks containing expected keys
let testPluginHooksShape () : JS.Promise<unit> =
    promise {
        let captures = { prompts=[]; commands=[]; messages=[] }
        let input    = mkMockInput captures
        let obs = mkDefaultObs ()
        let deps = mkObservableDeps captures obs
        let! result = pluginWithDeps input deps
        let h = result.hooks
        check (not (isNullish (get h "tool")))
        check (not (isNullish (get h "config")))
        check (not (isNullish (get h "command.execute.before")))
        check (not (isNullish (get h "dispose")))
    }

// Test 2 — config hook registers /squad /squad-kill /squad-status commands
let testConfigHookRegistersCommands () : JS.Promise<unit> =
    promise {
        let captures = { prompts=[]; commands=[]; messages=[] }
        let input    = mkMockInput captures
        let obs = mkDefaultObs ()
        let deps = mkObservableDeps captures obs
        let! result = pluginWithDeps input deps
        let configHook = get result.hooks "config"
        let cfg = createObj [ "command", box (createObj []) ]
        do! unbox<JS.Promise<unit>> (configHook $ (cfg))
        let cmds = get cfg "command"
        check (not (isNullish (get cmds "squad")))
        check (not (isNullish (get cmds "squad-kill")))
        check (not (isNullish (get cmds "squad-status")))
        let squadCmd = get cmds "squad"
        check ((str squadCmd "template") <> "")
        check ((str squadCmd "description") <> "")
    }

// Test 3 — dispose returns a thenable
let testDisposeReturnsPromise () : JS.Promise<unit> =
    promise {
        let captures = { prompts=[]; commands=[]; messages=[] }
        let input    = mkMockInput captures
        let obs = mkDefaultObs ()
        let deps = mkObservableDeps captures obs
        let! result = pluginWithDeps input deps
        let dispose = get result.hooks "dispose"
        do! unbox<JS.Promise<unit>> (dispose $ ())
    }

let entriesAsync () : (string * (unit -> JS.Promise<unit>)) list = [
    ("E2E.plugin_hooks_shape: pluginWithDeps returns hooks dict with expected keys",
     testPluginHooksShape)

    ("E2E.config_hook_registers_commands: config hook writes squad / squad-kill / squad-status",
     testConfigHookRegistersCommands)

    ("E2E.dispose_returns_promise: dispose returns thenable",
     testDisposeReturnsPromise)
]
