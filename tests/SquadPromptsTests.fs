module Wanxiangzhen.Tests.SquadPromptsTests

open Wanxiangzhen.Tests.Assert
open Wanxiangzhen.Kernel.SquadPrompts

let private taskId = "squad-a1b2"
let private title = "test-title"
let private description = "test-description-body"
let private masterBranch = "main"

let private vibeFsPrompt () : string =
    buildSlavePrompt taskId title description masterBranch true

let private noVibeFsPrompt () : string =
    buildSlavePrompt taskId title description masterBranch false

let entries () : (string * (unit -> unit)) list = [
    ("buildSlavePrompt vibeFs=true contains task: frontmatter", fun () ->
        let p = vibeFsPrompt ()
        check (p.StartsWith "---\ntask: "))

    ("buildSlavePrompt vibeFs=true contains taskId and title", fun () ->
        let p = vibeFsPrompt ()
        check (p.Contains (sprintf "task %s" taskId))
        check (p.Contains title))

    ("buildSlavePrompt vibeFs=false contains submit_to_squad", fun () ->
        let p = noVibeFsPrompt ()
        check (p.Contains "submit_to_squad"))

    ("buildSlavePrompt vibeFs=false contains git rebase + masterBranch", fun () ->
        let p = noVibeFsPrompt ()
        check (p.Contains (sprintf "git rebase %s" masterBranch)))
]
