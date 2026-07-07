module Wanxiangzhen.Tests.ArchitectureTests

open Wanxiangzhen.Tests.Assert
open Wanxiangzhen.Tests.ArchitectureTestsSupport

let private forbiddenPatterns =
    [ "open Node.Api"
      "open Node"
      "Node.Fs"
      "promise {"
      "Async."
      "System.DateTime"
      "System.Random"
      "open Shell"
    ]

let architectureTests () : (string * (unit -> unit)) list =
    let kernelDir = "src/Kernel"
    chk ("kernel dir exists: " + kernelDir) (existsSync kernelDir)
    if not (existsSync kernelDir) then []
    else
        let files = collectFsFiles kernelDir
        chk "kernel has .fs files" (files.Length > 0)
        files |> List.collect (fun file ->
            let content = requireFile file
            forbiddenPatterns |> List.map (fun pat ->
                (sprintf "no '%s' in %s" pat file),
                fun () ->
                    chk (sprintf "%s must not contain '%s'" file pat)
                        (not (content.Contains(pat)))
            )
        )

let entries () : (string * (unit -> unit)) list =
    [ ("Kernel architecture purity", fun () ->
        let tests = architectureTests ()
        for (label, body) in tests do
            setCurrentLabel label
            try body ()
            with ex -> recordException (sprintf "EXCEPTION in %s: %s" label (string ex))
    ) ]
