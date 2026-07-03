module Wanxiangzhen.Tests.Assert

open Fable.Core
open Fable.Core.JsInterop

[<Global>]
let private console : obj = jsNative

let mutable private passCount = 0
let mutable private failureCount = 0
let mutable private currentLabel = ""
let mutable private failureLabels : string list = []

let setCurrentLabel (label: string) = currentLabel <- label

let reset () =
    passCount <- 0
    failureCount <- 0
    failureLabels <- []

/// Alias for reset — used by e2e runner to clear state before each run.
let clearFailuresForRun () = reset ()

let private pass () = passCount <- passCount + 1

let recordFailure () =
    failureCount <- failureCount + 1
    failureLabels <- currentLabel :: failureLabels
    console?error ("FAIL: " + currentLabel) |> ignore

let recordException (msg: string) =
    failureCount <- failureCount + 1
    failureLabels <- currentLabel :: failureLabels
    console?error ("FAIL: " + currentLabel + " -- " + msg) |> ignore

let checkBare (cond: bool) = if cond then pass () else recordFailure ()

/// Labeled check — records the label with the failure for diagnostics.
let check (label: string) (cond: bool) =
    if cond then pass ()
    else
        failureCount <- failureCount + 1
        failureLabels <- label :: failureLabels
        console?error ("FAIL: " + label) |> ignore

let chk (label: string) (cond: bool) = check label cond

let equal (expected: 'a) (actual: 'a) = checkBare (expected = actual)
let isSome (x: 'a option) = checkBare x.IsSome
let isNone (x: 'a option) = checkBare (not x.IsSome)

let summary () : int =
    let total = passCount + failureCount
    console?log (sprintf "%d passed, %d failed (%d total)" passCount failureCount total) |> ignore
    if failureCount > 0 then
        failureLabels |> List.rev |> List.iter (fun l -> console?log ("  - " + l) |> ignore)
    failureCount
