module Kernel

open System
open System.Collections.Generic
// SquadConfig
type SquadConfig = {
    maxConcurrent: int
    masterBranch: string
    terminal: string
    sharedDirs: string list
}

// TaskStatus DU
type TaskStatus =
    | Pending
    | Running
    | Submitted
    | Merged of masterSha:string
    | Done
    | Cancelled

// TaskId
type TaskId = TaskId of string

// Task
type Task = {
    id: TaskId
    title: string
    description: string
    dependsOn: TaskId list
    status: TaskStatus
    worktreePath: string option
    branchName: string option
    slavePid: int option
    lastHeartbeatAt: string option
    mergedSha: string option
    createdAt: string
    updatedAt: string
}

// VALID_TRANSITIONS
let tryTransition (fromS:TaskStatus) (toS:TaskStatus) : Result<TaskStatus, string> =
    if fromS = toS then Ok fromS
    else
        match fromS, toS with
        | Pending, Running -> Ok Running
        | Pending, Cancelled -> Ok Cancelled
        | Running, Submitted -> Ok Submitted
        | Running, Done -> Ok Done
        | Running, Cancelled -> Ok Cancelled
        | Submitted, Merged _ -> Ok toS
        | Submitted, Running -> Ok Running
        | Merged _, Submitted -> Error "Merged is terminal"
        | Merged _, Running -> Error "Merged is terminal"
        | Merged _, Done -> Error "Merged is terminal"
        | Merged _, Cancelled -> Error "Merged is terminal"
        | Merged _, Pending -> Error "Merged is terminal"
        | Done, Merged _ -> Error "Done is terminal"
        | Done, Running -> Error "Done is terminal"
        | Done, Pending -> Error "Done is terminal"
        | Done, Cancelled -> Error "Done is terminal"
        | Done, Submitted -> Error "Done is terminal"
        | Cancelled, Pending -> Error "Cancelled is terminal"
        | Cancelled, Running -> Error "Cancelled is terminal"
        | Cancelled, Submitted -> Error "Cancelled is terminal"
        | Cancelled, Merged _ -> Error "Cancelled is terminal"
        | Cancelled, Done -> Error "Cancelled is terminal"
        | Pending, Merged _ -> Error "Cannot skip Running"
        | Pending, Done -> Error "Cannot skip Running"
        | Pending, Submitted -> Error "Cannot skip Running"
        | Submitted, Done -> Ok Done
        | Submitted, Cancelled -> Ok Cancelled
        | Running, Merged _ -> Error "Illegal: Running -> Merged (must go through Submitted)"
        | _ -> Error (sprintf "Illegal transition: %A -> %A" fromS toS)

// DAG
type Dag = {
    sessionId: string
    tasks: Map<TaskId, Task>
    rootRequirement: string
}

// topoSort
let topoSort (dag: Dag) : Result<TaskId list, string> =
    let rec visit (nodeId: TaskId) (visiting: Set<TaskId>) (visited: Set<TaskId>) (acc: TaskId list) : Result<TaskId list * Set<TaskId>, string> =
        if visited.Contains(nodeId) then Ok (acc, visited)
        elif visiting.Contains(nodeId) then
            Error (sprintf "Cycle detected at %A" nodeId)
        else
            let visiting = visiting.Add(nodeId)
            let accResult =
                match dag.tasks.TryGetValue(nodeId) with
                | true, task ->
                    task.dependsOn
                    |> List.fold (fun accResult depId ->
                        match accResult with
                        | Error e -> Error e
                        | Ok (acc, visited) ->
                            visit depId visiting visited acc
                    ) (Ok (acc, visited))
                | _ -> Error (sprintf "Task %A not found in DAG" nodeId)
            match accResult with
            | Error e -> Error e
            | Ok (acc, visited) ->
                let visited = visited.Add(nodeId)
                Ok (nodeId :: acc, visited)

    dag.tasks
    |> Map.toList
    |> List.fold (fun accResult (nodeId, _) ->
        match accResult with
        | Error e -> Error e
        | Ok (acc, visited) ->
            let visiting = Set.empty
            visit nodeId visiting visited acc
    ) (Ok ([], Set.empty))
    |> Result.map fst

