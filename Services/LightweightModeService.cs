using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;

namespace SingBoxWinUI.Services;

/// <summary>
/// 后台轻量模式：窗口隐藏到托盘达到设定时长后，暂停高频遥测、释放工作集、
/// 并把进程切到 Windows 能效模式（EcoQoS）以降低 CPU 占用。
/// </summary>
public sealed class LightweightModeService
{
    /// <summary>轻量模式期间的维持间隔：只压工作集，不跑 GC。</summary>
    private static readonly TimeSpan MaintenanceInterval = TimeSpan.FromMinutes(3);

    /// <summary>
    /// UI 清理完成后等多久再 GC：WinUI 3 的 Page Unload 与 DirectComposition 句柄解绑
    /// 不是同步完成的，视觉树这一帧还挂在合成器上时做 GC，对象根本收不掉。
    /// </summary>
    private const int XamlFrameReleaseDelayMs = 200;

    private readonly DispatcherQueue _dispatcher;
    private readonly Action<string> _log;

    /// <summary>
    /// 缓冲日志（只入队、不碰 UI 集合）。压缩完之后写 UI 日志会把刚换出的页重新摸热，
    /// 所以这条路径用于"已进入轻量模式"这类发生在 TrimMemory 之后的记录。
    /// </summary>
    private readonly Action<string> _deferredLog;

    /// <summary>内核进程 Id 的取值委托（轻量模式要连带修剪 sing-box.exe 的工作集）。</summary>
    private readonly Func<int?> _kernelProcessId;

    private CancellationTokenSource? _pending;

    /// <summary>轻量模式期间的周期性维持任务。</summary>
    private CancellationTokenSource? _maintenanceCts;

    /// <summary>程序已退出：不再响应进入轻量模式的请求。</summary>
    private bool _isShutdown;

    public LightweightModeService(
        DispatcherQueue dispatcher,
        Action<string> log,
        Func<int?>? kernelProcessId = null,
        Action<string>? deferredLog = null)
    {
        _dispatcher = dispatcher;
        _log = log;
        _kernelProcessId = kernelProcessId ?? (() => null);
        _deferredLog = deferredLog ?? log;
    }

    public bool IsActive { get; private set; }

    /// <summary>即将进入轻量模式（此时可暂停遥测、裁剪日志）。</summary>
    public event EventHandler? Entered;

    /// <summary>已退出轻量模式（此时可恢复遥测）。</summary>
    public event EventHandler? Exited;

    /// <summary>窗口隐藏后开始倒计时。</summary>
    public void Schedule(int minutes)
    {
        CancelPending();

        var cts = new CancellationTokenSource();
        _pending = cts;
        _ = WaitThenEnterAsync(Math.Clamp(minutes, 1, 24 * 60), cts.Token);
    }

    /// <summary>窗口重新可见时取消倒计时（还没进入就什么都不做）。</summary>
    public void CancelPending()
    {
        var pending = _pending;
        _pending = null;

        if (pending is not null)
        {
            try
            {
                pending.Cancel();
            }
            catch (Exception)
            {
                // 忽略
            }
        }
    }

    /// <summary>退出轻量模式：停掉维持任务、关掉 EcoQoS，交给订阅方恢复遥测。</summary>
    public void Exit()
    {
        if (!IsActive)
        {
            return;
        }

        IsActive = false;

        // 先停维持任务：否则恢复遥测后它还会每隔 3 分钟把刚热起来的工作集又压回去
        StopMaintenance();
        SetEcoQos(false);

        Exited?.Invoke(this, EventArgs.Empty);
        _log("[轻量] 已退出轻量模式，恢复实时遥测");
    }

    /// <summary>程序退出：停掉倒计时与维持任务（不触发 Exited，订阅方已在关窗流程里收尾）。</summary>
    public void Shutdown()
    {
        _isShutdown = true;
        IsActive = false;
        CancelPending();
        StopMaintenance();
    }

    private async Task WaitThenEnterAsync(int minutes, CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(minutes), ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        _dispatcher.TryEnqueue(Enter);
    }

