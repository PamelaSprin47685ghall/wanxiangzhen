module Wanxiangzhen.Kernel.SquadEvent

open Wanxiangzhen.Kernel.Task
open Wanxiangzhen.Kernel.Dag

type SquadEventType =
    | SquadCreated
    | TaskCreated
    | TaskStarted
    | TaskSubmitted
    | TaskMerged
    | TaskDone
    | SquadCancelled

type SquadEvent = {
    Type: SquadEventType
    SessionId: string
    TaskId: string option
    Title: string option
    Description: string option
    DependsOn: string list option
    WorktreePath: string option
    BranchName: string option
    SlavePid: int option
    CommitSha: string option
    MasterSha: string option
    Merged: bool option
}

let eventTypeName (t: SquadEventType) : string =
    match t with
    | SquadCreated -> "squad_created"
    | TaskCreated -> "task_created"
    | TaskStarted -> "task_started"
    | TaskSubmitted -> "task_submitted"
    | TaskMerged -> "task_merged"
    | TaskDone -> "task_done"
    | SquadCancelled -> "squad_cancelled"

let eventTypeFromString (s: string) : SquadEventType option =
    match s with
    | "squad_created" -> Some SquadCreated
    | "task_created" -> Some TaskCreated
    | "task_started" -> Some TaskStarted
    | "task_submitted" -> Some TaskSubmitted
    | "task_merged" -> Some TaskMerged
    | "task_done" -> Some TaskDone
    | "squad_cancelled" -> Some SquadCancelled
    | _ -> None

let private taskIdOf (e: SquadEvent) : string =
    e.TaskId |> Option.defaultValue ""

let foldEvent (dag: Dag) (e: SquadEvent) : Dag =
    match e.Type with
    | SquadCreated -> { dag with SessionId = e.SessionId }
    | TaskCreated ->
        let t = Wanxiangzhen.Kernel.Task.create (taskIdOf e)
                        (e.Title |> Option.defaultValue "")
                        (e.Description |> Option.defaultValue "")
                        (e.DependsOn |> Option.defaultValue []) ""
        addTask t dag
    | TaskStarted ->
        dag |> updateTask (taskIdOf e) (fun t ->
            { t with Status = Running
                     WorktreePath = e.WorktreePath
                     BranchName = e.BranchName
                     SlavePid = e.SlavePid })
    | TaskSubmitted ->
        dag |> updateTask (taskIdOf e) (fun t -> { t with Status = Submitted })
    | TaskMerged ->
        dag |> updateTask (taskIdOf e) (fun t ->
            { t with Status = Merged; MergedSha = e.MasterSha })
    | TaskDone ->
        dag |> updateTask (taskIdOf e) (fun t -> { t with Status = Done })
    | SquadCancelled ->
        { dag with Tasks = dag.Tasks |> Map.map (fun _ t -> if isTerminal t.Status then t else { t with Status = Cancelled }) }

let foldEvents (events: SquadEvent list) (dag: Dag) : Dag =
    List.fold foldEvent dag events
