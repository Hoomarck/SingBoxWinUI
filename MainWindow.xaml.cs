using System.IO;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Windows.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SingBoxWinUI.Helpers;
using SingBoxWinUI.Services;
using SingBoxWinUI.ViewModels;
using SingBoxWinUI.Views;

namespace SingBoxWinUI;

public sealed partial class MainWindow : Window
{
    private readonly TrayIconService _tray;
    private readonly LightweightModeService _lightweight;

    /// <summary>侧栏底部状态块的数据源。</summary>
    public MainViewModel ViewModel => App.Main;

    /// <summary>托盘菜单“彻底退出”置位，之后关闭窗口不再拦截。</summary>
    private bool _isExplicitExit;

    /// <summary>本地化入口：模板里的 {x:Bind Loc.Xxx} 走这里。</summary>
    public LocalizationService Loc => LocalizationService.Instance;


    public MainWindow()
    {
        InitializeComponent();

        Title = "Sing-box Runner";

        // 自定义标题栏 + Mica：让标题栏区域也透出云母材质
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        ApplyWindowIcon();
        LoadTitleBarIcon();

        // NavigationView 的「设置」项由代码赋值，切语言时要跟着刷新
        LocalizationService.Instance.LanguageChanged += (_, _) => ApplySettingsItemText();

        // 自定义标题栏 + 运行时切主题时，系统按钮颜色不会自动跟随，需要手动指定
        RootGrid.ActualThemeChanged += OnRootThemeChanged;
        UpdateTitleBarColors();

        ResizeToLogicalSize(1180, 820);

        _tray = new TrayIconService(
            "SingBox WinUI",
            () => App.Main.IsRunning,
            ShowFromTray,
            () => App.Main.ToggleCoreCommand.Execute(null),
            ExitFromTray,
            // 勾选标记反映"当前是否真的生效"：未运行时退回用户偏好
            () => App.Main.IsRunning ? App.Main.IsSystemProxyOn : App.Main.IsSystemProxyEnabled,
            () => App.Main.ToggleSystemProxy(),
            () => App.Main.ClashMode,
            mode => App.Main.SetClashModeByName(mode));

        App.Main.AppendLog(_tray.Show()
            ? "[托盘] 图标已创建，关闭窗口将最小化到托盘。"
            : "[托盘] 任务栏尚未就绪，图标注册已转入后台重试（最多 60 秒）。");

        _lightweight = new LightweightModeService(
            DispatcherQueue,
            message => App.Main.AppendLog(message),
            // 轻量模式要连带修剪内核进程的工作集：Go 运行时自己不会主动还页
            () => App.Main.KernelProcessId,
            // 压缩之后的日志只进缓冲队列：写 UI 集合会把刚换出的页重新摸热
            message => App.Main.EnqueueDeferredLog(message));
        _lightweight.Entered += OnLightweightEntered;
        _lightweight.Exited += OnLightweightExited;

        AppWindow.Closing += OnAppWindowClosing;

        NavView.SelectedItem = NavView.MenuItems[0];

        // 侧栏收起后只有 48px，放不下状态卡片，直接藏掉避免文字被截断
        NavView.PaneClosing += (_, _) => SpeedCard.Visibility = Visibility.Collapsed;
        NavView.PaneOpening += (_, _) => SpeedCard.Visibility = Visibility.Visible;

        // 原生设置项默认按应用语言显示 "Settings"，与中文界面不一致。
        // SettingsItem 要等模板应用后才存在，所以放在 Loaded 里改。
        NavView.Loaded += (_, _) =>
        {
            ApplySettingsItemText();

            // 启动时焦点默认落在汉堡键上，会画出一圈很显眼的焦点框；主动移交给页面内容
            if (ContentFrame.Content is Control page)
            {
                page.Focus(FocusState.Programmatic);
            }
        };

        NavigateTo(typeof(HomePage));

        App.NavigationRequested += OnNavigationRequested;

        Closed += OnClosed;
    }

    /// <summary>供启动流程在 --autorun 静默隐藏后补一次托盘图标自愈。</summary>
    public void EnsureTrayIcon() => _tray.EnsureTrayIcon();

