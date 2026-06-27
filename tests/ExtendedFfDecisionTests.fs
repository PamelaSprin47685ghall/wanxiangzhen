module Wanxiangzhen.Tests.ExtendedFfDecisionTests

open Wanxiangzhen.Kernel.FfDecision
open Wanxiangzhen.Tests.Assert

let entries () : (string * (unit -> unit)) list = [
    ("FfDecision.ffResultLabel Merged returns 'merged'", fun () ->
        let label = ffResultLabel (Merged "abc123")
        equal "merged" label)

    ("FfDecision.ffResultLabel RebaseNeeded returns 'rebase_needed'", fun () ->
        let label = ffResultLabel (RebaseNeeded "abc123")
        equal "rebase_needed" label)

    ("FfDecision.ffResultLabel StaleCommit returns 'stale_commit'", fun () ->
        let label = ffResultLabel StaleCommit
        equal "stale_commit" label)

    ("FfDecision.ffResultLabel CoordinatorNotReady returns 'coordinator_not_ready'", fun () ->
        let label = ffResultLabel (CoordinatorNotReady "dirty")
        equal "coordinator_not_ready" label)

    ("FfDecision.ffResultLabel NotSubmittable returns 'not_submittable'", fun () ->
        let label = ffResultLabel (NotSubmittable "merged")
        equal "not_submittable" label)
]