// SquadEvent DU
type SquadEvent =
    | SquadCreated
    | TaskCreated
    | TaskStarted
    | TaskSubmitted
    | TaskMerged
    | TaskDone
    | SquadCancelled

// EventPayload
type EventPayload = {
    squadEvent: SquadEvent
    sessionId: string
    taskId: TaskId option
    title: string option
    description: string option
    dependsOn: TaskId list
    masterSha: string option
    worktreePath: string option
    branchName: string option
    slavePid: int option
    merged: bool option
    requirement: string option
}

// EventPayload constructors
module EventPayload =
    let taskCreated (sessionId: string) (taskId: TaskId) (title: string) (description: string) (dependsOn: TaskId list) : EventPayload =
        { squadEvent = TaskCreated
          sessionId = sessionId
          taskId = Some taskId
          title = Some title
          description = Some description
          dependsOn = dependsOn
          masterSha = None
          worktreePath = None
          branchName = None
          slavePid = None
          merged = None
          requirement = None }

    let taskStarted (sessionId: string) (taskId: TaskId) (worktreePath: string option) (branchName: string option) (slavePid: int option) : EventPayload =
        { squadEvent = TaskStarted
          sessionId = sessionId
          taskId = Some taskId
          title = None
          description = None
          dependsOn = []
          masterSha = None
          worktreePath = worktreePath
          branchName = branchName
          slavePid = slavePid
          merged = None
          requirement = None }

    let taskSubmitted (sessionId: string) (taskId: TaskId) (slavePid: int option) : EventPayload =
        { squadEvent = TaskSubmitted
          sessionId = sessionId
          taskId = Some taskId
          title = None
          description = None
          dependsOn = []
          masterSha = None
          worktreePath = None
          branchName = None
          slavePid = slavePid
          merged = None
          requirement = None }

    let taskMerged (sessionId: string) (taskId: TaskId) (masterSha: string) : EventPayload =
        { squadEvent = TaskMerged
          sessionId = sessionId
          taskId = Some taskId
          title = None
          description = None
          dependsOn = []
          masterSha = Some masterSha
          worktreePath = None
          branchName = None
          slavePid = None
          merged = None
          requirement = None }

    let taskDone (sessionId: string) (taskId: TaskId) (merged: bool option) : EventPayload =
        { squadEvent = TaskDone
          sessionId = sessionId
          taskId = Some taskId
          title = None
          description = None
          dependsOn = []
          masterSha = None
          worktreePath = None
          branchName = None
          slavePid = None
          merged = merged
          requirement = None }

    let squadCreated (sessionId: string) (requirement: string) : EventPayload =
        { squadEvent = SquadCreated
          sessionId = sessionId
          taskId = None
          title = None
          description = None
          dependsOn = []
          masterSha = None
          worktreePath = None
          branchName = None
          slavePid = None
          merged = None
          requirement = Some requirement }

