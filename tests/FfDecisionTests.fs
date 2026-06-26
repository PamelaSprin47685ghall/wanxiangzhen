module Wanxiangzhen.Tests.FfDecisionTests

open Wanxiangzhen.Kernel.FfDecision
open Wanxiangzhen.Tests.Assert

let entries () : (string * (unit -> unit)) list = [
    ("FfDecision.format Merged", fun () ->
        let r = formatSubmitOutcome "main" (Response (Merged "sha1"))
        check (r.Contains "Merged")
        check (r.Contains "sha1"))

    ("FfDecision.format RebaseNeeded", fun () ->
        let r = formatSubmitOutcome "main" (Response (RebaseNeeded "sha2"))
        check (r.Contains "rebase"))

    ("FfDecision.format StaleCommit", fun () ->
        let r = formatSubmitOutcome "main" (Response StaleCommit)
        check (r.Contains "differs"))

    ("FfDecision.format CoordinatorNotReady", fun () ->
        let r = formatSubmitOutcome "main" (Response (CoordinatorNotReady "dirty"))
        check (r.Contains "not ready"))

    ("FfDecision.format NotSubmittable", fun () ->
        let r = formatSubmitOutcome "main" (Response (NotSubmittable "done"))
        check (r.Contains "done"))

    ("FfDecision.format TaskNotFound", fun () ->
        let r = formatSubmitOutcome "main" TaskNotFound
        check (r.Contains "not found"))

    ("FfDecision.format CoordinatorUnreachable", fun () ->
        let r = formatSubmitOutcome "main" CoordinatorUnreachable
        check (r.Contains "unreachable"))
]
