using System.Runtime.InteropServices;
using System.Threading;

namespace SingBoxWinUI.Services;

/// <summary>
/// 单实例守卫 + 跨权限唤醒。
///
/// 唤醒走「注册窗口消息 + 托盘消息窗口」：高权限实例在托盘窗口上
/// ChangeWindowMessageFilterEx 放行该消息，低权限实例才能穿过 UIPI 把消息投递进去。
/// </summary>
public static class SingleInstance
{
    private const string MutexName = @"Local\SingBoxWinUI_SingleInstance_Mutex";
    private const string WakeUpMessageName = "SingBoxWinUI_WakeUp_Msg";
    private const string TrayWindowClassName = "SingBoxWinUI.TrayWindow";

    private static readonly IntPtr HwndMessage = new(-3);
    private static readonly IntPtr HwndBroadcast = new(0xFFFF);

    /// <summary>AllowSetForegroundWindow 的 ASFW_ANY。</summary>
    private const int AsfwAny = -1;

    private static Mutex? _mutex;

    /// <summary>全局注册的唤醒消息 ID（同名字符串在各进程拿到同一个值）。</summary>
    public static uint WakeUpMessage { get; } = RegisterWindowMessageW(WakeUpMessageName);

    /// <summary>托盘消息窗口的固定类名，供第二个实例 FindWindowEx 定位。</summary>
    public static string TrayWindowClass => TrayWindowClassName;

    /// <summary>
    /// 尝试取得单实例所有权。
    /// waitForHandover=true 用于 --elevate：提权进程要等旧进程先释放锁，最多 timeoutMs。
    /// </summary>
    public static bool TryAcquire(bool waitForHandover, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;

        while (true)
        {
            try
            {
                // 所有权语义 = createdNew：initiallyOwned 只在"本次创建成功"时生效。
                // 已存在时这里不持有所有权，直接换下一个循环等对方释放句柄。
                // （AbandonedMutexException 只可能来自 WaitOne，构造函数不会抛，所以不需要捕获。）
                var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
                if (createdNew)
                {
                    _mutex = mutex;
                    return true;
                }

                mutex.Dispose();
            }
            catch (UnauthorizedAccessException)
            {
                // 打不开就当已有实例在跑
                return false;
            }

            if (!waitForHandover || Environment.TickCount64 >= deadline)
            {
                return false;
            }

            Thread.Sleep(150);
        }
    }

    /// <summary>主动释放锁。提权交接前必须调用，否则新进程会把自己当成重复实例。</summary>
    public static void Release()
    {
        try
        {
            // 创建时拿到了所有权，这里正常释放；真正的"解锁"是下面的 Dispose ——
            // 句柄全部关闭后内核对象才销毁，新进程的 createdNew 才会变成 true。
            _mutex?.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // 理论上不会走到（创建即持有），兜底忽略
        }

        _mutex?.Dispose();
        _mutex = null;
    }

    /// <summary>通知已存在的实例把窗口叫到前台。</summary>
    public static void SignalExistingInstance()
    {
        if (WakeUpMessage == 0)
        {
            return;
        }

        // 本进程是用户刚双击起来的，拥有前台权限；把它转授出去，
        // 否则目标进程的 SetForegroundWindow 会被系统拒绝（只闪任务栏）。
        AllowSetForegroundWindow(AsfwAny);

        // 消息窗口是 HWND_MESSAGE 的子窗口，用 FindWindowEx 定位
        var hwnd = FindWindowExW(HwndMessage, IntPtr.Zero, TrayWindowClassName, null);
        if (hwnd != IntPtr.Zero)
        {
            PostMessageW(hwnd, WakeUpMessage, IntPtr.Zero, IntPtr.Zero);
            return;
        }

        // 托盘没建起来时退化为广播
        PostMessageW(HwndBroadcast, WakeUpMessage, IntPtr.Zero, IntPtr.Zero);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessageW(string message);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindowExW(IntPtr parent, IntPtr childAfter, string? className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool PostMessageW(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AllowSetForegroundWindow(int processId);
}
