using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;

namespace SingBoxWinUI.Services;

/// <summary>
/// 纯 Win32 托盘图标：Shell_NotifyIcon + 消息窗口 + 原生右键菜单。
/// 不引入任何第三方包；所有调用都必须在 UI 线程（有消息泵）上执行。
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private const int WmApp = 0x8000;
    private const int WmTrayIcon = WmApp + 1;
    private const int WmNull = 0x0000;
    private const int WmDestroy = 0x0002;
    private const int WmLButtonDblClk = 0x0203;
    private const int WmRButtonUp = 0x0205;
    private const int WmContextMenu = 0x007B;

    private const uint NimAdd = 0;
    private const uint NimModify = 1;
    private const uint NimDelete = 2;

    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;

    private const uint MfString = 0x00000000;
    private const uint MfSeparator = 0x00000800;
    private const uint MfGrayed = 0x00000001;
    private const uint MfChecked = 0x00000008;
    private const uint MfPopup = 0x00000010;

    private const uint TpmLeftAlign = 0x0000;
    private const uint TpmTopAlign = 0x0000;
    private const uint TpmReturnCmd = 0x0100;

    private const int IdiApplication = 32512;
    private const uint MsgfltAllow = 1;

    /// <summary>任务栏重建广播。消息窗口（HWND_MESSAGE）收不到 HWND_BROADCAST，必须用顶层窗口。</summary>
    private static readonly uint TaskbarCreatedMessage = RegisterWindowMessageW("TaskbarCreated");

    private readonly string _tooltip;
    private readonly Func<bool> _isCoreRunning;
    private readonly Action _showWindow;
    private readonly Action _toggleCore;
    private readonly Action _exitApp;
    private readonly Func<bool> _isSystemProxyChecked;
    private readonly Action _toggleSystemProxy;
    private readonly Func<string> _getClashMode;
    private readonly Action<string> _setClashMode;

    // 委托必须被字段持有，否则 GC 后窗口过程指针失效会直接崩
    private readonly WndProcDelegate _wndProcDelegate;

    /// <summary>UI 线程队列：开机重试循环必须回到有消息泵的线程上补注册。</summary>
    private readonly DispatcherQueue? _dispatcher;

    /// <summary>最近一次成功注册的 NOTIFYICONDATA，NIM_MODIFY / 重建时复用。</summary>
    private NotifyIconData _nid;

    private int _retryRunning;

    private IntPtr _hwnd;
    private IntPtr _icon;
    private IntPtr _instance;
    private string? _className;
    private bool _iconIsShared;
    private bool _disposed;

    public TrayIconService(
        string tooltip,
        Func<bool> isCoreRunning,
        Action showWindow,
        Action toggleCore,
        Action exitApp,
        Func<bool> isSystemProxyChecked,
        Action toggleSystemProxy,
        Func<string> getClashMode,
        Action<string> setClashMode)
    {
        _tooltip = tooltip;
        _isCoreRunning = isCoreRunning;
        _showWindow = showWindow;
        _toggleCore = toggleCore;
        _exitApp = exitApp;
        _isSystemProxyChecked = isSystemProxyChecked;
        _toggleSystemProxy = toggleSystemProxy;
        _getClashMode = getClashMode;
        _setClashMode = setClashMode;
        _wndProcDelegate = WndProc;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
    }

    public bool IsVisible { get; private set; }

    /// <summary>创建隐藏消息窗口并添加托盘图标。返回是否成功。</summary>
    public bool Show()
    {
        if (IsVisible)
        {
            return true;
        }

        try
        {
            var instance = GetModuleHandle(null);

            // 类名固定，第二个实例要靠它 FindWindowEx 找到这个隐藏窗口
            var className = SingleInstance.TrayWindowClass;

            var windowClass = new WndClassEx
            {
                cbSize = (uint)Marshal.SizeOf<WndClassEx>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate),
                hInstance = instance,
                lpszClassName = className,
            };

            if (RegisterClassExW(ref windowClass) == 0)
            {
                return false;
            }

            // hWndParent 必须是 0：HWND_MESSAGE 窗口收不到 HWND_BROADCAST 的 "TaskbarCreated"，
            // 开机自启早于 explorer.exe 就绪时图标就永久丢失。窗口 style=0（无 WS_VISIBLE）不会出现在任务栏。
            _hwnd = CreateWindowExW(0, className, "SingBoxWinUI.Tray", 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero)
            {
                return false;
            }

            // 记下类名与模块句柄，Dispose 时要反注册（否则再次 Show 会类名冲突）
            _className = className;
            _instance = instance;

            // 显式放行两条跨权限消息：唤醒（低权限实例 → 本进程）与任务栏重建（explorer → 本进程）
            if (SingleInstance.WakeUpMessage != 0)
            {
                ChangeWindowMessageFilterEx(_hwnd, SingleInstance.WakeUpMessage, MsgfltAllow, IntPtr.Zero);
            }

            if (TaskbarCreatedMessage != 0)
            {
                ChangeWindowMessageFilterEx(_hwnd, TaskbarCreatedMessage, MsgfltAllow, IntPtr.Zero);
            }

            _icon = LoadTrayIcon();

            var trayReady = FindWindowW("Shell_TrayWnd", null) != IntPtr.Zero;
            var registered = trayReady && RegisterIcon(deleteFirst: false);

            // 开机自启时 explorer 任务栏可能还没起来：后台每 1s 补注册，最多 60 次
            if (!registered)
            {
                StartRetryLoop();
            }

            return IsVisible;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private IntPtr LoadTrayIcon()
    {
        var icon = LoadAppIcon(out var shared);
        _iconIsShared = shared;
        return icon;
    }

    private IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        // 第二个实例发来的唤醒消息（可能是低权限进程，靠 ChangeWindowMessageFilterEx 放行）
        if (message == SingleInstance.WakeUpMessage && message != 0)
        {
            _showWindow();
            return IntPtr.Zero;
        }

        // explorer 重启 / 任务栏重建：托盘图标会全部失效，必须删掉再重新加
        if (TaskbarCreatedMessage != 0 && message == TaskbarCreatedMessage)
        {
            RegisterIcon(deleteFirst: true);
            return IntPtr.Zero;
        }

        if (message == WmTrayIcon)
        {
            var mouseMessage = (uint)(lParam.ToInt64() & 0xFFFF);

            if (mouseMessage == WmLButtonDblClk)
            {
                _showWindow();
            }
            else if (mouseMessage == WmRButtonUp || mouseMessage == WmContextMenu)
            {
                ShowContextMenu();
            }

            return IntPtr.Zero;
        }

        // 不能在这里 PostQuitMessage，否则会把整个应用的消息泵关掉
        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    /// <summary>构造一份完整的 NOTIFYICONDATA。</summary>
    private NotifyIconData BuildNotifyIconData() => new()
    {
        cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
        hWnd = _hwnd,
        uID = 1,
        uFlags = NifMessage | NifIcon | NifTip,
        uCallbackMessage = WmTrayIcon,
        hIcon = _icon,
        szTip = _tooltip,
    };

    /// <summary>
    /// 注册/重建图标。deleteFirst=true 时先 NIM_DELETE 再 NIM_ADD，
    /// 避免任务栏重建或首次半失败后残留一条无法响应的旧图标。
    /// </summary>
    private bool RegisterIcon(bool deleteFirst)
    {
        if (_disposed || _hwnd == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            if (deleteFirst)
            {
                var remove = new NotifyIconData
                {
                    cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
                    hWnd = _hwnd,
                    uID = 1,
                };
                Shell_NotifyIconW(NimDelete, ref remove);
                IsVisible = false;
            }

            var data = BuildNotifyIconData();
            if (!Shell_NotifyIconW(NimAdd, ref data))
            {
                return false;
            }

            _nid = data;
            IsVisible = true;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 隐藏到托盘 / 唤醒窗口时的自愈保底：先用 NIM_MODIFY 探测图标是否还活着，
    /// 失败说明任务栏重建过或图标被系统清掉，立刻删掉重加。
    /// </summary>
    public bool EnsureTrayIcon()
    {
        if (_disposed || _hwnd == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            var probe = _nid.cbSize == 0 ? BuildNotifyIconData() : _nid;
            if (Shell_NotifyIconW(NimModify, ref probe))
            {
                IsVisible = true;
                return true;
            }

            return RegisterIcon(deleteFirst: true);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>开机启动期后台重试：1s 一次、最多 60 次，全部在 UI 线程执行。</summary>
    private void StartRetryLoop()
    {
        if (_dispatcher is null || Interlocked.Exchange(ref _retryRunning, 1) == 1)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                for (var attempt = 0; attempt < 60 && !_disposed && !IsVisible; attempt++)
                {
                    await Task.Delay(1000).ConfigureAwait(false);
                    if (_disposed || IsVisible)
                    {
                        break;
                    }

                    var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    if (!_dispatcher.TryEnqueue(() =>
                    {
                        try
                        {
                            tcs.TrySetResult(
                                FindWindowW("Shell_TrayWnd", null) != IntPtr.Zero
                                && RegisterIcon(deleteFirst: true));
                        }
                        catch (Exception ex)
                        {
                            tcs.TrySetException(ex);
                        }
                    }))
                    {
                        break;
                    }

                    try
                    {
                        if (await tcs.Task.ConfigureAwait(false))
                        {
                            break;
                        }
                    }
                    catch (Exception)
                    {
                        // 单次失败继续重试
                    }
                }
            }
            catch (Exception)
            {
                // 重试循环不向启动流程抛
            }
            finally
            {
                Interlocked.Exchange(ref _retryRunning, 0);
            }
        });
    }

    private void ShowContextMenu()
    {
        var running = _isCoreRunning();
        var loc = LocalizationService.Instance;
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            return;
        }

        try
        {
            AppendMenuW(menu, MfString, 1, loc.TrayShowWindow);
            AppendMenuW(menu, MfSeparator, 0, null);

            // 系统代理：勾选标记反映"当前是否真的生效"
            AppendMenuW(
                menu,
                MfString | (_isSystemProxyChecked() ? MfChecked : 0),
                10,
                loc.HomeSystemProxy);

            // 代理模式子菜单（互斥勾选）
            var modeMenu = CreatePopupMenu();
            if (modeMenu != IntPtr.Zero)
            {
                var currentMode = _getClashMode();
                // 菜单是每次右键现建的，直接按当前语言取文案即可，不需要额外刷新机制
                AppendMenuW(modeMenu, MfString | (IsMode(currentMode, "Rule") ? MfChecked : 0), 21, loc.TrayModeRule);
                AppendMenuW(modeMenu, MfString | (IsMode(currentMode, "Global") ? MfChecked : 0), 22, loc.TrayModeGlobal);
                AppendMenuW(modeMenu, MfString | (IsMode(currentMode, "Direct") ? MfChecked : 0), 23, loc.TrayModeDirect);

                AppendMenuW(menu, MfPopup, modeMenu, loc.HomeProxyMode);
            }

            AppendMenuW(menu, MfSeparator, 0, null);
            AppendMenuW(menu, MfString, 2, running ? loc.TrayStopCore : loc.TrayStartCore);
            AppendMenuW(menu, MfSeparator, 0, null);
            AppendMenuW(menu, MfString, 3, loc.TrayExit);

            GetCursorPos(out var point);

            // 必须先抢前台，否则菜单点外面不会消失
            SetForegroundWindow(_hwnd);

            var command = TrackPopupMenu(
                menu,
                TpmReturnCmd | TpmLeftAlign | TpmTopAlign,
                point.X,
                point.Y,
                0,
                _hwnd,
                IntPtr.Zero);

            PostMessageW(_hwnd, WmNull, IntPtr.Zero, IntPtr.Zero);

            switch (command)
            {
                case 1:
                    _showWindow();
                    break;
                case 2:
                    _toggleCore();
                    break;
                case 3:
                    _exitApp();
                    break;
                case 10:
                    _toggleSystemProxy();
                    break;
                case 21:
                    _setClashMode("Rule");
                    break;
                case 22:
                    _setClashMode("Global");
                    break;
                case 23:
                    _setClashMode("Direct");
                    break;
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private static bool IsMode(string current, string expected) =>
        string.Equals(current, expected, StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            if (IsVisible)
            {
                var data = new NotifyIconData
                {
                    cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
                    hWnd = _hwnd,
                    uID = 1,
                };

                Shell_NotifyIconW(NimDelete, ref data);
                IsVisible = false;
            }

            if (_hwnd != IntPtr.Zero)
            {
                DestroyWindow(_hwnd);
                _hwnd = IntPtr.Zero;
            }

            // 类不反注册的话，Dispose 之后再 Show() 会因 ERROR_CLASS_ALREADY_EXISTS 直接失败
            if (_className is not null && _instance != IntPtr.Zero)
            {
                UnregisterClassW(_className, _instance);
                _className = null;
            }

            if (_icon != IntPtr.Zero)
            {
                // 系统共享图标（LR_SHARED）不归我们释放，销毁它属于误用
                if (!_iconIsShared)
                {
                    DestroyIcon(_icon);
                }

                _icon = IntPtr.Zero;
            }
        }
        catch (Exception)
        {
            // 退出路径上不再抛
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;

        public uint dwState;
        public uint dwStateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;

        public uint uVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;

        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIconW(uint message, ref NotifyIconData data);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconExW(string file, int iconIndex, out IntPtr large, out IntPtr small, uint count);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WndClassEx windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessageW(string message);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindowW(string? className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool UnregisterClassW(string className, IntPtr instance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ChangeWindowMessageFilterEx(IntPtr hWnd, uint message, uint action, IntPtr changeInfo);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIconW(IntPtr instance, IntPtr iconName);

    /// <summary>
    /// 取 exe 里嵌入的应用图标（csproj 的 ApplicationIcon）。
    /// 托盘用 16x16，所以优先取 ExtractIconEx 的 small；取不到再退回系统默认图标。
    /// shared 表示拿到的是系统共享图标（LoadIcon 的 LR_SHARED），这种句柄不能 DestroyIcon。
    /// </summary>
    private static IntPtr LoadAppIcon(out bool shared)
    {
        shared = false;

        try
        {
            // 计划任务 ONLOGON 的默认 CWD 是 System32，所有路径都必须用绝对路径
            var candidates = new[]
            {
                Environment.ProcessPath,
                Path.Combine(AppContext.BaseDirectory, "SingBoxWinUI.exe"),
                Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"),
            };

            foreach (var path in candidates)
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    continue;
                }

                var count = ExtractIconExW(path, 0, out var large, out var small, 1);
                if (count > 0)
                {
                    if (small != IntPtr.Zero)
                    {
                        if (large != IntPtr.Zero) DestroyIcon(large);
                        return small;
                    }

                    if (large != IntPtr.Zero)
                    {
                        return large;
                    }
                }
            }
        }
        catch (Exception)
        {
            // 落到系统默认图标
        }

        shared = true;
        return LoadIconW(IntPtr.Zero, (IntPtr)IdiApplication);
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenuW(IntPtr menu, uint flags, IntPtr itemId, string? item);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hWnd, IntPtr rect);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool PostMessageW(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
