module Wanxiangzhen.Kernel.Task

type TaskStatus =
    | Pending
    | Running
    | Submitted
    | Merged
    | Done
    | Cancelled

type Task = {
    Id: string
    Title: string
    Description: string
    DependsOn: string list
    Status: TaskStatus
    WorktreePath: string option
    BranchName: string option
    SlavePid: int option
    MergedSha: string option
    CreatedAt: string
    UpdatedAt: string
}

let taskIdPrefix = "squad-"

let statusToString (s: TaskStatus) : string =
    match s with
    | Pending -> "pending"
    | Running -> "running"
    | Submitted -> "submitted"
    | Merged -> "merged"
    | Done -> "done"
    | Cancelled -> "cancelled"

let statusFromString (s: string) : TaskStatus option =
    match s with
    | "pending" -> Some Pending
    | "running" -> Some Running
    | "submitted" -> Some Submitted
    | "merged" -> Some Merged
    | "done" -> Some Done
    | "cancelled" -> Some Cancelled
    | _ -> None

let isTerminal (s: TaskStatus) : bool =
    match s with Merged | Done | Cancelled -> true | _ -> false

let canTransition (from: TaskStatus) (toStatus: TaskStatus) : bool =
    match from, toStatus with
    | Pending, Running | Pending, Cancelled -> true
    | Running, Submitted | Running, Done | Running, Cancelled -> true
    | Submitted, Merged | Submitted, Running | Submitted, Done | Submitted, Cancelled -> true
    | _ -> false

let withStatus (task: Task) (newStatus: TaskStatus) (now: string) : Task =
    { task with Status = newStatus; UpdatedAt = now }

let create (id: string) (title: string) (description: string)
           (dependsOn: string list) (now: string) : Task =
    { Id = id; Title = title; Description = description; DependsOn = dependsOn
      Status = Pending; WorktreePath = None; BranchName = None
      SlavePid = None; MergedSha = None
      CreatedAt = now; UpdatedAt = now }
