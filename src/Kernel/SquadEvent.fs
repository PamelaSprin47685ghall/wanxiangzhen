module Wanxiangzhen.Kernel.SquadEvent

open Wanxiangzhen.Kernel.Task
open Wanxiangzhen.Kernel.Dag

type SquadEvent =
    | SquadCreated of sessionId: string * requirement: string
    | TasksCreated of sessionId: string * tasks: (string * string * string * string list) list
    | TaskStarted of sessionId: string * taskId: string * worktreePath: string * branchName: string
    | TaskSubmitted of sessionId: string * taskId: string * commitSha: string
    | TaskMerged of sessionId: string * taskId: string * masterSha: string
    | TaskDone of sessionId: string * taskId: string * merged: bool
    | SquadCancelled of sessionId: string

let eventSessionId (e: SquadEvent) : string =
    match e with
    | SquadCreated (sid, _)
    | TasksCreated (sid, _)
    | TaskStarted (sid, _, _, _)
    | TaskSubmitted (sid, _, _)
    | TaskMerged (sid, _, _)
    | TaskDone (sid, _, _)
    | SquadCancelled sid -> sid

let eventTypeName (e: SquadEvent) : string =
    match e with
    | SquadCreated _ -> "squad_created"
    | TasksCreated _ -> "tasks_created"
    | TaskStarted _ -> "task_started"
    | TaskSubmitted _ -> "task_submitted"
    | TaskMerged _ -> "task_merged"
    | TaskDone _ -> "task_done"
    | SquadCancelled _ -> "squad_cancelled"

let eventTypeNameFromString (s: string) : string option =
    match s with
    | "squad_created" | "tasks_created" | "task_started" | "task_submitted"
    | "task_merged" | "task_done" | "squad_cancelled" -> Some s
    | _ -> None

let eventProse (e: SquadEvent) : string =
    match e with
    | SquadCreated (_, _req) ->
        "Decompose this requirement into independently executable tasks. Each task should:\n\
         - Be completable within a single git worktree\n\
         - Have clear completion criteria\n\
         - Minimize file conflicts with other tasks\n\
         Express dependencies via dependsOn (dependency must be merged first).\n\
         Call the squad_update tool with an events array containing all task_created events."
    | TasksCreated (_, tasks) ->
        let count = List.length tasks
        sprintf "%d tasks created. Nothing needs to be done. The scheduler will start them as dependencies are met." count
    | TaskStarted (_, _, _, _) -> "Task started in worktree. Nothing needs to be done."
    | TaskSubmitted (_, _, _) -> "Task submitted for fast-forward check. Nothing needs to be done."
    | TaskMerged (_, _, sha) -> sprintf "Task merged into master @ %s. Nothing needs to be done." sha
    | TaskDone (_, _, merged) ->
        if merged then "Task slave exited after successful merge."
        else "Task slave exited (not merged). DAG continues."
    | SquadCancelled _ -> "Squad session cancelled by user. Remaining tasks marked cancelled."

let foldEvent (dag: Dag) (e: SquadEvent) : Dag =
    match e with
    | SquadCreated (sid, req) -> { dag with SessionId = sid; RootRequirement = req }
    | TasksCreated (_, tasks) ->
        tasks |> List.fold (fun d (tid, title, desc, deps) ->
            let t = Wanxiangzhen.Kernel.Task.create tid title desc deps ""
            addTask t d) dag
    | TaskStarted (_, tid, wt, branch) ->
        dag |> updateTask tid (fun t ->
            { t with Status = Running; WorktreePath = Some wt; BranchName = Some branch })
    | TaskSubmitted (_, tid, _) ->
        dag |> updateTask tid (fun t -> { t with Status = Submitted })
    | TaskMerged (_, tid, sha) ->
        dag |> updateTask tid (fun t -> { t with Status = Merged; MergedSha = Some sha })
    | TaskDone (_, tid, _) ->
        dag |> updateTask tid (fun t -> { t with Status = Done })
    | SquadCancelled _ ->
        { dag with Tasks = dag.Tasks |> Map.map (fun _ t -> if isTerminal t.Status then t else { t with Status = Cancelled }) }

let foldEvents (events: SquadEvent list) (dag: Dag) : Dag =
    List.fold foldEvent dag events