    private void OnNavigationRequested(object? sender, string tag)
    {
        foreach (var item in NavView.MenuItems.OfType<NavigationViewItem>())
        {
            if (string.Equals(item.Tag as string, tag, StringComparison.Ordinal))
            {
                NavView.SelectedItem = item;
                return;
            }
        }
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected)
        {
            NavigateTo(typeof(SettingsPage));
            return;
        }

        if (args.SelectedItem is NavigationViewItem { Tag: string tag })
        {
            NavigateTo(tag);
        }
    }

    private void NavigateTo(Type pageType)
    {
        // 页面被 Frame 缓存（NavigationCacheMode.Required），这里只是避免重复导航
        if (ContentFrame.CurrentSourcePageType != pageType)
        {
            ContentFrame.Navigate(pageType);
        }
    }

    private void NavigateTo(string tag)
    {
        var pageType = tag switch
        {
            "proxies" => typeof(ProxiesPage),
            "connections" => typeof(ConnectionsPage),
            "logs" => typeof(LogsPage),
            "profiles" => typeof(ProfilesPage),
            _ => typeof(HomePage),
        };

        NavigateTo(pageType);
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_isExplicitExit || !App.AppSettings.Current.CloseToTray)
        {
            return;
        }

        // 隐藏到托盘继续后台运行
        args.Cancel = true;
        sender.Hide();
        _tray.EnsureTrayIcon();

        if (App.AppSettings.Current.EnableLightweightMode)
        {
            _lightweight.Schedule(App.AppSettings.Current.LightweightDelayMinutes);
        }
    }

    /// <summary>Frame 默认缓存容量：释放后要恢复，否则之后每次导航都重建页面视觉树。</summary>
    private const int DefaultFrameCacheSize = 10;

    /// <summary>
    /// 进入轻量模式：断遥测、清空连接集合、裁剪日志、丢掉页面缓存，
    /// 剩下的（EcoQoS / GC / 工作集 / 3 分钟维持）由服务自己做。
    /// </summary>
    private void OnLightweightEntered(object? sender, EventArgs e)
    {
        // 所有 UI 集合（Logs / Connections）只能在 UI 线程动。
        // Entered 目前就在 DispatcherQueue 上触发，这里兜一层防止将来换调用点踩坑。
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => OnLightweightEntered(sender, e));
            return;
        }

        // 先停分配源，再让服务去压缩内存：顺序反了会把刚换出的页立刻拉回来
        App.Main.EnterLightweightMode();
        App.Connections.EnterLightweightMode();
        ReleasePageCaches();
    }

    private void OnLightweightExited(object? sender, EventArgs e)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => OnLightweightExited(sender, e));
            return;
        }

        // 先刷缓冲日志再恢复遥测，恢复后新日志按正常节奏落盘
        App.Main.ExitLightweightMode();
        App.Connections.ExitLightweightMode(ContentFrame.CurrentSourcePageType == typeof(ConnectionsPage));
    }

    /// <summary>
    /// 释放导航历史与已离开页面的视觉树缓存。
    /// 说明：本项目所有页面都是 NavigationCacheMode.Required，Frame 会无视 CacheSize 常驻缓存，
    /// 所以这一步的收益主要在 BackStack（NavigationEntry 及页面参数）与 Enabled 模式页面；
    /// 真正的大头是连接表与日志集合，已在 EnterLightweightMode 里清掉。
    /// </summary>
    private void ReleasePageCaches()
    {
        if (ContentFrame.BackStack.Count > 0)
        {
            ContentFrame.BackStack.Clear();
        }

        if (ContentFrame.ForwardStack.Count > 0)
        {
            ContentFrame.ForwardStack.Clear();
        }

        // 临时压到 0 让 Frame 摘掉"已离开页面"的缓存，随后恢复默认值，
        // 否则一直保持 0 会让之后每次切页都重建页面（反而更费内存与时间）。
        var previous = ContentFrame.CacheSize;
        ContentFrame.CacheSize = 0;
        ContentFrame.CacheSize = previous > 0 ? previous : DefaultFrameCacheSize;
    }

    private void ShowFromTray()
    {
        // 任务栏可能在驻留期间重建过，唤醒时先确保图标还在
        _tray.EnsureTrayIcon();

        // 唤醒就立刻取消待进入的倒计时，并退出轻量模式
        _lightweight.CancelPending();
        _lightweight.Exit();

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

        AppWindow.Show();

        // 只有真的被最小化时才 SW_RESTORE。
        // 窗口若是最大化/普通状态、只是被 AppWindow.Hide() 藏起来，
        // 这里再调 SW_RESTORE 会把它压回普通尺寸，出现"先全屏闪一下再缩小"的抖动。
        if (IsIconic(hwnd))
        {
            ShowWindow(hwnd, SwRestore);
        }

        ForceForeground(hwnd);
    }

    /// <summary>
    /// 强制把窗口拉到前台。SetForegroundWindow 有前台锁限制，
    /// 目标进程不在前台时会被静默拒绝，这里用 AttachThreadInput 借前台线程的输入队列绕过。
    /// </summary>
    private static void ForceForeground(IntPtr hwnd)
    {
        if (GetForegroundWindow() == hwnd)
        {
            return;
        }

        var foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        var currentThread = GetCurrentThreadId();

        var attached = foregroundThread != 0
                       && foregroundThread != currentThread
                       && AttachThreadInput(currentThread, foregroundThread, true);

        try
        {
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
            SetActiveWindow(hwnd);
        }
        finally
        {
            if (attached)
            {
                AttachThreadInput(currentThread, foregroundThread, false);
            }
        }
    }

    private void ExitFromTray()
    {
        _isExplicitExit = true;
        _tray.Dispose();

        // 先优雅停内核，再真正关窗
        App.Main.Shutdown();
        Close();
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        // static 事件持有窗口实例，不解绑会阻止窗口回收，且旧窗口仍会响应导航请求
        App.NavigationRequested -= OnNavigationRequested;

        // 三个单例 VM 各自持有定时循环 / HttpClient，窗口关闭时统一收尾
        App.Connections.Stop();
        App.Proxies.Stop();
        App.Profiles.Stop();

        _lightweight.Shutdown();
        _tray.Dispose();
        App.Main.Shutdown();
    }

    private void OnRootThemeChanged(FrameworkElement sender, object args)
    {
        // 主题画刷缓存必须一起失效，否则新产生的日志条目会继续用旧主题的颜色
        ThemeBrushHelper.InvalidateCache();
        UpdateTitleBarColors();
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int cmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    /// <summary>
    /// AppWindow.Resize 用的是物理像素：150% 缩放下 1040 只有 ~693 逻辑宽度，
    /// 内容区被挤到 400 出头，工具栏会塌。这里按 DPI 换算成逻辑尺寸，并限制在工作区内。
    /// </summary>
    private void ResizeToLogicalSize(int logicalWidth, int logicalHeight)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var dpi = GetDpiForWindow(hwnd);
        var scale = dpi <= 0 ? 1.0 : dpi / 96.0;

        var workArea = Microsoft.UI.Windowing.DisplayArea
            .GetFromWindowId(AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary)
            .WorkArea;

        var width = Math.Min((int)(logicalWidth * scale), (int)(workArea.Width * 0.94));
        var height = Math.Min((int)(logicalHeight * scale), (int)(workArea.Height * 0.94));

        AppWindow.Resize(new Windows.Graphics.SizeInt32(Math.Max(width, 640), Math.Max(height, 480)));
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SetActiveWindow(IntPtr hWnd);

    private const int SwRestore = 9;

    private const int WmSetIcon = 0x0080;
    private const int IconSmall = 0;
    private const int IconBig = 1;
    private const int ImageIconType = 1;
    private const uint LrLoadFromFile = 0x0010;

    /// <summary>HICON 句柄要留到窗口销毁，不能提前释放。</summary>
    private IntPtr _bigIcon;
    private IntPtr _smallIcon;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageW(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImageW(IntPtr hInst, string name, int type, int cx, int cy, uint flags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconExW(string file, int index, out IntPtr large, out IntPtr small, uint count);

    /// <summary>
    /// Unpackaged 模式下 csproj 的 ApplicationIcon 只改 exe 的 PE 资源，
    /// 窗口/任务栏不会自动跟着变，所以这里显式挂一次，再补 Win32 双保险。
    /// </summary>
    private void ApplyWindowIcon()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");

        // 1) 官方 API
        try
        {
            if (File.Exists(iconPath))
            {
                AppWindow.SetIcon(iconPath);
            }
        }
        catch (Exception)
        {
            // 交给下面的 Win32 兜底
        }

        // 2) Win32：任务栏用 ICON_BIG，标题栏/Alt+Tab 小图用 ICON_SMALL
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        if (File.Exists(iconPath))
        {
            _bigIcon = LoadImageW(IntPtr.Zero, iconPath, ImageIconType, 32, 32, LrLoadFromFile);
            _smallIcon = LoadImageW(IntPtr.Zero, iconPath, ImageIconType, 16, 16, LrLoadFromFile);
        }

        if (_bigIcon == IntPtr.Zero || _smallIcon == IntPtr.Zero)
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                var extracted = ExtractIconExW(exe, 0, out var large, out var small, 1);
                if (extracted > 0)
                {
                    if (_bigIcon == IntPtr.Zero)
                    {
                        _bigIcon = large;
                    }

                    if (_smallIcon == IntPtr.Zero)
                    {
                        _smallIcon = small;
                    }
                }
            }
        }

        if (_bigIcon != IntPtr.Zero)
        {
            SendMessageW(hwnd, WmSetIcon, IconBig, _bigIcon);
        }

        if (_smallIcon != IntPtr.Zero)
        {
            SendMessageW(hwnd, WmSetIcon, IconSmall, _smallIcon);
        }
    }

    /// <summary>
    /// NavigationView 的「设置」项是控件内置的，不走 x:Bind，
    /// 只能代码改文案；切语言时也要重新赋一次。
    /// </summary>
    private void ApplySettingsItemText()
    {
        if (NavView.SettingsItem is NavigationViewItem settingsItem)
        {
            var text = LocalizationService.Instance.NavSettings;
            settingsItem.Content = text;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(settingsItem, text);
        }
    }

    /// <summary>
    /// ExtendsContentIntoTitleBar 之后，最小化/最大化/关闭三个按钮是系统画的，
    /// 运行时切换 RequestedTheme 时它们不会自动变色，会出现"深色背景配黑图标"。
    /// </summary>
    private void UpdateTitleBarColors()
    {
        try
        {
            var titleBar = AppWindow.TitleBar;
            var isDark = RootGrid.ActualTheme == ElementTheme.Dark;

            titleBar.ButtonBackgroundColor = Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;

            var foreground = isDark ? Colors.White : Colors.Black;
            var inactive = isDark
                ? Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF)
                : Color.FromArgb(0x8C, 0x00, 0x00, 0x00);
            var hover = isDark
                ? Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)
                : Color.FromArgb(0x14, 0x00, 0x00, 0x00);
            var pressed = isDark
                ? Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)
                : Color.FromArgb(0x24, 0x00, 0x00, 0x00);

            titleBar.ButtonForegroundColor = foreground;
            titleBar.ButtonInactiveForegroundColor = inactive;
            titleBar.ButtonHoverForegroundColor = foreground;
            titleBar.ButtonPressedForegroundColor = foreground;
            titleBar.ButtonHoverBackgroundColor = hover;
            titleBar.ButtonPressedBackgroundColor = pressed;
        }
        catch (Exception)
        {
            // 个别系统版本不支持自定义，忽略
        }
    }

    /// <summary>标题栏左上角的图标（18px 显示，用 64px 图源保证高分屏清晰）。</summary>
    private void LoadTitleBarIcon()
    {
        var pngPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app-64.png");
        if (!File.Exists(pngPath))
        {
            return;
        }

        var bitmap = new BitmapImage
        {
            UriSource = new Uri(pngPath),
            DecodePixelWidth = 64,
        };

        TitleBarIcon.Source = bitmap;
    }
}