    private void Enter()
    {
        if (IsActive || _isShutdown)
        {
            return;
        }

        IsActive = true;
        _ = EnterAsync();
    }

    /// <summary>
    /// 严格串行的进入流程：
    /// (1) UI 线程完成集合清理与页面缓存释放 → (2) 等一帧让 XAML 真正卸载 →
    /// (3) 后台线程压缩堆并修剪双进程工作集 → (4) 只写缓冲日志，不碰 UI。
    /// 任何一步反序，刚换出的物理页都会被后续分配重新摸热。
    /// </summary>
    private async Task EnterAsync()
    {
        try
        {
            // 1) UI 线程：停轮询、清集合、裁日志、丢页面缓存
            await RunOnUiThreadAsync(() => Entered?.Invoke(this, EventArgs.Empty));

            // 2) 给 WinUI 一帧的时间完成 Page Unload / 合成器句柄解绑
            await Task.Delay(XamlFrameReleaseDelayMs);

            if (!IsActive || _isShutdown)
            {
                return;
            }

            // 3) 重活挪到后台线程：全量 GC 会阻塞上百毫秒，压在 UI 线程会卡住消息泵
            var kernelPid = ResolveKernelProcessId();
            await Task.Run(() => TrimMemory(kernelPid));

            if (!IsActive || _isShutdown)
            {
                return;
            }

            SetEcoQos(true);
            StartMaintenance();

            // 4) 只进缓冲队列：此刻 Logs.Add 会立刻分配并触发 UI 集合通知
            _deferredLog($"[轻量] 已进入后台轻量模式，当前工作集：{DescribeWorkingSets()}");
        }
        catch (Exception)
        {
            // 轻量模式失败不影响主功能
        }
    }

