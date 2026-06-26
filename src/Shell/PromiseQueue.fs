module Shell.PromiseQueue

open Fable.Core

/// Single-threaded, lock-free async serial queue.
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

type PromiseQueue = SerialQueue
