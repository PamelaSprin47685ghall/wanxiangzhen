module Wanxiangzhen.Tests.SpinWait

open Fable.Core
open Fable.Core.JsInterop

let rec private tickUntil (pred: unit -> bool) (remaining: int) : JS.Promise<unit> =
    promise {
        if remaining <= 0 then return ()
        elif pred () then return ()
        else
            let! _ = Promise.lift ()
            return! tickUntil pred (remaining - 1)
    }

/// Poll pred on microtask boundaries up to maxSteps.
/// Returns immediately if pred already true; yields microtask between checks.
let spinUntil (pred: unit -> bool) (maxSteps: int) : JS.Promise<unit> = tickUntil pred maxSteps

/// Poll pred on microtask boundaries; failwith if maxSteps exhausted without pred becoming true.
let spinUntilFail (pred: unit -> bool) (maxSteps: int) : JS.Promise<unit> =
    promise {
        let! _ = tickUntil pred maxSteps
        if not (pred ()) then
            failwithf "spinUntilFail: predicate not satisfied within %d microtask steps" maxSteps
    }