    /// <summary>
    /// 把一段操作丢到 UI 线程并等它真正执行完。
    /// 用 TaskCompletionSource 而不是 TryEnqueue 后就返回：GC 必须排在清理之后。
    /// </summary>
    private Task RunOnUiThreadAsync(Action action)
    {
        if (_dispatcher.HasThreadAccess)
        {
            action();
            return Task.CompletedTask;
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        if (!_dispatcher.TryEnqueue(() =>
            {
                try
                {
                    action();
                    tcs.TrySetResult();
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            }))
        {
            // 调度器已关闭（窗口销毁中）：当作完成，避免把进入流程卡死
            tcs.TrySetResult();
        }

        return tcs.Task;
    }

    /// <summary>把两个进程的工作集拼成一行，写进日志方便对照效果。</summary>
    private string DescribeWorkingSets()
    {
        var parts = new List<string>(2);

        try
        {
            using var current = Process.GetCurrentProcess();
            parts.Add($"界面 {current.WorkingSet64 / 1048576.0:F1} MB");
        }
        catch (Exception)
        {
            // 忽略
        }

        if (ResolveKernelProcessId() is int kernelPid)
        {
            try
            {
                using var kernel = Process.GetProcessById(kernelPid);
                parts.Add($"内核 {kernel.WorkingSet64 / 1048576.0:F1} MB");
            }
            catch (Exception)
            {
                // 内核刚好退出
            }
        }

        return parts.Count == 0 ? "未知" : string.Join(" / ", parts);
    }

    private int? ResolveKernelProcessId()
    {
        try
        {
            return _kernelProcessId();
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ---- 内存修剪 ----

    /// <summary>
    /// 三段式释放：先压缩托管堆（含 LOH），再把工作集还给系统，最后连带修剪内核子进程。
    /// 只压缩不修剪，页仍在工作集里；只修剪不压缩，GC 下一轮又会把死对象所在页拉回来。
    /// </summary>
    private static void TrimMemory(int? childProcessId)
    {
        try
        {
            // CompactOnce 只对下一次 Full GC 生效：把大对象堆里的空洞也压掉，
            // 否则 LOH 碎片会让进程一直占着已经用不到的段。
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();

            // 终结器里可能又释放了一批对象，再压一次把它们收干净
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        }
        catch (Exception)
        {
            // 压缩失败不影响功能
        }

        TrimWorkingSetsOnly(childProcessId);
    }

    /// <summary>只压工作集（不跑 GC）：进入时和每 3 分钟的维持都用它。</summary>
    private static void TrimWorkingSetsOnly(int? childProcessId)
    {
        TrimProcessWorkingSet(GetCurrentProcess());
        TrimChildProcessWorkingSet(childProcessId);
    }

    private static void TrimChildProcessWorkingSet(int? childProcessId)
    {
        if (childProcessId is not int pid)
        {
            return;
        }

        try
        {
            using var child = Process.GetProcessById(pid);
            TrimProcessWorkingSet(child.Handle);
        }
        catch (Exception)
        {
            // 内核刚好退出 / 提权进程拿不到句柄：跳过，不影响主流程
        }
    }

    private static void TrimProcessWorkingSet(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return;
        }

        // SetProcessWorkingSetSizeEx(-1, -1) 与 EmptyWorkingSet 等价，
        // 两个都调是为了覆盖不同系统版本上的行为差异（前者某些场景会被配额拦下）。
        var empty = new IntPtr(-1);

        try
        {
            SetProcessWorkingSetSizeEx(handle, empty, empty, 0);
        }
        catch (Exception)
        {
            // 忽略
        }

        try
        {
            EmptyWorkingSet(handle);
        }
        catch (Exception)
        {
            // 忽略
        }
    }

    /// <summary>
    /// 轻量模式期间的低频维持：系统后台线程（托盘、定时器、GC 自身）会零星把页重新拉回工作集，
    /// 每 3 分钟轻量地压一次即可，不再重复跑全量 GC（那会带来明显的 CPU 尖峰）。
    /// </summary>
    private void StartMaintenance()
    {
        StopMaintenance();

        var cts = new CancellationTokenSource();
        _maintenanceCts = cts;
        _ = MaintainAsync(cts.Token);
    }

    private void StopMaintenance()
    {
        var cts = _maintenanceCts;
        _maintenanceCts = null;

        if (cts is null)
        {
            return;
        }

        try
        {
            cts.Cancel();
        }
        catch (Exception)
        {
            // 忽略
        }
    }

    private async Task MaintainAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(MaintenanceInterval);
            while (await timer.WaitForNextTickAsync(ct))
            {
                if (!IsActive)
                {
                    return;
                }

                TrimWorkingSetsOnly(ResolveKernelProcessId());
            }
        }
        catch (OperationCanceledException)
        {
            // 退出轻量模式 / 程序关闭：正常结束
        }
        catch (Exception)
        {
            // 维持任务不该影响主流程
        }
    }

    // ---- EcoQoS ----

    private const int ProcessPowerThrottling = 4;
    private const uint ProcessPowerThrottlingCurrentVersion = 1;
    private const uint ProcessPowerThrottlingExecutionSpeed = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessPowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(
        IntPtr process,
        int informationClass,
        ref ProcessPowerThrottlingState information,
        int informationSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessWorkingSetSizeEx(
        IntPtr process, IntPtr minimumWorkingSetSize, IntPtr maximumWorkingSetSize, uint flags);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool EmptyWorkingSet(IntPtr process);

    private static void SetEcoQos(bool enabled)
    {
        try
        {
            var state = new ProcessPowerThrottlingState
            {
                Version = ProcessPowerThrottlingCurrentVersion,
                ControlMask = ProcessPowerThrottlingExecutionSpeed,
                StateMask = enabled ? ProcessPowerThrottlingExecutionSpeed : 0u,
            };

            using var process = Process.GetCurrentProcess();
            SetProcessInformation(
                process.Handle,
                ProcessPowerThrottling,
                ref state,
                Marshal.SizeOf<ProcessPowerThrottlingState>());
        }
        catch (Exception)
        {
            // 老系统不支持就算了
        }
    }
}
