module Shell.CoordinatorState
open System
open System.Collections.Generic
open Kernel
open Shell.PromiseQueue
open Shell.Clock

// ─── SubmitResult DU ───────────────────────────────────────────
type SubmitResult =
    | Merged of masterSha:string
    | RebaseNeeded of masterSha:string * message:string
    | StaleCommit of message:string
    | CoordinatorNotReady of reason:string * message:string
    | NotSubmittable of currentStatus:string

// ─── SchedulerSideEffect ───────────────────────────────────────────
type SchedulerSideEffect =
    | InjectEvent of EventPayload * string
    | SpawnSlave of Task * SquadConfig

// ─── CoordinatorState（唯一可变模块）────────────────────────────────
// IV2: CoordinatorState 是 DAG+event 投影唯一写入入口
type CoordinatorState(dag0: Dag, config: SquadConfig, gitQueue: SerialQueue, injectQueue: SerialQueue) =

    let mutable dag = dag0
    let mutable masterSessionId: string option = None
    let mutable masterBranch = config.masterBranch
    let mutable cfg = config
    let mutable coordinatorUrl = ""
    let mutable token = ""

    let isTerminal (task: Task) =
        match task.status with
        | TaskStatus.Merged _ | TaskStatus.Done | TaskStatus.Cancelled -> true
        | _ -> false

    member _.TryGetTask(taskId:TaskId) =
        match dag.tasks.TryGetValue(taskId) with
        | true, t -> Some t
        | _ -> None

    member _.GetAllTasks() = dag.tasks |> Map.toSeq |> Seq.map (fun (id, t) -> t, id)

    member _.RegisterPid(taskId:TaskId, pid:int) =
        match dag.tasks.TryGetValue(taskId) with
        | true, t ->
            let updated = { t with slavePid = Some pid; updatedAt = utcNowIso() }
            dag <- { dag with tasks = dag.tasks.Add(taskId, updated) }
        | _ -> ()

    member _.OnSubmitResult(taskId:TaskId, result:SubmitResult, masterSha:string) =
        let sid = dag.sessionId
        match dag.tasks.TryGetValue(taskId) with
        | true, t ->
            let newStatus, mergedShaOpt =
                match result with
                | SubmitResult.Merged sha -> TaskStatus.Merged sha, Some sha
                | SubmitResult.RebaseNeeded _ -> TaskStatus.Running, None
                | SubmitResult.StaleCommit _ -> TaskStatus.Running, None
                | SubmitResult.CoordinatorNotReady _ -> TaskStatus.Running, None
                | SubmitResult.NotSubmittable _ -> t.status, t.mergedSha
            let updated = { t with status = newStatus; mergedSha = mergedShaOpt; updatedAt = utcNowIso() }
            dag <- { dag with tasks = dag.tasks.Add(taskId, updated) }
            let baseEvt = [EventPayload.taskSubmitted sid taskId t.slavePid]
            match result with
            | SubmitResult.Merged sha -> baseEvt @ [EventPayload.taskMerged sid taskId sha]
            | _ -> baseEvt
        | _ -> []

    member _.OnSlaveExit(taskId:TaskId) =
        match dag.tasks.TryGetValue(taskId) with
        | true, t when isTerminal t -> []
        | true, t ->
            let updated = { t with status = Done; updatedAt = utcNowIso() }
            dag <- { dag with tasks = dag.tasks.Add(taskId, updated) }
            [EventPayload.taskDone dag.sessionId taskId (Some false)]
        | _ -> []

    member _.ReplayFromHistory(sessionId:string) (events:EventPayload list) =
        dag <- { sessionId = sessionId; tasks = Map.empty; rootRequirement = "" }
        dag <- foldAll events dag utcNowIso

    member _.ApplyDag (events: EventPayload list) : Dag =
        let dag1 = dag
        let dag2 = foldAll events dag1 utcNowIso
        dag <- dag2
        dag2

    member _.GitReconcile(isAncestor: TaskId -> string option) =
        let updated = Kernel.gitReconcileDag dag isAncestor
        dag <- updated

    member _.GetStateView() = dag.tasks |> Map.map (fun id t -> t, id)

    member _.Dag with get() = dag
    member _.MasterSessionId with get() = masterSessionId and set v = masterSessionId <- v
    member _.MasterBranch with get() = masterBranch
    member _.Config with get() = cfg
    member _.CoordinatorUrl with get() = coordinatorUrl
    member _.Token with get() = token
    member _.SetCoordinatorUrl(v) = coordinatorUrl <- v
    member _.SetToken(v) = token <- v
    member _.InjectQueue with get() = injectQueue
    member _.GitQueue with get() = gitQueue

// ─── 工厂 ─────────────────────────────────────────────────────
let create (initialDag: Dag) (config: SquadConfig) (gitQueue: SerialQueue) (injectQueue: SerialQueue) : CoordinatorState =
    CoordinatorState(initialDag, config, gitQueue, injectQueue)
