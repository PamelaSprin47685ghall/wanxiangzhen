module Wanxiangzhen.Shell.HttpCodec

open Fable.Core
open Wanxiangzhen.Kernel.Task
open Wanxiangzhen.Kernel.Dag
open Wanxiangzhen.Kernel.FfDecision
open Wanxiangzhen.Shell.Dyn

[<Global>]
let private JSON : obj = jsNative

let encodeTaskDetail (task: Task) : obj =
    box {| id = task.Id
           title = task.Title
           description = task.Description
           dependsOn = List.toArray task.DependsOn
           status = statusToString task.Status |}

let encodeStateSnapshot (dag: Dag) : obj =
    let tasks =
        dag.Tasks |> Map.toList |> List.map (fun (_, t) ->
            box {| id = t.Id; title = t.Title; status = statusToString t.Status
                   dependsOn = List.toArray t.DependsOn; slavePid = t.SlavePid |})
    box {| sessions = [| box {| sessionId = dag.SessionId; tasks = List.toArray tasks |} |] |}

let encodeFfResponseBody (r: FfResult) : obj =
    match r with
    | Merged sha -> box {| result = "merged"; masterSha = sha |}
    | RebaseNeeded sha -> box {| result = "rebase_needed"; masterSha = sha |}
    | StaleCommit -> box {| result = "stale_commit" |}
    | CoordinatorNotReady reason -> box {| result = "coordinator_not_ready"; reason = reason |}
    | NotSubmittable status -> box {| result = "not_submittable"; currentStatus = status |}

let encodeResult (label: string) : obj = box {| result = label |}

let decodeFfResult (body: obj) : FfResult option =
    let result = str body "result"
    match result with
    | "merged" -> Some (Merged (str body "masterSha"))
    | "rebase_needed" -> Some (RebaseNeeded (str body "masterSha"))
    | "stale_commit" -> Some StaleCommit
    | "coordinator_not_ready" -> Some (CoordinatorNotReady (str body "reason"))
    | "not_submittable" -> Some (NotSubmittable (str body "currentStatus"))
    | _ -> None

let decodeSubmitBody (body: obj) : string option =
    let v = get body "commitSha"
    if isNullish v then None else Some (string v)

let decodeRegisterBody (body: obj) : int option =
    let v = get body "pid"
    if isNullish v then None else Some (unbox<int> v)
