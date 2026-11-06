using System.Runtime.InteropServices;

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
    private static readonly IntPtr HwndMessage = new(-3);

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

            _hwnd = CreateWindowExW(0, className, "SingBoxWinUI.Tray", 0, 0, 0, 0, 0, HwndMessage, IntPtr.Zero, instance, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero)
            {
                return false;
            }

            // 记下类名与模块句柄，Dispose 时要反注册（否则再次 Show 会类名冲突）
            _className = className;
            _instance = instance;

            // 关键：显式放行唤醒消息，否则普通权限实例会被 UIPI 挡住投递不进来
            if (SingleInstance.WakeUpMessage != 0)
            {
                ChangeWindowMessageFilterEx(_hwnd, SingleInstance.WakeUpMessage, MsgfltAllow, IntPtr.Zero);
            }

            _icon = LoadTrayIcon();

            var data = new NotifyIconData
            {
                cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
                hWnd = _hwnd,
                uID = 1,
                uFlags = NifMessage | NifIcon | NifTip,
                uCallbackMessage = WmTrayIcon,
                hIcon = _icon,
                szTip = _tooltip,
            };

            IsVisible = Shell_NotifyIconW(NimAdd, ref data);
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
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe) && File.Exists(exe))
            {
                var count = ExtractIconExW(exe, 0, out var large, out var small, 1);
                if (count > 0)
                {
                    if (small != IntPtr.Zero)
                    {
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
