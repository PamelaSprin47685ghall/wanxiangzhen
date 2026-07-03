module Wanxiangzhen.Tests.E2eIntegrationTests

open Fable.Core
open Fable.Core.JsInterop

/// Real integration tests live in e2e/Tests.fs (run via `npm run e2e`).
/// This module is intentionally empty so the unit-test runner does nothing.
let entriesAsync () : (string * (unit -> JS.Promise<unit>)) list = []