// foldEvent
let foldEvent (dag: Dag) (evt: EventPayload) (utcNow: unit -> string) : Dag =
    let ts = utcNow()
    match evt.squadEvent with
    | SquadCreated ->
        let req = evt.requirement |> Option.defaultValue dag.rootRequirement
        { dag with sessionId = evt.sessionId; rootRequirement = req }

    | TaskCreated ->
        let taskId = evt.taskId |> Option.get
        let task = {
            id = taskId
            title = evt.title |> Option.defaultValue ""
            description = evt.description |> Option.defaultValue ""
            dependsOn = evt.dependsOn
            status = Pending
            worktreePath = None
            branchName = None
            slavePid = None
            lastHeartbeatAt = None
            mergedSha = None
            createdAt = ts
            updatedAt = ts
        }
        { dag with tasks = dag.tasks.Add(taskId, task) }

    | TaskStarted ->
        match evt.taskId with
        | Some taskId when dag.tasks.ContainsKey(taskId) ->
            let old = dag.tasks.[taskId]
            match tryTransition old.status Running with
            | Ok _ ->
                let updated = {
                    old with
                        status = Running
                        worktreePath = evt.worktreePath
                        branchName = evt.branchName
                        slavePid = evt.slavePid
                        updatedAt = ts
                }
                { dag with tasks = dag.tasks.Add(taskId, updated) }
            | Error _ -> dag
        | _ -> dag

    | TaskSubmitted ->
        match evt.taskId with
        | Some taskId when dag.tasks.ContainsKey(taskId) ->
            let old = dag.tasks.[taskId]
            match tryTransition old.status Submitted with
            | Ok _ ->
                let updated = {
                    old with
                        status = Submitted
                        slavePid = evt.slavePid
                        updatedAt = ts
                }
                { dag with tasks = dag.tasks.Add(taskId, updated) }
            | Error _ -> dag
        | _ -> dag

    | TaskMerged ->
        match evt.taskId with
        | Some taskId when dag.tasks.ContainsKey(taskId) ->
            let old = dag.tasks.[taskId]
            let masterSha = evt.masterSha |> Option.defaultValue ""
            match tryTransition old.status (Merged masterSha) with
            | Ok _ ->
                let updated = {
                    old with
                        status = Merged masterSha
                        mergedSha = Some masterSha
                        updatedAt = ts
                }
                { dag with tasks = dag.tasks.Add(taskId, updated) }
            | Error _ -> dag
        | _ -> dag

    | TaskDone ->
        match evt.taskId with
        | Some taskId when dag.tasks.ContainsKey(taskId) ->
            let old = dag.tasks.[taskId]
            match old.status with
            | Merged _ | Done | Cancelled -> dag
            | _ ->
                let updated = {
                    old with
                        status = Done
                        updatedAt = ts
                }
                { dag with tasks = dag.tasks.Add(taskId, updated) }
        | _ -> dag

    | SquadCancelled ->
        let cancelledTasks =
            dag.tasks
            |> Map.toSeq
            |> Seq.map (fun (id, task) ->
                match task.status with
                | Merged _ | Done | Cancelled -> task
                | _ ->
                    { task with
                        status = Cancelled
                        updatedAt = ts
                    }
            )
            |> Seq.map (fun t -> t.id, t)
            |> Map.ofSeq
        { dag with tasks = cancelledTasks }

// foldAll
let foldAll (events: EventPayload list) (initial: Dag) (utcNow: unit -> string) : Dag =
    events |> List.fold (fun dag evt -> foldEvent dag evt utcNow) initial

// buildEventProse
let buildEventProse (evt: SquadEvent) (task: Task option) : string =
    match evt with
    | SquadCreated ->
        let req = task |> Option.map (fun t -> t.description) |> Option.defaultValue ""
        sprintf "Squad session created. Requirement: %s\nNothing needs to be done. The scheduler will handle the rest." req
    | TaskCreated ->
        let title = task |> Option.map (fun t -> t.title) |> Option.defaultValue ""
        sprintf "Task '%s' created.\nNothing needs to be done. The scheduler will start it once dependencies are merged." title
    | TaskStarted ->
        let title = task |> Option.map (fun t -> t.title) |> Option.defaultValue ""
        sprintf "Task '%s' started. Worktree created, slave process launched.\nNothing needs to be done." title
    | TaskSubmitted ->
        let title = task |> Option.map (fun t -> t.title) |> Option.defaultValue ""
        sprintf "Task '%s' submitted for fast-forward merge.\nNothing needs to be done. The scheduler will handle the merge." title
    | TaskMerged ->
        let title = task |> Option.map (fun t -> t.title) |> Option.defaultValue ""
        let sha = task |> Option.bind (fun t -> t.mergedSha) |> Option.defaultValue ""
        sprintf "Task '%s' merged into masterBranch @ %s.\nYou may summarize progress to the user." title sha
    | TaskDone ->
        let title = task |> Option.map (fun t -> t.title) |> Option.defaultValue ""
        sprintf "Task '%s' slave process exited.\nNothing needs to be done. The scheduler will handle the rest." title
    | SquadCancelled ->
        "Squad session cancelled by /squad-kill.\nNothing needs to be done."

