module Wanxiangzhen.Tests.CoordinatorOpsTests

open Wanxiangzhen.Kernel.Dag
open Wanxiangzhen.Kernel.Task
open Wanxiangzhen.Tests.Assert

// ── helpers ──────────────────────────────────────────────────────────────────

let private mkTask id deps status =
    { Id = id; Title = id; Description = ""; DependsOn = deps
      Status = status; WorktreePath = None; BranchName = None
      SlavePid = None; LastHeartbeatAt = None; MergedSha = None
      CreatedAt = ""; UpdatedAt = "" }

// formatDagText in CoordinatorOps is private; test Dag.formatDag directly
// (CoordinatorOps.formatDagText is a one-line passthrough to Dag.formatDag).

// ── Dag.formatDag tests (replaces CoordinatorOps.formatDagText tests) ─────────

let entries () : (string * (unit -> unit)) list = [

    ("Dag.formatDag empty dag contains no-tasks marker", fun () ->
        let dag = empty "s1" ""
        let text = formatDag dag
        check (text.Contains "(no tasks)"))

    ("Dag.formatDag single task shows id and Pending", fun () ->
        let dag =
            empty "s1" ""
            |> addTask (mkTask "squad-a1b2" [] Pending)
        let text = formatDag dag
        check (text.Contains "squad-a1b2")
        check (text.Contains "pending"))

    ("Dag.formatDag multiple tasks appear in sorted order", fun () ->
        let dag =
            empty "s1" ""
            |> addTask (mkTask "squad-c3d4" [] Pending)
            |> addTask (mkTask "squad-a1b2" [] Pending)
            |> addTask (mkTask "squad-e5f6" [] Pending)
        let text = formatDag dag
        // IDs must appear in lexicographic order a1b2 < c3d4 < e5f6
        let idxA = text.IndexOf "squad-a1b2"
        let idxC = text.IndexOf "squad-c3d4"
        let idxE = text.IndexOf "squad-e5f6"
        check (idxA >= 0 && idxC >= 0 && idxE >= 0)
        check (idxA < idxC)
        check (idxC < idxE))

    ("Dag.formatDag shows RootRequirement when non-empty", fun () ->
        let dag = empty "s1" "add remember-me to login" |> addTask (mkTask "squad-a1b2" [] Pending)
        let text = formatDag dag
        check (text.Contains "add remember-me to login"))

    ("Dag.formatDag omits Requirement line when empty", fun () ->
        let dag = empty "s1" "" |> addTask (mkTask "squad-a1b2" [] Pending)
        let text = formatDag dag
        check (not (text.Contains "Requirement:")))

    ("Dag.formatDag shows Session id", fun () ->
        let dag = empty "my-session-001" "" |> addTask (mkTask "squad-a1b2" [] Pending)
        let text = formatDag dag
        check (text.Contains "my-session-001"))

    ("Dag.formatDag shows task deps when present", fun () ->
        let dag =
            empty "s1" ""
            |> addTask (mkTask "squad-dep" [] Merged)
            |> addTask (mkTask "squad-a1b2" ["squad-dep"] Pending)
        let text = formatDag dag
        check (text.Contains "squad-dep"))

    ("Dag.formatDag renders Running status", fun () ->
        let dag = empty "s1" "" |> addTask (mkTask "squad-a1b2" [] Running)
        let text = formatDag dag
        check (text.Contains "running"))
]
