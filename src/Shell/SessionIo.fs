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

let clientId (hookInput: obj) : string = str hookInput "sessionID"
