module Wanxiangzhen.Shell.SessionIo

open Fable.Core
open Fable.Core.JsInterop
open Wanxiangzhen.Shell.Dyn

let getSession (client: obj) : Result<obj, string> =
    let session = get client "session"
    if isNullish session then Error "client.session missing"
    else Ok session

let promptSession (client: obj) (sessionId: string) (text: string) : JS.Promise<unit> =
    promise {
        match getSession client with
        | Error _ -> return ()
        | Ok session ->
            let part = createObj [ "type", box "text"; "text", box text ]
            let arg =
                createObj [
                    "path", box (createObj [ "id", box sessionId ])
                    "body", box (createObj [ "parts", box [| part |] ])
                ]
            let! _ = session?("prompt")(arg) |> unbox<JS.Promise<obj>>
            return ()
    }

let readAllTexts (client: obj) (sessionId: string) (directory: string) : JS.Promise<string list> =
    promise {
        try
            match getSession client with
            | Error _ -> return []
            | Ok session ->
                let arg =
                    if directory = "" then
                        box {| path = box {| id = sessionId |} |}
                    else
                        box {| path = box {| id = sessionId |}; query = box {| directory = directory |} |}
                let! result = session?("messages")(arg) |> unbox<JS.Promise<obj>>
                let data = get result "data"
                if isNullish data then return []
                else
                    let msgs = data :?> obj array
                    return
                        msgs |> Array.toList |> List.collect (fun msg ->
                            let parts = get msg "parts"
                            if isNullish parts || not (isArray parts) then []
                            else
                                (parts :?> obj array) |> Array.toList
                                |> List.collect (fun part ->
                                    let ty = str part "type"
                                    if ty = "text" || ty = "tool_result" then [ str part "text" ] else []))
        with _ -> return []
    }

let clientId (hookInput: obj) : string = str hookInput "sessionID"
