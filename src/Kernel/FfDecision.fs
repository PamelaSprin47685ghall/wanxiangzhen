module Wanxiangzhen.Kernel.FfDecision

type FfResult =
    | Merged of masterSha: string
    | RebaseNeeded of masterSha: string
    | StaleCommit
    | CoordinatorNotReady of reason: string
    | NotSubmittable of currentStatus: string

type SubmitOutcome =
    | Response of FfResult
    | TaskNotFound
    | CoordinatorUnreachable

let ffResultLabel (r: FfResult) : string =
    match r with
    | Merged _ -> "merged"
    | RebaseNeeded _ -> "rebase_needed"
    | StaleCommit -> "stale_commit"
    | CoordinatorNotReady _ -> "coordinator_not_ready"
    | NotSubmittable _ -> "not_submittable"
