module Wanxiangzhen.Shell.CoordinatorReplay

open Fable.Core
open Fable.Core.JsInterop
open Wanxiangzhen.Kernel.Task
open Wanxiangzhen.Kernel.Dag
open Wanxiangzhen.Kernel.SquadEvent
open Wanxiangzhen.Shell.CoordinatorRuntime

let replayFromEventLog (rt: CoordinatorRuntime) : JS.Promise<unit> =
    promise {
        let! events = rt.Deps.ReadAllSquadEvents rt.ProjectRoot
        let mutable currentDag = empty "" ""
        let mutable sessions = Map.empty
        for ev in events do
            match ev with
            | SquadCreated (sid, req) ->
                if currentDag.SessionId <> "" && not currentDag.Tasks.IsEmpty then
                    sessions <- sessions.Add(currentDag.SessionId, currentDag)
                currentDag <- empty sid req
            | _ ->
                currentDag <- foldEvent currentDag ev

        let hasCommits = rt.Deps.HasCommits rt.ProjectRoot

        let reconciledTasks =
            currentDag.Tasks |> Map.map (fun _ t ->
                if t.Status = Submitted || t.Status = Running then
                    match rt.GitError with
                    | Some _ ->
                        if t.Status = Submitted then
                            withReconciledStatus t TaskStatus.Running (rt.Deps.Now ())
                        else t
                    | None ->
                        match t.BranchName with
                        | Some b when hasCommits && rt.Deps.MergeBaseIsAncestor rt.ProjectRoot rt.MasterBranch b ->
                            let sha = rt.Deps.RevParseRef rt.ProjectRoot rt.MasterBranch
                            { (withReconciledStatus t TaskStatus.Merged (rt.Deps.Now ())) with MergedSha = Some sha }
                        | _ ->
                            if t.Status = Submitted then
                                withReconciledStatus t TaskStatus.Running (rt.Deps.Now ())
                            else t
                else t)

        rt.Dag <- { currentDag with Tasks = reconciledTasks }
        rt.Sessions <- sessions

        if rt.MasterSessionId <> "" then
            let orphans =
                rt.Dag.Tasks |> Map.toList |> List.map snd
                |> List.filter (fun t -> t.Status = Running && t.SlavePid.IsNone)
            if orphans <> [] then
                let names = orphans |> List.map (fun t -> t.Id) |> String.concat ", "
                let warning = sprintf "WARNING: Orphan running tasks without PID: %s. Use /squad-kill or ignore." names
                rt.Deps.PromptSession rt.Client rt.MasterSessionId warning |> Promise.start |> ignore
    }