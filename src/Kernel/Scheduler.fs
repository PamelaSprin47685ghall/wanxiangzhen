module Wanxiangzhen.Kernel.Scheduler

open Wanxiangzhen.Kernel.Task
open Wanxiangzhen.Kernel.Dag

type ScheduleDecision = {
    TasksToStart: string list
    TasksWaiting: string list
}

let decide (dag: Dag) (maxConcurrent: int) : ScheduleDecision =
    let occupied = runningCount dag
    let available = max 0 (maxConcurrent - occupied)
    let ready = readyTasks dag
    let toStart = ready |> List.truncate available |> List.map (fun t -> t.Id)
    let waiting = ready |> List.skip (min available ready.Length) |> List.map (fun t -> t.Id)
    { TasksToStart = toStart; TasksWaiting = waiting }
