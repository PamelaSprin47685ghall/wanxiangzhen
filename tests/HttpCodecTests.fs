module Wanxiangzhen.Tests.HttpCodecTests

open Fable.Core
open Fable.Core.JsInterop
open Wanxiangzhen.Kernel.FfDecision
open Wanxiangzhen.Shell.Dyn
open Wanxiangzhen.Shell.HttpCodec
open Wanxiangzhen.Tests.Assert

let private taskA : Wanxiangzhen.Kernel.Task.Task =
    { Id = "squad-a1b2"
      Title = "Task A"
      Description = "Desc A"
      DependsOn = [ "squad-x9y8" ]
      Status = Wanxiangzhen.Kernel.Task.Running
      WorktreePath = Some "/wt/a"
      BranchName = Some "squad-a1b2"
      SlavePid = Some 12345
      LastHeartbeatAt = Some "2024-01-01T00:00:00Z"
      MergedSha = None
      CreatedAt = "2024-01-01T00:00:00Z"
      UpdatedAt = "2024-01-01T00:00:00Z" }

let entries () : (string * (unit -> unit)) list = [

    ("HttpCodec.encodeTaskDetail: all fields", fun () ->
        let o = encodeTaskDetail taskA
        equal "squad-a1b2" (str o "id")
        equal "Task A" (str o "title")
        equal "Desc A" (str o "description")
        let deps = unbox<string[]> (get o "dependsOn")
        equal 1 deps.Length
        equal "squad-x9y8" deps.[0]
        equal "running" (str o "status"))

    ("HttpCodec.encodeTaskDetail: empty dependsOn", fun () ->
        let t = { taskA with DependsOn = [] }
        let o = encodeTaskDetail t
        let deps = unbox<string[]> (get o "dependsOn")
        equal 0 deps.Length)

    ("HttpCodec.encodeStateSnapshot: empty dag", fun () ->
        let dag = Wanxiangzhen.Kernel.Dag.empty "s1" ""
        let o = encodeStateSnapshot dag
        let sessions = unbox<obj[]> (get o "sessions")
        equal 1 sessions.Length
        let tasks = unbox<obj[]> (get sessions.[0] "tasks")
        equal 0 tasks.Length)

    ("HttpCodec.encodeStateSnapshot: one task", fun () ->
        let dag = Wanxiangzhen.Kernel.Dag.empty "s1" "req"
                     |> Wanxiangzhen.Kernel.Dag.addTask taskA
        let o = encodeStateSnapshot dag
        let sessions = unbox<obj[]> (get o "sessions")
        equal 1 sessions.Length
        let tasks = unbox<obj[]> (get sessions.[0] "tasks")
        equal 1 tasks.Length
        equal "squad-a1b2" (str tasks.[0] "id")
        equal "Task A" (str tasks.[0] "title")
        equal "running" (str tasks.[0] "status")
        let deps = unbox<string[]> (get tasks.[0] "dependsOn")
        equal 1 deps.Length
        check (not (isNullish (get tasks.[0] "slavePid"))))

    ("HttpCodec.encodeFfResponseBody: Merged sha", fun () ->
        let o = encodeFfResponseBody (Merged "sha1") |> unbox<obj>
        equal "merged" (str o "result")
        equal "sha1" (str o "masterSha"))

    ("HttpCodec.encodeFfResponseBody: RebaseNeeded sha", fun () ->
        let o = encodeFfResponseBody (RebaseNeeded "sha2") |> unbox<obj>
        equal "rebase_needed" (str o "result")
        equal "sha2" (str o "masterSha"))

    ("HttpCodec.encodeFfResponseBody: StaleCommit no extra", fun () ->
        let o = encodeFfResponseBody StaleCommit |> unbox<obj>
        equal "stale_commit" (str o "result")
        check (isNullish (get o "masterSha")))

    ("HttpCodec.encodeFfResponseBody: CoordinatorNotReady reason", fun () ->
        let o = encodeFfResponseBody (CoordinatorNotReady "dirty") |> unbox<obj>
        equal "coordinator_not_ready" (str o "result")
        equal "dirty" (str o "reason"))

    ("HttpCodec.encodeFfResponseBody: NotSubmittable status", fun () ->
        let o = encodeFfResponseBody (NotSubmittable "merged") |> unbox<obj>
        equal "not_submittable" (str o "result")
        equal "merged" (str o "currentStatus"))

    ("HttpCodec.encodeResult: label", fun () ->
        let o = encodeResult "ok" |> unbox<obj>
        equal "ok" (str o "result"))

    ("HttpCodec.decodeFfResult: Merged", fun () ->
        let body = createObj [ "result" ==> "merged"; "masterSha" ==> "s1" ]
        match decodeFfResult body with
        | Some (Merged sha) -> equal "s1" sha
        | _ -> check false)

    ("HttpCodec.decodeFfResult: RebaseNeeded", fun () ->
        let body = createObj [ "result" ==> "rebase_needed"; "masterSha" ==> "s2" ]
        match decodeFfResult body with
        | Some (RebaseNeeded sha) -> equal "s2" sha
        | _ -> check false)

    ("HttpCodec.decodeFfResult: StaleCommit", fun () ->
        let body = createObj [ "result" ==> "stale_commit" ]
        isSome (decodeFfResult body))

    ("HttpCodec.decodeFfResult: CoordinatorNotReady", fun () ->
        let body = createObj [ "result" ==> "coordinator_not_ready"; "reason" ==> "dirty" ]
        match decodeFfResult body with
        | Some (CoordinatorNotReady r) -> equal "dirty" r
        | _ -> check false)

    ("HttpCodec.decodeFfResult: NotSubmittable", fun () ->
        let body = createObj [ "result" ==> "not_submittable"; "currentStatus" ==> "done" ]
        match decodeFfResult body with
        | Some (NotSubmittable s) -> equal "done" s
        | _ -> check false)

    ("HttpCodec.decodeFfResult: unknown returns None", fun () ->
        let body = createObj [ "result" ==> "bogus" ]
        isNone (decodeFfResult body))

    ("HttpCodec.decodeSubmitBody: with commitSha", fun () ->
        let body = createObj [ "commitSha" ==> "abc123" ]
        match decodeSubmitBody body with
        | Some sha -> equal "abc123" sha
        | _ -> check false)

    ("HttpCodec.decodeSubmitBody: without commitSha", fun () ->
        let body = createObj []
        isNone (decodeSubmitBody body))

    ("HttpCodec.decodeRegisterBody: int pid", fun () ->
        let body = createObj [ "pid" ==> 9999 ]
        match decodeRegisterBody body with
        | Some pid -> equal 9999 pid
        | _ -> check false)

    ("HttpCodec.decodeRegisterBody: without pid", fun () ->
        let body = createObj []
        isNone (decodeRegisterBody body))

    ("HttpCodec.decodeLogBody: with message", fun () ->
        let body = createObj [ "message" ==> "hello" ]
        match decodeLogBody body with
        | Some msg -> equal "hello" msg
        | _ -> check false)

    ("HttpCodec.decodeLogBody: without message", fun () ->
        let body = createObj []
        isNone (decodeLogBody body))
]
