module Shell.GitExecutor
open System
open Kernel
open Shell.NodeInterop

type IGitExecutor =
    abstract member ResolveMasterBranch: projectRoot:string -> string
    abstract member IsAncestor: ancestor:string -> descendant:string -> projectRoot:string -> bool
    abstract member IsWorktreeClean: projectRoot:string -> bool
    abstract member FastForward: projectRoot:string -> masterBranch:string -> taskId:TaskId -> branchName:string -> reportedSha:string -> string * string
    abstract member CreateWorktree: taskId:TaskId -> masterBranch:string -> projectRoot:string -> string
    abstract member RemoveWorktree: worktreePath:string -> unit
    abstract member DeleteBranch: branchName:string -> projectRoot:string -> unit
    abstract member GetCurrentBranch: projectRoot:string -> string
    abstract member GetBranchSha: branchName:string -> projectRoot:string -> string

type GitExecutor() =
    // Use NodeInterop.execSync with mkExecOptions for cwd/env
    let execGit (cwd: string) (args: string) : string =
        NodeInterop.execSync ("git " + args) (NodeInterop.mkExecOptions cwd null)

    let ignoreStr (_: string) = ()

    interface IGitExecutor with
        member _.ResolveMasterBranch(projectRoot) = execGit projectRoot "rev-parse --abbrev-ref HEAD"

        member _.IsAncestor ancestor descendant projectRoot =
            try ignoreStr (execGit projectRoot $"merge-base --is-ancestor {ancestor} {descendant}"); true
            with _ -> false

        member _.IsWorktreeClean(projectRoot) =
            let output = execGit projectRoot "status --porcelain"
            String.IsNullOrWhiteSpace output

        member _.FastForward (projectRoot:string) (masterBranch:string) (taskId:TaskId) (branchName:string) (reportedSha:string) =
            let headSha = execGit projectRoot $"rev-parse {branchName}"
            if headSha <> reportedSha then ("stale_commit", headSha)
            else
                let curBranch = execGit projectRoot "rev-parse --abbrev-ref HEAD"
                if curBranch <> masterBranch then ("coordinator_not_ready:not_on_master", curBranch)
                else
                    let porcelain = execGit projectRoot "status --porcelain"
                    if not (String.IsNullOrWhiteSpace porcelain) then ("coordinator_not_ready:dirty", curBranch)
                    else
                        try
                            ignoreStr (execGit projectRoot $"merge-base --is-ancestor {masterBranch} {branchName}")
                            ignoreStr (execGit projectRoot $"merge --ff-only {branchName}")
                            let newSha = execGit projectRoot "rev-parse HEAD"
                            ("merged", newSha)
                        with _ ->
                            let newSha = execGit projectRoot $"rev-parse {masterBranch}"
                            ("rebase_needed", newSha)

        member _.CreateWorktree (taskId:TaskId) (masterBranch:string) (projectRoot:string) =
            let wtName = sprintf "worktree-%s" (match taskId with Kernel.TaskId s -> s)
            // Use NodeInterop.pathJoin instead of Path.Combine
            let wtPath = NodeInterop.pathJoin [| projectRoot; ".."; wtName |]
            execGit projectRoot $"worktree add -b {match taskId with Kernel.TaskId s -> s} \"{wtPath}\" {masterBranch}" |> ignoreStr
            wtPath

        member _.RemoveWorktree(worktreePath:string) =
            try ignoreStr (execGit worktreePath "rev-parse --git-dir") with _ -> ()
            try ignoreStr (execGit "" $"worktree remove --force \"{worktreePath}\"") with _ -> ()

        member _.DeleteBranch (branchName:string) (projectRoot:string) =
            try ignoreStr (execGit projectRoot $"branch -D {branchName}") with _ -> ()

        member _.GetCurrentBranch(projectRoot) = execGit projectRoot "rev-parse --abbrev-ref HEAD"

        member _.GetBranchSha (branchName:string) (projectRoot:string) = execGit projectRoot $"rev-parse {branchName}"
