module Wanxiangzhen.Kernel.SquadEvent

open Wanxiangzhen.Kernel.Task
open Wanxiangzhen.Kernel.Dag

type SquadEvent =
    | SquadCreated of sessionId: string * requirement: string
    | TaskCreated of sessionId: string * taskId: string * title: string * description: string * dependsOn: string list
    | TaskStarted of sessionId: string * taskId: string * worktreePath: string * branchName: string
    | TaskSubmitted of sessionId: string * taskId: string * commitSha: string
    | TaskMerged of sessionId: string * taskId: string * masterSha: string
    | TaskDone of sessionId: string * taskId: string * merged: bool
    | SquadCancelled of sessionId: string

let eventSessionId (e: SquadEvent) : string =
    match e with
    | SquadCreated (sid, _)
    | TaskCreated (sid, _, _, _, _)
    | TaskStarted (sid, _, _, _)
    | TaskSubmitted (sid, _, _)
    | TaskMerged (sid, _, _)
    | TaskDone (sid, _, _)
    | SquadCancelled sid -> sid

let eventTypeName (e: SquadEvent) : string =
    match e with
    | SquadCreated _ -> "squad_created"
    | TaskCreated _ -> "task_created"
    | TaskStarted _ -> "task_started"
    | TaskSubmitted _ -> "task_submitted"
    | TaskMerged _ -> "task_merged"
    | TaskDone _ -> "task_done"
    | SquadCancelled _ -> "squad_cancelled"

let eventTypeNameFromString (s: string) : string option =
    match s with
    | "squad_created" | "task_created" | "task_started" | "task_submitted"
    | "task_merged" | "task_done" | "squad_cancelled" -> Some s
    | _ -> None

let eventProse (e: SquadEvent) : string =
    match e with
    | SquadCreated (_, req) ->
        sprintf "Squad session created for: %s. Analyze the requirement and call squad_update to decompose into tasks." req
    | TaskCreated (_, _, title, _, _) ->
        sprintf "Task '%s' created. Nothing needs to be done. The scheduler will start it once dependencies are merged." title
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
    | TaskCreated (_, tid, title, desc, deps) ->
        let t = Wanxiangzhen.Kernel.Task.create tid title desc deps ""
        addTask t dag
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
