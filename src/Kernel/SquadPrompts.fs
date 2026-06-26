module Wanxiangzhen.Kernel.SquadPrompts

let buildSlavePrompt (taskId: string) (title: string) (description: string)
                     (masterBranch: string) (vibeFsDetected: bool) : string =
     if vibeFsDetected then
         sprintf
             "---\ntask: %s\n---\n\n\
              You are executing squad task %s: %s\n\
              Task description:\n%s\n\n\
              Complete the task following the review workflow.\n\
              After development, call submit_review for review.\n\
              After review PASS, git commit, then call submit_to_squad.\n\
              If review REJECT, fix per feedback and re-review until PASS."
             title taskId title description
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

