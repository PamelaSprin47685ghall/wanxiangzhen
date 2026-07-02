module Wanxiangzhen.Shell.SquadEventLogRuntime

open Fable.Core
open Wanxiangzhen.Kernel.SquadEvent
open Wanxiangzhen.Shell.SquadEventLogFiles

let mutable private stores: Map<string, SquadEventLogStore> = Map.empty

let getStore (workspaceRoot: string) : SquadEventLogStore =
    match Map.tryFind workspaceRoot stores with
    | Some s -> s
    | None ->
        let s = SquadEventLogStore workspaceRoot
        stores <- Map.add workspaceRoot s stores
        s

let readAllSquadEvents (workspaceRoot: string) : JS.Promise<SquadEvent list> =
    getStore(workspaceRoot).ReadAllEvents()

let appendSquadEvent (workspaceRoot: string) (at: string) (e: SquadEvent) : JS.Promise<Result<unit, string>> =
    getStore(workspaceRoot).AppendEvent at e