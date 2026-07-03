module Wanxiangzhen.Tests.SquadUpdateIdAssignTests

open Wanxiangzhen.Kernel.SquadUpdateIdAssign
open Wanxiangzhen.Tests.Assert

let entries () : (string * (unit -> unit)) list = [
    ("assignTaskIds.IdExhausted when all collide", fun () ->
        let mutable n = 0
        let gen = {
            Generate = fun () -> n <- n + 1; "squad-dead"
            RefExists = fun _ -> true
        }
        match assignTaskIds Set.empty [ (None, "t", "d", []) ] gen with
        | Error () -> ()
        | Ok _ -> check "" false)

    ("assignTaskIds assigns when ref free", fun () ->
        let gen =
            { Generate = (fun () -> "squad-abcd")
              RefExists = (fun _ -> false) }
        match assignTaskIds Set.empty [ (None, "t", "d", []) ] gen with
        | Ok [ (id, _, _, _) ] -> equal "squad-abcd" id
        | _ -> check "" false)
]