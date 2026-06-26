module Tests.Kernel

open Expecto
open Kernel

// ─── TaskStatus 合法/非法转移 ─────────────────────────────────────
[<Tests>]
let taskStatusTransitionTests =
    testList "Dag.tryTransition" [
        testCase "Pending -> Running (legal)" <| fun _ ->
            match tryTransition Pending Running with Ok s -> Expect.equal s Running "should be Running" | Error e -> failwith e

        testCase "Pending -> Cancelled (legal)" <| fun _ ->
            match tryTransition Pending Cancelled with Ok s -> Expect.equal s Cancelled "should be Cancelled" | Error e -> failwith e

        testCase "Running -> Submitted (legal)" <| fun _ ->
            match tryTransition Running Submitted with Ok s -> Expect.equal s Submitted "should be Submitted" | Error e -> failwith e

        testCase "Running -> Done (legal)" <| fun _ ->
            match tryTransition Running Done with Ok s -> Expect.equal s Done "should be Done" | Error e -> failwith e

        testCase "Running -> Cancelled (legal)" <| fun _ ->
            match tryTransition Running Cancelled with Ok s -> Expect.equal s Cancelled "should be Cancelled" | Error e -> failwith e

        testCase "Submitted -> Merged (legal)" <| fun _ ->
            match tryTransition Submitted (Merged "abc123") with Ok (Merged sha) -> Expect.equal sha "abc123" "sha match" | _ -> failwith "should be Merged"

        testCase "Submitted -> Running (legal, rebase)" <| fun _ ->
            match tryTransition Submitted Running with Ok s -> Expect.equal s Running "should be Running" | Error e -> failwith e

        testCase "Merged -> Running (terminal)" <| fun _ ->
            match tryTransition (Merged "abc") Running with Error e -> () | _ -> failwith "should reject terminal"

        testCase "Done -> Merged (terminal)" <| fun _ ->
            match tryTransition Done (Merged "abc") with Error e -> () | _ -> failwith "should reject terminal"

        testCase "Cancelled -> Pending (terminal)" <| fun _ ->
            match tryTransition Cancelled Pending with Error e -> () | _ -> failwith "should reject terminal"

        testCase "Pending -> Merged (illegal, skip Running)" <| fun _ ->
            match tryTransition Pending (Merged "abc") with Error _ -> () | _ -> failwith "should reject"
    ]

// ─── topoSort ─────────────────────────────────────────────────────
let makeTask (id: TaskId) (deps: TaskId list) =
    { id = id; title = ""; description = ""; dependsOn = deps; status = Pending
      worktreePath = None; branchName = None; slavePid = None; lastHeartbeatAt = None
      mergedSha = None; createdAt = ""; updatedAt = "" }

[<Tests>]
let topoSortTests =
    testList "topoSort" [
        testCase "empty DAG" <| fun _ ->
            let dag = { sessionId = ""; tasks = Map.empty; rootRequirement = "" }
            Expect.equal (topoSort dag |> Result.defaultValue []) [] "empty dag"

        testCase "single node" <| fun _ ->
            let dag = { sessionId = ""; tasks = Map.ofList [(TaskId "a", makeTask (TaskId "a") [])]; rootRequirement = "" }
            Expect.equal (topoSort dag |> Result.defaultValue []) [TaskId "a"] "single node"

        testCase "chain a->b->c" <| fun _ ->
            let a = makeTask (TaskId "a") []
            let b = makeTask (TaskId "b") [TaskId "a"]
            let c = makeTask (TaskId "c") [TaskId "b"]
            let dag = { sessionId = ""; tasks = Map.ofList [(TaskId "a", a); (TaskId "b", b); (TaskId "c", c)]; rootRequirement = "" }
            let topo = topoSort dag |> Result.defaultValue []
            Expect.equal topo [TaskId "c"; TaskId "b"; TaskId "a"] "chain order"

        testCase "diamond" <| fun _ ->
            let a = makeTask (TaskId "a") []
            let b = makeTask (TaskId "b") [TaskId "a"]
            let c = makeTask (TaskId "c") [TaskId "a"]
            let d = makeTask (TaskId "d") [TaskId "b"; TaskId "c"]
            let dag = { sessionId = ""; tasks = Map.ofList [(TaskId "a", a); (TaskId "b", b); (TaskId "c", c); (TaskId "d", d)]; rootRequirement = "" }
            let topo = topoSort dag |> Result.defaultValue []
            Expect.contains topo (TaskId "d") "d first"
            Expect.contains topo (TaskId "a") "a last"

        testCase "cycle a->b->a" <| fun _ ->
            let a = makeTask (TaskId "a") [TaskId "b"]
            let b = makeTask (TaskId "b") [TaskId "a"]
            let dag = { sessionId = ""; tasks = Map.ofList [(TaskId "a", a); (TaskId "b", b)]; rootRequirement = "" }
            match topoSort dag with Error _ -> () | _ -> failwith "should detect cycle"
    ]

