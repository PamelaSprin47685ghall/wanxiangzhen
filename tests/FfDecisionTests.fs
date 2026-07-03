module Wanxiangzhen.Tests.FfDecisionTests

open Wanxiangzhen.Kernel.FfDecision
open Wanxiangzhen.Tests.Assert

let entries () : (string * (unit -> unit)) list = [
    ("FfDecision.format Merged", fun () ->
        let r = formatSubmitOutcome "main" (Response (Merged "sha1"))
        checkBare (r.Contains "Merged")
        checkBare (r.Contains "sha1"))

    ("FfDecision.format RebaseNeeded", fun () ->
        let r = formatSubmitOutcome "main" (Response (RebaseNeeded "sha2"))
        checkBare (r.Contains "rebase"))

    ("FfDecision.format StaleCommit", fun () ->
        let r = formatSubmitOutcome "main" (Response StaleCommit)
        checkBare (r.Contains "differs"))

    ("FfDecision.format CoordinatorNotReady", fun () ->
        let r = formatSubmitOutcome "main" (Response (CoordinatorNotReady "dirty"))
        checkBare (r.Contains "not ready"))

    ("FfDecision.format NotSubmittable", fun () ->
        let r = formatSubmitOutcome "main" (Response (NotSubmittable "done"))
        checkBare (r.Contains "done"))

    ("FfDecision.format TaskNotFound", fun () ->
        let r = formatSubmitOutcome "main" TaskNotFound
        checkBare (r.Contains "not found"))

    ("FfDecision.format CoordinatorUnreachable", fun () ->
        let r = formatSubmitOutcome "main" CoordinatorUnreachable
        checkBare (r.Contains "unreachable"))

    ("FfDecision.format LocalGitError", fun () ->
        let r = formatSubmitOutcome "main" (LocalGitError "not a git repository")
        checkBare (r.Contains "Local git error")
        checkBare (r.Contains "not a git repository"))
]
