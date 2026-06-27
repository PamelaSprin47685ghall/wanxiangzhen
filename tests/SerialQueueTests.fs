module Wanxiangzhen.Tests.SerialQueueTests

open Fable.Core
open Fable.Core.JsInterop
open Wanxiangzhen.Shell.SerialQueue
open Wanxiangzhen.Tests.Assert

let private runAsync (p: JS.Promise<unit>) : unit =
    p |> Promise.map ignore |> ignore

let entries () : (string * (unit -> unit)) list = [

    ("SerialQueue.enqueue resolves with result", fun () ->
        Promise.start <| promise {
            let q = SerialQueue()
            let! r = q.Enqueue(fun () -> promise { return 42 })
            equal 42 r
        })

    ("SerialQueue.preserves order", fun () ->
        Promise.start <| promise {
            let q = SerialQueue()
            let! r1 = q.Enqueue(fun () -> promise { return 1 })
            let! r2 = q.Enqueue(fun () -> promise { return 2 })
            let! r3 = q.Enqueue(fun () -> promise { return 3 })
            equal [1; 2; 3] [r1; r2; r3]
        })

    ("SerialQueue.continues after rejection", fun () ->
        Promise.start <| promise {
            let q = SerialQueue()
            let! r1 =
                q.Enqueue(fun () ->
                    promise {
                        do! Promise.sleep 10
                        return failwith "intentional" })
                |> Promise.catch (fun _ -> None)
            let! r2 = q.Enqueue(fun () -> promise { return "ok" })
            isNone r1
            equal "ok" r2
        })

    ("SerialQueue.withTimeout resolves before timeout", fun () ->
        Promise.start <| promise {
            let! r =
                withTimeout 5000 (promise {
                    do! Promise.sleep 10
                    return "done" })
            isSome r
            equal "done" r.Value
        })

    ("SerialQueue.withTimeout returns None on timeout", fun () ->
        Promise.start <| promise {
            let work =
                promise {
                    do! Promise.sleep 2000
                    return "too late" }
            let! r = withTimeout 50 work
            isNone r
        })
]