// ─── foldEvent ────────────────────────────────────────────────────
[<Tests>]
let foldEventTests =
    testList "foldEvent" [
        let makeTaskWithStatus id status =
            { makeTask id [] with status = status }

        testCase "TaskCreated adds task" <| fun _ ->
            let dag = { sessionId = ""; tasks = Map.empty; rootRequirement = "" }
            let evt = { squadEvent = TaskCreated; sessionId = "s1"; taskId = Some (TaskId "a"); title = Some "T"; description = Some "D"; dependsOn = []; masterSha = None; worktreePath = None; branchName = None; slavePid = None; merged = None; requirement = None }
            let dag' = foldEvent dag evt (fun () -> "2020-01-01T00:00:00Z")
            Expect.isTrue (dag'.tasks.ContainsKey(TaskId "a")) "task a exists"

        testCase "TaskStarted sets Running" <| fun _ ->
            let dag = { sessionId = ""; tasks = Map.ofList [(TaskId "a", makeTaskWithStatus (TaskId "a") Pending)]; rootRequirement = "" }
            let evt = { squadEvent = TaskStarted; sessionId = "s1"; taskId = Some (TaskId "a"); title = None; description = None; dependsOn = []; masterSha = None; worktreePath = Some "/wt"; branchName = Some "squad-a"; slavePid = Some 123; merged = None; requirement = None }
            let dag' = foldEvent dag evt (fun () -> "2020-01-01T00:00:00Z")
            Expect.equal dag'.tasks.[TaskId "a"].status Running "should be Running"

        testCase "TaskMerged sets Merged" <| fun _ ->
            let dag = { sessionId = ""; tasks = Map.ofList [(TaskId "a", makeTaskWithStatus (TaskId "a") Submitted)]; rootRequirement = "" }
            let evt = { squadEvent = TaskMerged; sessionId = "s1"; taskId = Some (TaskId "a"); title = None; description = None; dependsOn = []; masterSha = Some "deadbeef"; worktreePath = None; branchName = None; slavePid = None; merged = None; requirement = None }
            let dag' = foldEvent dag evt (fun () -> "2020-01-01T00:00:00Z")
            match dag'.tasks.[TaskId "a"].status with Merged sha -> Expect.equal sha "deadbeef" "sha match" | _ -> failwith "Merged"

        testCase "TaskDone idempotent when Merged" <| fun _ ->
            let dag = { sessionId = ""; tasks = Map.ofList [(TaskId "a", makeTaskWithStatus (TaskId "a") (Merged "abc"))]; rootRequirement = "" }
            let evt = { squadEvent = TaskDone; sessionId = "s1"; taskId = Some (TaskId "a"); title = None; description = None; dependsOn = []; masterSha = None; worktreePath = None; branchName = None; slavePid = None; merged = Some false; requirement = None }
            let dag' = foldEvent dag evt (fun () -> "2020-01-01T00:00:00Z")
            match dag'.tasks.[TaskId "a"].status with Merged _ -> () | _ -> failwith "should remain Merged"

        testCase "SquadCancelled sets non-terminal to Cancelled" <| fun _ ->
            let a = makeTaskWithStatus (TaskId "a") Pending
            let b = makeTaskWithStatus (TaskId "b") Submitted
            let dag = { sessionId = ""; tasks = Map.ofList [(TaskId "a", a); (TaskId "b", b)]; rootRequirement = "" }
            let evt = { squadEvent = SquadCancelled; sessionId = "s1"; taskId = None; title = None; description = None; dependsOn = []; masterSha = None; worktreePath = None; branchName = None; slavePid = None; merged = None; requirement = None }
            let dag' = foldEvent dag evt (fun () -> "2020-01-01T00:00:00Z")
            Expect.equal dag'.tasks.[TaskId "a"].status Cancelled "a Cancelled"
            Expect.equal dag'.tasks.[TaskId "b"].status Cancelled "b Cancelled"
    ]