// ─── buildSlavePrompt ──────────────────────────────────────────
let buildSlavePrompt (task: Task) (vibeFsDetected: bool) : string =
    let taskId = match task.id with TaskId s -> s
    let title = task.title
    let desc = task.description
    let masterBranch = task.branchName |> Option.defaultValue taskId
    if vibeFsDetected then
        let lines = [
            "wanxiangshu /loop（With-Review Mode）可用。请按 review 流程开发："
            "1. 用 /loop <任务描述> 激活 With-Review Mode"
            "2. 完成开发后调用 submit_review 提交审查"
            "3. review 通过（PASS）后再 git commit，最后调用 submit_to_squad 提交到 coordinator"
            "4. 若 review REJECT，按反馈修改后重新 review，直至 PASS"
            ""
            sprintf "你正在执行 万象阵 任务 %s：%s" taskId title
            "任务描述："
            desc
        ]
        String.concat "\n" lines
    else
        sprintf "你正在执行 万象阵 任务 %s：%s\n\n任务描述：\n%s\n\n请在当前 worktree 中完成上述任务。完成后：\n1. git add + git commit（在当前分支 %s 上提交）\n2. 调用 submit_to_squad 工具提交到 coordinator\n若被要求 rebase，执行 git rebase %s 后重新提交。\n拿不准全局状态时调用 query_squad 工具。" taskId title desc taskId masterBranch

// ─── gitReconcileDag ────────────────────────────────────────────
// Pure function: upgrade Running/Submitted tasks to Merged when isAncestor
// returns Some sha.  Cancelled is preserved unconditionally.
let gitReconcileDag (dag: Dag) (isAncestor: TaskId -> string option) : Dag =
    let mutable updated = dag
    let tasks = dag.tasks |> Map.toSeq |> Seq.map snd |> Seq.toList
    for task in tasks do
        match task.status with
        | TaskStatus.Running
        | TaskStatus.Submitted ->
            let branchName = defaultArg task.branchName (match task.id with TaskId s -> s)
            match isAncestor task.id with
            | Some sha when sha <> "" ->
                let old = updated.tasks.[task.id]
                let fixedTask = { old with status = TaskStatus.Merged sha; mergedSha = Some sha }
                updated <- { updated with tasks = updated.tasks.Add(task.id, fixedTask) }
            | _ -> ()
        | TaskStatus.Cancelled -> ()   // cancelled is explicit user intent; preserve regardless of git state
        | _ -> ()
    updated

// ─── TickInput / TickOutput ──────────────────────────────────────
type TickInput = {
    dag: Dag
    config: SquadConfig
}

type TickOutput = {
    toStart: Task list
    events: EventPayload list
}

// ─── Scheduler.tick ─────────────────────────────────────────────
let tick (input: TickInput) : TickOutput =
    let isReadyForMerge (task: Task) =
        task.dependsOn
        |> List.forall (fun depId ->
            match input.dag.tasks.TryGetValue(depId) with
            | true, dep ->
                match dep.status with
                | Merged _ -> true
                | _ -> false
            | _ -> false
        )
    // count occupied slots: Running + Submitted
    let occupiedSlots =
        input.dag.tasks
        |> Map.toSeq
        |> Seq.map snd
        |> Seq.filter (fun t ->
            match t.status with
            | Running | Submitted -> true
            | _ -> false
        )
        |> Seq.length
    let remainingSlots = max 0 (input.config.maxConcurrent - occupiedSlots)
    let pendingTasks =
        input.dag.tasks
        |> Map.toSeq
        |> Seq.filter (fun (_, t) ->
            match t.status with
            | Pending -> isReadyForMerge t
            | _ -> false
        )
        |> Seq.truncate remainingSlots
    let toStart = pendingTasks |> Seq.map snd |> Seq.toList
    let events =
        toStart
        |> List.map (fun t ->
            EventPayload.taskStarted
                input.dag.sessionId t.id
                (Some (sprintf "worktree-%s" (match t.id with TaskId s -> s)))
                (Some (match t.id with TaskId s -> s))
                None
        )
    { toStart = toStart; events = events }
