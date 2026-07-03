module Wanxiangzhen.Tests.ExtendedMockE2eSlaveHttpTests

open Fable.Core
open Fable.Core.JsInterop
open Wanxiangzhen.Kernel.Task
open Wanxiangzhen.Kernel.Dag
open Wanxiangzhen.Shell.Dyn
open Wanxiangzhen.Shell.CoordinatorRuntime
open Wanxiangzhen.Shell.CoordinatorOps
open Wanxiangzhen.Shell.CoordinatorLifecycle
open Wanxiangzhen.Shell.HttpCodec
open Wanxiangzhen.Shell.HttpServer
open Wanxiangzhen.Tests.Assert
open Wanxiangzhen.Tests.TestDoubles
open Wanxiangzhen.Tests.ExtendedMockE2eHelpers

let testSlaveSubmitMerged () : JS.Promise<unit> =
    promise {
        let! s, rt, server = mkRunningTaskServer ()
        try
            s.mergeBaseTrueForFirstN <- 1
            s.revParseRefOverrides <- s.revParseRefOverrides.Add("squad-a1b2", "deadbeef")
            let! r = fetchJson (server.Url + "/task/squad-a1b2/submit") (createObj [
                "method", box "POST"
                "headers", box {| Authorization = box ("Bearer " + rt.Token) |}
                "body", box (JSON?stringify (createObj [ "commitSha", box "deadbeef" ])) ])
            check (r.status = 200)
            check (str r.body "result" = "merged")
        finally
            server.Close ()
    }

let testSlaveSubmitRebaseNeeded () : JS.Promise<unit> =
    promise {
        let! s, rt, server = mkRunningTaskServer ()
        try
            s.mergeBaseTrueForFirstN <- 0
            s.revParseRefOverrides <- s.revParseRefOverrides.Add("squad-a1b2", "deadbeef")
            let! r = fetchJson (server.Url + "/task/squad-a1b2/submit") (createObj [
                "method", box "POST"
                "headers", box {| Authorization = box ("Bearer " + rt.Token) |}
                "body", box (JSON?stringify (createObj [ "commitSha", box "deadbeef" ])) ])
            check (r.status = 200)
            check (str r.body "result" = "rebase_needed")
        finally
            server.Close ()
    }

let testSlaveSubmitStaleCommit () : JS.Promise<unit> =
    promise {
        let! s, rt, server = mkRunningTaskServer ()
        try
            s.revParseRefOverride <- Some (fun _ _ -> "actual-sha")
            let! r = fetchJson (server.Url + "/task/squad-a1b2/submit") (createObj [
                "method", box "POST"
                "headers", box {| Authorization = box ("Bearer " + rt.Token) |}
                "body", box (JSON?stringify (createObj [ "commitSha", box "deadbeef" ])) ])
            check (r.status = 200)
            check (str r.body "result" = "stale_commit")
        finally
            server.Close ()
    }

let testSlaveSubmitCoordinatorNotReady () : JS.Promise<unit> =
    promise {
        let! s, rt, server = mkRunningTaskServer ()
        try
            s.revParseRefOverride <- Some (fun _ r ->
                if r = "squad-a1b2" then "deadbeef"
                elif r = rt.MasterBranch then "merged-sha"
                else "actual-sha")
            s.statusIsCleanOverride <- Some (fun _ -> false)
            let! r = fetchJson (server.Url + "/task/squad-a1b2/submit") (createObj [
                "method", box "POST"
                "headers", box {| Authorization = box ("Bearer " + rt.Token) |}
                "body", box (JSON?stringify (createObj [ "commitSha", box "deadbeef" ])) ])
            check (r.status = 200)
            check (str r.body "result" = "coordinator_not_ready")
        finally
            server.Close ()
    }

let testSlaveSubmitNotSubmittable () : JS.Promise<unit> =
    promise {
        let! s, rt, server = mkRunningTaskServer ()
        try
            rt.Scheduling <- true
            rt.Dag <- rt.Dag |> updateTask "squad-a1b2" (fun t -> { t with Status = Merged })
            rt.Scheduling <- false
            let! r = fetchJson (server.Url + "/task/squad-a1b2/submit") (createObj [
                "method", box "POST"
                "headers", box {| Authorization = box ("Bearer " + rt.Token) |}
                "body", box (JSON?stringify (createObj [ "commitSha", box "deadbeef" ])) ])
            check (r.status = 200)
            check (str r.body "result" = "not_submittable")
        finally
            server.Close ()
    }

let testSlaveSubmitTaskNotFound () : JS.Promise<unit> =
    promise {
        let! _, rt, server = mkRunningTaskServer ()
        try
            let! r = fetchJson (server.Url + "/task/does-not-exist/submit") (createObj [
                "method", box "POST"
                "headers", box {| Authorization = box ("Bearer " + rt.Token) |}
                "body", box (JSON?stringify (createObj [ "commitSha", box "deadbeef" ])) ])
            check (r.status = 404)
            check (str r.body "result" = "task_not_found")
        finally
            server.Close ()
    }

let testSlaveSubmitUnauthorized () : JS.Promise<unit> =
    promise {
        let! _, rt, server = mkRunningTaskServer ()
        try
            let! r = fetchJson (server.Url + "/task/squad-a1b2/submit") (createObj [
                "method", box "POST"
                "headers", box {| Authorization = box "Bearer wrong-token" |}
                "body", box (JSON?stringify (createObj [ "commitSha", box "deadbeef" ])) ])
            check (r.status = 401)
            check (str r.body "result" = "unauthorized")
        finally
            server.Close ()
    }

let testSlaveQuerySquadTaskDetail () : JS.Promise<unit> =
    promise {
        let s    = mkFake ()
        let deps = mkDeps s
        let rt   = mkRuntime deps
        rt.MasterSessionId <- "squad-session-001"

        let evts  = [| mkTaskEvent "squad-a1b2" "Task A" "desc A" [] |]
        let args  = mkSquadUpdateArgs evts
        rt.Scheduling <- true
        let! _    = handleSquadUpdate rt args
        rt.Scheduling <- false
        do! schedulerTick rt

        let! server = startServer rt.Token (routeHandler rt)

        try
            let! resp = fetchJson (server.Url + "/task/squad-a1b2") (createObj [
                "method", box "GET"
                "headers", box {| Authorization = box ("Bearer " + rt.Token) |} ])
            check (resp.status = 200)
            check ((str resp.body "id") = "squad-a1b2")
            check ((str resp.body "title") = "Task A")
            check ((str resp.body "status") = "running")
        finally
            server.Close ()
    }

let entriesAsync () : (string * (unit -> JS.Promise<unit>)) list = [
    ("ExtendedMockE2e.slave_submit_merged", testSlaveSubmitMerged)
    ("ExtendedMockE2e.slave_submit_rebase_needed", testSlaveSubmitRebaseNeeded)
    ("ExtendedMockE2e.slave_submit_stale_commit", testSlaveSubmitStaleCommit)
    ("ExtendedMockE2e.slave_submit_coordinator_not_ready", testSlaveSubmitCoordinatorNotReady)
    ("ExtendedMockE2e.slave_submit_not_submittable", testSlaveSubmitNotSubmittable)
    ("ExtendedMockE2e.slave_submit_task_not_found", testSlaveSubmitTaskNotFound)
    ("ExtendedMockE2e.slave_submit_unauthorized", testSlaveSubmitUnauthorized)
    ("ExtendedMockE2e.slave_query_squad_task_detail",
     testSlaveQuerySquadTaskDetail)
]
