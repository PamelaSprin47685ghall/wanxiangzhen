module Wanxiangzhen.Tests.SquadConfigTests

open Wanxiangzhen.Kernel.SquadConfig
open Wanxiangzhen.Tests.Assert

let entries () : (string * (unit -> unit)) list = [
    ("defaults.MaxConcurrent=3", fun () ->
        equal 3 defaults.MaxConcurrent)

    ("defaults.Terminal=alacritty", fun () ->
        equal "alacritty" defaults.Terminal)

    ("defaults.MasterBranch=None", fun () ->
        isNone defaults.MasterBranch)

    ("defaults.SharedDirs=[]", fun () ->
        check defaults.SharedDirs.IsEmpty)

    ("mergeWithDefaults None → defaults", fun () ->
        let cfg = mergeWithDefaults None
        equal 3 cfg.MaxConcurrent
        equal "alacritty" cfg.Terminal
        isNone cfg.MasterBranch
        check cfg.SharedDirs.IsEmpty)

    ("mergeWithDefaults empty fields → defaults", fun () ->
        let cfg = mergeWithDefaults (Some { MaxConcurrent = 0; Terminal = ""; MasterBranch = None; SharedDirs = [] })
        equal 3 cfg.MaxConcurrent
        equal "alacritty" cfg.Terminal
        isNone cfg.MasterBranch
        check cfg.SharedDirs.IsEmpty)

    ("mergeWithDefaults all custom values preserved", fun () ->
        let cfg = mergeWithDefaults (Some { MaxConcurrent = 5; Terminal = "kitty"; MasterBranch = Some "main"; SharedDirs = ["node_modules"] })
        equal 5 cfg.MaxConcurrent
        equal "kitty" cfg.Terminal
        equal (Some "main") cfg.MasterBranch
        equal ["node_modules"] cfg.SharedDirs)

    ("mergeWithDefaults MaxConcurrent<=0 falls back to default", fun () ->
        let cfg = mergeWithDefaults (Some { MaxConcurrent = -1; Terminal = ""; MasterBranch = None; SharedDirs = [] })
        equal 3 cfg.MaxConcurrent
        equal "alacritty" cfg.Terminal
        isNone cfg.MasterBranch
        check cfg.SharedDirs.IsEmpty)
]
