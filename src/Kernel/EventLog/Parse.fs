module Wanxiangzhen.Kernel.EventLog.Parse

let parseLinesWithTruncate (tryParse: string -> 'a option) (lines: string list) : 'a list =
    let rec go acc (rest: string list) =
        match rest with
        | [] -> List.rev acc
        | line :: tail ->
            let t = if isNull line then "" else line.Trim()
            if t = "" then go acc tail
            else
                match tryParse t with
                | Some v -> go (v :: acc) tail
                | None -> List.rev acc
    go [] lines