module Tests.CoordinatorState

open Expecto
open Kernel

// ─── Helpers ──────────────────────────────────────────────────────

let makeTask id status deps =
    { id = TaskId id
      title = ""
      description = ""
      dependsOn = deps
      status = status
      worktreePath = None
      branchName = Some id
      slavePid = None
      lastHeartbeatAt = None
      mergedSha = None
      createdAt = "2025-01-01T00:00:00Z"
      updatedAt = "2025-01-01T00:00:00Z" }

let emptyDag = { sessionId = ""; tasks = Map.empty; rootRequirement = "" }

let applyEvt (evt: EventPayload) (dag: Dag) : Dag =
    foldAll [evt] dag (fun () -> "2025-01-01T00:00:00Z")

// ─── GitReconcile Cancelled skip ──────────────────────────────────

[<Tests>]
let gitReconcileTests =
    testList "GitReconcile" [
        testCase "Cancelled task is never upgraded by git, Running sibling is upgraded to Merged" <| fun _ ->
            let cancelled = makeTask "squad-a1b2" Cancelled []
            let running   = makeTask "squad-c3d4" Running [TaskId "squad-a1b2"]
            let dag = { emptyDag with tasks = Map.ofList [TaskId "squad-a1b2", cancelled; TaskId "squad-c3d4", running] }

            // isAncestor: only "squad-c3d4" is ancestor → sha = "abc123"
            let isAncestor taskId =
                match taskId with
                | TaskId s when s = "squad-c3d4" -> Some "abc123"
                | _ -> None

            let result = Kernel.gitReconcileDag dag isAncestor

            // Cancelled must stay Cancelled, mergedSha stays None
            let ct = result.tasks.[TaskId "squad-a1b2"]
            Expect.equal ct.status Cancelled "cancelled task must not be upgraded"
            Expect.equal ct.mergedSha None "cancelled task mergedSha must stay None"

            // Running sibling must upgrade to Merged with sha
            let rt = result.tasks.[TaskId "squad-c3d4"]
            match rt.status with
            | Merged sha -> Expect.equal sha "abc123" "running task should become Merged"
            | _ -> failwith "running sibling should become Merged"

        testCase "GitReconcile is idempotent: running same dag twice yields same result" <| fun _ ->
            let task = makeTask "squad-e5f6" Running []
            let dag = { emptyDag with tasks = Map.ofList [TaskId "squad-e5f6", task] }

            let isAncestor _ = Some "deadbeef"
            let first  = Kernel.gitReconcileDag dag isAncestor
            let second = Kernel.gitReconcileDag first isAncestor   // fold on already-upgraded dag

            // Second pass: already Merged → stays Merged (already in Merged branch → not re-processed)
            let st = second.tasks.[TaskId "squad-e5f6"]
            match st.status with
            | Merged sha -> Expect.equal sha "deadbeef" "idempotent second pass"
            | _ -> failwith "second pass should still be Merged"

        testCase "GitReconcile skips Submitted tasks (not yet Running) when branch IS ancestor → upgrades to Merged" <| fun _ ->
            let submitted = makeTask "squad-x1y2" Submitted []
            let dag = { emptyDag with tasks = Map.ofList [TaskId "squad-x1y2", submitted] }

            let isAncestor _ = Some "cafebabe"
            let result = Kernel.gitReconcileDag dag isAncestor

            let st = result.tasks.[TaskId "squad-x1y2"]
            match st.status with
            | Merged sha -> Expect.equal sha "cafebabe" "Submitted should upgrade to Merged when ancestor"
            | _ -> failwith "Submitted should become Merged"
    ]
