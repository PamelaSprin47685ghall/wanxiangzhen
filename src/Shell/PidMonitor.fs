module Shell.PidMonitor
open System
open Kernel
open Shell.NodeInterop

// alive：跨平台 PID 探活（Windows/POSIX）
// 使用 NodeInterop.nodeProcessKill 替换 System.Diagnostics.Process.GetProcessById
// 捕获 EPERM（视为存活）和 ESRCH（进程不存在，视为死亡）两种边界
let alive (pid: int) : bool =
    try
        // process.kill(pid, "0") = 探活；ESRCH 表示 pid 不存在（返回 false）
        // EPERM 表示 pid 存在但无法发信号（仍视为存活，返回 true）
        // nodeProcessKill 返回 true 表示成功发信号（pid 存在）
        nodeProcessKill pid "0"
    with
    | :? System.ComponentModel.Win32Exception as ex when ex.NativeErrorCode = 3 -> false  // ESRCH
    | _ -> true  // EPERM 或其他异常均视为存活（保守策略：宁可多轮询也不误杀）

/// startMonitor：启动 PID 轮询，由 Plugin 调用（使用 JS setInterval）
/// getTasks: 获取当前所有 task 及其 pid
/// onExit: pid 消失时的回调
/// intervalMs: 轮询间隔（毫秒）
let startMonitor (getTasks: unit -> (Kernel.TaskId * int option) list) (onExit: Kernel.TaskId -> unit) (intervalMs: int) : System.IDisposable =
    // 使用 NodeInterop 的 setInterval（在 JS 端包装），避免 .NET Timer 依赖
    // 回退方案：若 Fable 无法直接绑定 setInterval，则用 System.Threading.Timer
    // 为 MVP 简单起见，这里仍使用 System.Threading.Timer（在 Fable 编译到 JS 时 Timer 由 Fable.Core 提供 polyfill）
    let mutable disposed = false
    let timer = new System.Threading.Timer(fun _ ->
        if not disposed then
            let tasks = getTasks()
            for taskId, pidOpt in tasks do
                match pidOpt with
                | Some pid ->
                    if not (alive pid) then
                        onExit taskId
                | None -> ()
    , null, 0, intervalMs)
    { new System.IDisposable with
        member _.Dispose() =
            disposed <- true
            timer.Dispose() }
