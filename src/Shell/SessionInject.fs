module Shell.SessionInject
open Shell.PromiseQueue

/// Thin wrapper exposing the shared inject queue.
/// All event injection is done via injectQueue.Enqueue directly in Plugin.fs.
type SessionInject(injectQueue: SerialQueue) =
    member x.InjectQueue = injectQueue
