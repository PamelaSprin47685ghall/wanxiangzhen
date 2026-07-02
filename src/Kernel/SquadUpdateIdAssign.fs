module Wanxiangzhen.Kernel.SquadUpdateIdAssign

type IdGen = {
    Generate: unit -> string
    RefExists: string -> bool
}

let assignTaskIds (existingIds: Set<string>) (raw: (string option * string * string * string list) list) (gen: IdGen) : Result<(string * string * string * string list) list, unit> =
    let rec genUnique (used: Set<string>) (remaining: int) : string option =
        if remaining <= 0 then None
        else
            let cand = gen.Generate ()
            if Set.contains cand existingIds || Set.contains cand used || gen.RefExists cand then
                genUnique used (remaining - 1)
            else Some cand
    let rec go (used: Set<string>) (tasks: (string option * string * string * string list) list) : Result<(string * string * string * string list) list, unit> =
        match tasks with
        | [] -> Ok []
        | (idOpt, title, desc, deps) :: rest ->
            match idOpt with
            | Some id -> go (Set.add id used) rest |> Result.map (fun tail -> (id, title, desc, deps) :: tail)
            | None ->
                match genUnique used 10 with
                | Some tid -> go (Set.add tid used) rest |> Result.map (fun tail -> (tid, title, desc, deps) :: tail)
                | None -> Error ()
    go Set.empty raw