module Wanxiangzhen.Kernel.SquadPrompts

open Wanxiangzhen.Kernel.SquadEvent

let buildSlavePrompt (taskId: string) (title: string) (description: string)
                     (masterBranch: string) (vibeFsDetected: bool) : string =
    if vibeFsDetected then
        sprintf
            "wanxiangshu /loop (With-Review Mode) is available. Follow the review workflow:\n\
             1. Use /loop <task description> to activate With-Review Mode\n\
             2. After development, call submit_review for review\n\
             3. After review PASS, git commit, then call submit_to_squad\n\
             4. If review REJECT, fix per feedback and re-review until PASS\n\n\
             You are executing squad task %s: %s\n\
             Task description:\n%s"
            taskId title description
    else
        sprintf
            "You are executing squad task %s: %s\n\n\
             Task description:\n%s\n\n\
             Complete the above task in the current worktree. After completion:\n\
             1. git add + git commit (on branch %s)\n\
             2. Call submit_to_squad tool to submit to coordinator\n\
             If asked to rebase, run: git rebase %s, then resubmit.\n\
             Use query_squad tool when unsure about global state."
            taskId title description taskId masterBranch

let buildDecompositionPrompt (requirement: string) (sessionId: string) : string =
    sprintf
        "---\nsquad_command: create\nsession_id: %s\nrequirement: |\n  %s\n---\n\n\
         Decompose the above requirement into independently executable tasks. Each task should:\n\
         - Be completable within a single git worktree\n\
         - Have clear completion criteria\n\
         - Minimize file conflicts with other tasks\n\
         Express dependencies via dependsOn (dependency must be merged first).\n\
         Call the squad_update tool to submit all tasks at once (events array)."
        sessionId requirement

let eventProse (eventType: SquadEventType) : string =
    match eventType with
    | SquadCreated -> "Squad session created."
    | TaskCreated -> "Task created. Nothing needs to be done."
    | TaskStarted -> "Task started. Nothing needs to be done."
    | TaskSubmitted -> "Task submitted for ff check. Nothing needs to be done."
    | TaskMerged -> "Task merged into integration branch. Nothing needs to be done."
    | TaskDone -> "Task slave exited."
    | SquadCancelled -> "Squad session cancelled by /squad-kill."

let buildGitConstraintPrompt (masterBranch: string) : string =
    sprintf
        "You are working in a squad worktree.\n\n\
         Allowed git operations:\n\
         - git add / git commit (on your branch)\n\
         - git rebase %s (rebase onto integration branch, local)\n\
         - git log / status / diff (read-only queries)\n\n\
         Forbidden git operations:\n\
         - git push (coordinator handles merge)\n\
         - git merge (coordinator does ff-only)\n\
         - git checkout %s (stay on your task branch)\n\
         - Any modification of the integration branch\n\
         - Any writes to shared directories (symlink targets)"
        masterBranch masterBranch
