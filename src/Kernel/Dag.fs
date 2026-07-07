module Wanxiangzhen.Kernel.Dag

open Wanxiangzhen.Kernel.Task

type DagValidationError =
    | DanglingDependency of taskId: string * unknownDep: string
    | DependencyCycle of cycle: string list

type SquadUpdateOutcome =
    | Success
    | DependencyErrors of errors: (string * string) list
    | CycleDetected of cycle: string list
    | InvalidInput of message: string
    | IdExhausted

let formatSquadUpdateOutcome (o: SquadUpdateOutcome) : string =
    match o with
    | Success -> "Tasks created successfully."
    | DependencyErrors errs ->
        let msgs = errs |> List.map (fun (tid, dep) -> tid + " dependsOn unknown " + dep)
        sprintf "Dependency error: %s. Fix dependencies." (String.concat "; " msgs)
    | CycleDetected cycle ->
        sprintf "Dependency cycle detected: %s. Please re-decompose without cycles." (String.concat " → " cycle)
    | InvalidInput msg -> sprintf "Error: %s" msg
    | IdExhausted -> "Error: Could not allocate a unique task id after 10 attempts."

type Dag = {
    SessionId: string
    Tasks: Map<string, Task>
    RootRequirement: string
}

let empty (sessionId: string) (requirement: string) : Dag =
    { SessionId = sessionId; Tasks = Map.empty; RootRequirement = requirement }

let addTask (task: Task) (dag: Dag) : Dag =
    { dag with Tasks = dag.Tasks.Add(task.Id, task) }

let updateTask (taskId: string) (f: Task -> Task) (dag: Dag) : Dag =
    match dag.Tasks.TryFind taskId with
    | Some task -> { dag with Tasks = dag.Tasks.Add(taskId, f task) }
    | None -> dag

let findTask (taskId: string) (dag: Dag) : Task option =
    dag.Tasks.TryFind taskId

let isReady (task: Task) (dag: Dag) : bool =
    task.Status = Pending &&
    task.DependsOn |> List.forall (fun depId ->
        match dag.Tasks.TryFind depId with
        | Some dep -> dep.Status = Merged
        | None -> false)

let readyTasks (dag: Dag) : Task list =
    dag.Tasks
    |> Map.toList
    |> List.map snd
    |> List.filter (fun t -> isReady t dag)
    |> List.sortBy (fun t -> t.Id)

let runningCount (dag: Dag) : int =
    dag.Tasks
    |> Map.toList
    |> List.map snd
    |> List.filter (fun t -> t.Status = Running || t.Status = Submitted)
    |> List.length

type private VisitState = { Visited: Set<string>; Visiting: Set<string>; Result: string list }

let topologicalOrder (tasks: (string * string list) list) : Result<string list, string list> =
    let depMap = tasks |> Map.ofList
    let idSet = tasks |> List.map fst |> Set.ofList

    let rec visit (state: VisitState) (id: string) : Result<VisitState, string list> =
        if state.Visiting.Contains id then Error [ id ]
        elif state.Visited.Contains id then Ok state
        else
            let visiting' = state.Visiting.Add id
            let deps = Map.tryFind id depMap |> Option.defaultValue []
            let st' = { state with Visiting = visiting' }
            let rec processDeps (st: VisitState) (remaining: string list) : Result<VisitState, string list> =
                match remaining with
                | [] -> Ok st
                | dep :: rest ->
                    match visit st dep with
                    | Error _ as e -> e
                    | Ok stNext -> processDeps stNext rest
            match processDeps st' deps with
            | Error _ as e -> e
            | Ok stDep ->
                let stOut = { stDep with Visiting = stDep.Visiting.Remove id
                                         Visited = stDep.Visited.Add id
                                         Result = stDep.Result @ [ id ] }
                Ok stOut

    let rec processAll (st: VisitState) (ids: string list) : Result<VisitState, string list> =
        match ids with
        | [] -> Ok st
        | id :: rest ->
            match visit st id with
            | Error _ as e -> e
            | Ok stNext -> processAll stNext rest

    let initialState = { Visited = Set.empty; Visiting = Set.empty; Result = [] }
    let orderedIds = idSet |> Set.toList |> List.sort
    match processAll initialState orderedIds with
    | Ok finalState -> Ok finalState.Result
    | Error cycle -> Error cycle

let detectCycle (tasks: (string * string list) list) : string list option =
    match topologicalOrder tasks with
    | Ok _ -> None
    | Error cycle -> Some cycle

let formatDag (dag: Dag) : string =
    let sb = System.Text.StringBuilder()
    sb.Append(sprintf "Session: %s" dag.SessionId) |> ignore
    if dag.RootRequirement <> "" then sb.Append(sprintf "\nRequirement: %s" dag.RootRequirement) |> ignore
    if dag.Tasks.IsEmpty then sb.Append("\n(no tasks)") |> ignore
    else
        sb.Append("\n") |> ignore
        for t in dag.Tasks |> Map.toList |> List.map snd |> List.sortBy (fun t -> t.Id) do
            let deps = if t.DependsOn = [] then "-" else t.DependsOn |> String.concat ", "
            sb.Append(sprintf "\n  [%s] %s | deps: %s | %s" t.Id t.Title deps (Wanxiangzhen.Kernel.Task.statusToString t.Status)) |> ignore
    sb.ToString()
