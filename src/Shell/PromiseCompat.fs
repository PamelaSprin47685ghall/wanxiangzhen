module Shell.PromiseCompat

open Fable.Core

/// Bridge System.Threading.Tasks.Task → JS.Promise
let fromTask (t: System.Threading.Tasks.Task<'T>) : JS.Promise<'T> =
    Promise.create (fun resolve reject ->
        t.ContinueWith(fun (task: System.Threading.Tasks.Task<'T>) ->
            if task.IsFaulted then reject task.Exception
            elif task.IsCanceled then reject (System.OperationCanceledException())
            else resolve task.Result) |> ignore)

/// Bridge System.Threading.Tasks.Task<unit> → JS.Promise<unit>
let fromTaskUnit (t: System.Threading.Tasks.Task) : JS.Promise<unit> =
    Promise.create (fun resolve reject ->
        t.ContinueWith(fun (task: System.Threading.Tasks.Task) ->
            if task.IsFaulted then reject task.Exception
            elif task.IsCanceled then reject (System.OperationCanceledException())
            else resolve ()) |> ignore)
