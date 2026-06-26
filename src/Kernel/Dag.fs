module Wanxiangzhen.Kernel.Dag

open Wanxiangzhen.Kernel.Task

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

let topologicalOrder (tasks: (string * string list) list) : Result<string list, string list> =
    let idSet = tasks |> List.map fst |> Set.ofList
    let depMap = tasks |> Map.ofList
    let visited = System.Collections.Generic.HashSet<string>()
    let visiting = System.Collections.Generic.HashSet<string>()
    let result = System.Collections.Generic.List<string>()
    let cyclePath = System.Collections.Generic.List<string>()
    let mutable hasCycle = false
    let rec visit (id: string) =
        if hasCycle then ()
        elif visiting.Contains id then
            hasCycle <- true
            cyclePath.Add id |> ignore
        elif visited.Contains id then ()
        else
            visiting.Add id |> ignore
            match Map.tryFind id depMap with
            | Some deps -> deps |> List.iter visit
            | None -> ()
            visiting.Remove id |> ignore
            if not hasCycle then
                visited.Add id |> ignore
                result.Add id |> ignore
    idSet |> Set.iter visit
    if hasCycle then
        cyclePath |> Seq.toList |> Error
    else
        result |> Seq.toList |> Ok

let detectCycle (tasks: (string * string list) list) : string list option =
    match topologicalOrder tasks with
    | Ok _ -> None
    | Error cycle -> Some cycle
