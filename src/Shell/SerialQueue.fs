module Wanxiangzhen.Shell.SerialQueue

open Fable.Core

/// Single-threaded, lock-free async serial queue.
/// Tasks run in order they are enqueued; the tail swallows predecessor
/// exceptions so the queue never jams.
type SerialQueue() =
    let mutable tail : JS.Promise<unit> = Promise.lift ()

    member _.Enqueue(work: unit -> JS.Promise<'T>) : JS.Promise<'T> =
        Promise.create (fun resolve reject ->
            let runNext () =
                promise {
                    try
                        let! result = work ()
                        resolve result
                    with ex ->
                        reject ex
                }
            tail <-
                tail
                |> Promise.catch (fun _ -> ())
                |> Promise.bind (fun _ -> runNext () |> Promise.map ignore))

/// Race a promise against a timeout. Returns None when the timeout wins.
let withTimeout (timeoutMs: int) (work: JS.Promise<'T>) : JS.Promise<'T option> =
    let timeoutPromise =
        promise {
            do! Promise.sleep timeoutMs
            return None
        }
    let workPromise =
        promise {
            let! res = work
            return Some res
        }
    Promise.race [ timeoutPromise; workPromise ]
