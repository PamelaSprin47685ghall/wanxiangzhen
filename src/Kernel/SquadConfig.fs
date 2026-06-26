module Wanxiangzhen.Kernel.SquadConfig

type SquadConfig = {
    MaxConcurrent: int
    Terminal: string
    MasterBranch: string option      // None = auto-detect from git
    SharedDirs: string list
}

let defaults = {
    MaxConcurrent = 3
    Terminal = "alacritty"
    MasterBranch = None
    SharedDirs = []
}

let mergeWithDefaults (cfgOpt: SquadConfig option) : SquadConfig =
    match cfgOpt with
    | None -> defaults
    | Some cfg ->
        { MaxConcurrent = if cfg.MaxConcurrent > 0 then cfg.MaxConcurrent else defaults.MaxConcurrent
          Terminal = if System.String.IsNullOrEmpty cfg.Terminal then defaults.Terminal else cfg.Terminal
          MasterBranch = cfg.MasterBranch
          SharedDirs = cfg.SharedDirs }
