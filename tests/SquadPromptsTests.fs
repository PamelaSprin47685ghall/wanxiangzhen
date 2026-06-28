module Wanxiangzhen.Tests.SquadPromptsTests

open Wanxiangzhen.Tests.Assert
open Wanxiangzhen.Kernel.SquadPrompts

let private taskId = "squad-a1b2"
let private title = "test-title"
let private description = "test-description-body"
let private masterBranch = "main"

let private prompt () : string =
    buildSlavePrompt taskId title description masterBranch

let entries () : (string * (unit -> unit)) list = [
    ("buildSlavePrompt output starts with ---\\ntask: frontmatter", fun () ->
        let p = prompt ()
        check (p.StartsWith "---\ntask: "))

    ("buildSlavePrompt contains taskId and title", fun () ->
        let p = prompt ()
        check (p.Contains (sprintf "task %s" taskId))
        check (p.Contains title))

    ("buildSlavePrompt contains submit_to_squad", fun () ->
        let p = prompt ()
        check (p.Contains "submit_to_squad"))

    ("buildSlavePrompt contains git rebase + masterBranch", fun () ->
        let p = prompt ()
        check (p.Contains (sprintf "git rebase %s" masterBranch)))

    ("buildSlavePrompt contains /loop or With-Review", fun () ->
        let p = prompt ()
        check (p.Contains "/loop" || p.Contains "With-Review"))
]
