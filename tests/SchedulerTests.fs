module Wanxiangzhen.Tests.SchedulerTests

open Wanxiangzhen.Kernel.Task
open Wanxiangzhen.Kernel.Dag
open Wanxiangzhen.Kernel.Scheduler
open Wanxiangzhen.Tests.Assert

let private mkTask id deps status =
    { Id = id; Title = ""; Description = ""; DependsOn = deps
      Status = status; WorktreePath = None; BranchName = None
      SlavePid = None; LastHeartbeatAt = None; MergedSha = None; CreatedAt = ""; UpdatedAt = "" }

let entries () : (string * (unit -> unit)) list = [
    ("Scheduler.empty dag", fun () ->
        let d = empty "s1" ""
        let dec = decide d 3
        check dec.TasksToStart.IsEmpty)

    ("Scheduler.one ready", fun () ->
        let d = empty "s1" "" |> addTask (mkTask "a" [] Pending)
        let dec = decide d 3
        equal ["a"] dec.TasksToStart)

    ("Scheduler.concurrency limit", fun () ->
        let d = empty "s1" ""
                |> addTask (mkTask "a" [] Pending)
                |> addTask (mkTask "b" [] Pending)
        let dec = decide d 1
        equal 1 dec.TasksToStart.Length
        equal 1 dec.TasksWaiting.Length)

    ("Scheduler.dep not merged", fun () ->
        let d = empty "s1" ""
                |> addTask (mkTask "dep" [] Pending)
                |> addTask (mkTask "a" ["dep"] Pending)
        let dec = decide d 3
        check (not (dec.TasksToStart |> List.contains "a")))

    ("Scheduler.dep merged starts dependent", fun () ->
        let d = empty "s1" ""
                |> addTask (mkTask "dep" [] Merged)
                |> addTask (mkTask "a" ["dep"] Pending)
        let dec = decide d 3
        check (dec.TasksToStart |> List.contains "a"))

    ("Scheduler.full concurrency", fun () ->
        let d = empty "s1" ""
                |> addTask (mkTask "a" [] Pending)
                |> addTask (mkTask "b" [] Pending)
                |> addTask (mkTask "c" [] Pending)
        let dec = decide d 5
        equal 3 dec.TasksToStart.Length)
]
