module Wanxiangzhen.Kernel.SquadPrompts

let buildSlavePrompt (taskId: string) (title: string) (description: string)
                     (masterBranch: string) : string =
     sprintf
          "---\ntask: %s\n---\n\n\
           You are executing squad task %s: %s\n\
           Task description:\n%s\n\n\
           Complete the task following the review workflow.\n\
           Activate With-Review Mode by calling /loop <task description>.\n\
           After development, call submit_review for review.\n\
           After review PASS, git commit, then call submit_to_squad.\n\
           If review REJECT, fix per feedback and re-review until PASS.\n\
           If asked to rebase, run: git rebase %s, then resubmit."
         title taskId title description masterBranch


