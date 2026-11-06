using Microsoft.UI.Dispatching;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI.Xaml;
using SingBoxWinUI.Services;
using SingBoxWinUI.ViewModels;

namespace SingBoxWinUI;

public partial class App : Application
{
    private Window? _window;

    /// <summary>
    /// 全局单例 ViewModel。页面用 NavigationCacheMode.Required 缓存，
    /// 状态只在这里维护，来回切页不会重复初始化。
    /// </summary>
    public static MainViewModel Main { get; private set; } = null!;

    public static ProxiesViewModel Proxies { get; private set; } = null!;

    public static ProfilesViewModel Profiles { get; private set; } = null!;

    public static ConnectionsViewModel Connections { get; private set; } = null!;

    public static LogsViewModel Logs { get; private set; } = null!;

    public static BackupRestoreService Backup { get; private set; } = null!;

    public static RuleSetUpdateService RuleSetsService { get; private set; } = null!;

    public static RuleSetsViewModel RuleSets { get; private set; } = null!;

    /// <summary>订阅元数据与文件管理。</summary>
    public static ProfileManagerService ProfileManager { get; private set; } = null!;

    /// <summary>客户端自身设置（Core\app_settings.json）。</summary>
    public static AppSettingsService AppSettings { get; private set; } = null!;

    /// <summary>主窗口引用，页面里需要 HWND 做 FileOpenPicker 时用。</summary>
    public static Window Window { get; private set; } = null!;

    /// <summary>页面请求切换到某个导航项（如主页跳转订阅管理）。</summary>
    public static event EventHandler<string>? NavigationRequested;

    public App()
    {
        InitializeComponent();

        // 越早越好：InitializeComponent 之后、任何业务代码之前。
        // 之后 OnLaunched 里发生的任何异常都能落到 crash.log。
        RegisterGlobalExceptionHandlers();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            LaunchCore();
        }
        catch (Exception ex)
        {
            HandleFatalStartupException(ex, "OnLaunched");
        }
    }

    /// <summary>
    /// 启动主流程。所有异常由 <see cref="OnLaunched"/> 的 catch 收口，
    /// 保证启动期失败一定写 crash.log 并弹框，而不是静默消失。
    /// </summary>
    private void LaunchCore()
    {
        // 单实例：在创建窗口 / 初始化 ViewModel / 建托盘之前拦截，避免白框闪一下
        // --elevate 表示是提权重启拉起的，要等旧进程把锁交出来
        var isElevate = HasArgument("--elevate");
        if (!SingleInstance.TryAcquire(waitForHandover: isElevate, timeoutMs: 3000))
        {
            SingleInstance.SignalExistingInstance();
            Environment.Exit(0);
            return;
        }

        var dispatcher = DispatcherQueue.GetForCurrentThread();
        var coreDirectory = SingBoxService.DefaultCoreDirectory;

        // 只读目录前置自检：绿色版的数据全写在 exe 同级的 Core\ 里。
        // 放进 Program Files 这类受保护目录时，后面的 Directory.CreateDirectory
        // 会抛 UnauthorizedAccessException —— 那时窗口还没建，用户只会看到「双击没反应」。
        if (!EnsureDirectoryWritable(coreDirectory))
        {
            ShowDirectoryNotWritableDialog(coreDirectory);
            Environment.Exit(1);
            return;
        }

        AppSettings = new AppSettingsService(coreDirectory);

        // 必须在建任何界面之前定好语言，否则首帧会先按默认中文渲染再跳变
        LocalizationService.Instance.SetLanguage(AppSettings.Current.Language);

        // 上次是管理员 + TUN（或明确记住提权）：双击启动时直接申请管理员，
        // 免去每次手动点「以管理员身份重启」。用户拒绝 UAC 就按普通权限继续。
        if (!isElevate && !IsRunningAsAdmin()
            && (AppSettings.Current.IsTunEnabled || AppSettings.Current.AlwaysRunAsAdmin))
        {
            if (TryRelaunchElevated())
            {
                return;
            }

            // 用户拒绝提权：继续普通启动。Main 还没建，提示留到 VM 初始化后再打
            _elevateDeclined = true;
        }

        // 首次运行生成 Core\mixin_pre.json / mixin_post.json 骨架
        MixinService.EnsureFiles(coreDirectory);

        ProfileManager = new ProfileManagerService(AppSettings, coreDirectory);
        ProfileManager.Load();

        Main = new MainViewModel(dispatcher, AppSettings, ProfileManager, ParseConfigArgument());

        if (_elevateDeclined)
        {
            Main.AppendLog("[提示] 已取消管理员提权，本次以普通权限启动，TUN 模式不可用。");
        }
        Proxies = new ProxiesViewModel(Main, dispatcher, AppSettings);
        Profiles = new ProfilesViewModel(ProfileManager, Main, AppSettings);
        Connections = new ConnectionsViewModel(Main, AppSettings);
        Logs = new LogsViewModel(Main);
        Backup = new BackupRestoreService(coreDirectory);
        RuleSetsService = new RuleSetUpdateService(AppSettings, Main, coreDirectory);
        Main.RuleSetService = RuleSetsService;
        RuleSets = new RuleSetsViewModel(RuleSetsService, AppSettings, Main);

        _window = new MainWindow();
        Window = _window;
        _window.Activate();

        // 启动后静默做一次 WebDAV 备份（失败只记日志）
        if (AppSettings.Current.AutoWebDavBackupOnStartup
            && !string.IsNullOrWhiteSpace(AppSettings.Current.WebDavUrl))
        {
            _ = RunStartupBackupAsync();
        }

        // 启动后按间隔静默检查规则集更新
        if (AppSettings.Current.AutoUpdateRuleSets)
        {
            _ = RunScheduledRuleSetUpdateAsync();
        }

        ApplyTheme(AppSettings.Current.AppTheme);

        if (HasArgument("--autorun") && AppSettings.Current.SilentStartToTray)
        {
            // 开机自启：静默驻留托盘
            _window.AppWindow.Hide();
        }

        // 自动拉起内核：开机自启，或者上次退出时内核本来就是开着的
        if (AppSettings.Current.AutoStartCore || AppSettings.Current.LastCoreRunning)
        {
            _ = Main.AutoStartCoreAsync();
        }

        // 管理员模式下把降级的「注册表 Run」自启无感升级成 HIGHEST 计划任务
        _ = UpgradeAutoStartPrivilegeAsync();
    }

    /// <summary>
    /// 恢复备份后：重读设置与订阅，刷新各 ViewModel，内核在跑就按新配置重启。
    /// 刷新必须覆盖所有持有设置副本的 VM —— 漏一个就会留下"界面显示旧值"的死角。
    /// </summary>
    public static async Task ReloadAfterRestoreAsync()
    {
        AppSettings.Load();
        ProfileManager.Load();

        Main.RefreshFromSettings();
        Profiles.ReloadFromSettings();
        RuleSets.ReloadFromSettings();
        Connections.ReloadFromSettings();

        // 设置页的 VM 由页面自己持有，SettingsPage 会在这之后调它的 ReloadFromSettings
        ApplyTheme(AppSettings.Current.AppTheme);

        if (Main.IsRunning)
        {
            await Main.RestartCoreAsync("[状态] 备份已恢复，正在按新配置重启内核...");
        }
    }

    /// <summary>启动后的 WebDAV 自动备份（失败只记日志）。</summary>
    public static async Task RunStartupBackupAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(8));

            var data = Backup.CreateBackup();
            var target = await Backup.WebDavBackupAsync(AppSettings.Current, data);
            Main.AppendLog($"[备份] 启动自动备份完成：{target}（{data.Length / 1024} KB）");
        }
        catch (Exception ex)
        {
            Main.AppendLog($"[提示] 启动自动备份失败：{ex.Message}");
        }
    }

    private static bool IsRunningAsAdmin()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(identity)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>以管理员身份重新拉起自身。返回 true 表示已交接并退出当前实例。</summary>
    private static bool TryRelaunchElevated()
    {
        try
        {
            var startInfo = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = $"--elevate --config \"{AppSettings.Current.ConfigPath}\"",
            };

            // 必须先交锁，否则提权进程会把自己当成重复实例直接退出
            SingleInstance.Release();
            Process.Start(startInfo);
            Environment.Exit(0);
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // 用户在 UAC 点了"否"：把锁拿回来，继续以普通权限运行
            SingleInstance.TryAcquire(waitForHandover: false, timeoutMs: 0);
            return false;
        }
        catch (Exception)
        {
            SingleInstance.TryAcquire(waitForHandover: false, timeoutMs: 0);
            return false;
        }
    }

    /// <summary>本次启动时用户拒绝了 UAC，用于在建好 Main 之后补一条提示。</summary>
    private static bool _elevateDeclined;

    /// <summary>管理员模式下把「注册表 Run」自启升级为最高权限计划任务。</summary>
    private static async Task UpgradeAutoStartPrivilegeAsync()
    {
        try
        {
            if (!Main.IsAdmin || !AppSettings.Current.AutoStartApp)
            {
                return;
            }

            var service = new AutoStartService();
            if (!service.IsRunKeyPresent() || await service.IsTaskCreatedAsync())
            {
                return;
            }

            var (ok, message) = await service.ApplyAsync(true, isAdmin: true);
            Main.AppendLog(ok
                ? "[状态] 开机自启已升级为最高权限计划任务（开机即带管理员权限）。"
                : $"[提示] 开机自启权限升级失败：{message}");
        }
        catch (Exception)
        {
            // 升级失败不影响主流程
        }
    }

    /// <summary>启动后按设定间隔静默更新规则集；失败只记日志。</summary>
    public static async Task RunScheduledRuleSetUpdateAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(12));

            var current = AppSettings.Current;
            var hours = Math.Max(1, current.RuleSetUpdateIntervalHours);

            if (current.LastRuleSetUpdate is { } last
                && DateTimeOffset.Now - last < TimeSpan.FromHours(hours))
            {
                return;
            }

            var result = await RuleSetsService.UpdateAllAsync();
            Main.AppendLog($"[规则集] 自动更新完成：{result.Describe()}");
            RuleSets.Refresh();
        }
        catch (Exception ex)
        {
            Main.AppendLog($"[提示] 规则集自动更新失败：{ex.Message}");
        }
    }

    public static void RequestNavigation(string tag) => NavigationRequested?.Invoke(null, tag);

    /// <summary>切换主题：直接改窗口根元素的 RequestedTheme，立即生效。</summary>
    public static void ApplyTheme(string theme)
    {
        if (Window?.Content is not FrameworkElement root)
        {
            return;
        }

        root.RequestedTheme = theme switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
    }

    private static bool HasArgument(string name) =>
        Environment.GetCommandLineArgs()
            .Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>提权重启时通过 --config 把用户已选中的配置带过来。</summary>
    private static string? ParseConfigArgument()
    {
        var argv = Environment.GetCommandLineArgs();

        for (var i = 1; i < argv.Length - 1; i++)
        {
            if (string.Equals(argv[i], "--config", StringComparison.OrdinalIgnoreCase))
            {
                return argv[i + 1];
            }
        }

        return null;
    }

    // ==================== 启动期兜底：目录自检 / 全局异常 / crash.log ====================

    /// <summary>crash.log 单文件上限，超过就重置（只保留最近一次崩溃，避免反复崩溃写满磁盘）。</summary>
    private const long CrashLogMaxBytes = 512 * 1024;

    private const uint MessageBoxOk = 0x00000000;
    private const uint MessageBoxIconError = 0x00000010;
    private const uint MessageBoxSetForeground = 0x00010000;
    private const uint MessageBoxTopMost = 0x00040000;

    /// <summary>运行期异常只提示一次，避免同一个 UI 事件反复抛异常时刷屏。</summary>
    private static bool _runtimeErrorReported;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    /// <summary>
    /// 在目标目录里建一个临时文件再关掉，确认真的可写。
    /// 用 <see cref="FileOptions.DeleteOnClose"/> 而不是「写完再 Delete」：
    /// 删除由内核在句柄关闭时完成，不会留下垃圾文件，也不会把「文件被占用」误判成「不可写」。
    /// </summary>
    private static bool EnsureDirectoryWritable(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);

            var probe = Path.Combine(directory, $".write-probe-{Guid.NewGuid():N}.tmp");

            using var stream = new FileStream(
                probe,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1,
                FileOptions.DeleteOnClose);

            stream.WriteByte(0);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (Exception)
        {
            // SecurityException / NotSupportedException / PathTooLongException 等
            return false;
        }
    }

    /// <summary>
    /// 三路未处理异常全部挂上：
    /// ① UI 线程（可标记 Handled，能救回来）；
    /// ② 任意线程（进程必死，只能留证据）；
    /// ③ 没人 await 的 Task（.NET Core 默认不终止，但仍要记录并标记已观察）。
    /// </summary>
    private void RegisterGlobalExceptionHandlers()
    {
        UnhandledException += OnUiThreadUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    private void OnUiThreadUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        var path = TryWriteCrashLog("UI 线程未处理异常", e.Exception);

        // 窗口还没建起来 → 属于启动期致命错误，提示后退出
        if (Main is null || Window is null)
        {
            e.Handled = true;
            ShowStartupFailureDialog(e.Exception, path);
            Environment.Exit(1);
            return;
        }

        // 运行期：标记 Handled 让程序继续跑，但只提示一次
        e.Handled = true;

        if (_runtimeErrorReported)
        {
            return;
        }

        _runtimeErrorReported = true;

        ShowMessageBox(
            "程序遇到一个未处理的异常，已记录到日志，界面可能暂时不正常。\r\n\r\n"
            + e.Exception?.Message
            + "\r\n\r\n日志：" + (path ?? "(无法写入 Core\\crash.log)")
            + "\r\n\r\n----------------------------------------\r\n"
            + "An unhandled exception occurred. It has been logged; the UI may be unstable.",
            "Sing-box Runner — 运行异常 / Runtime Error");
    }

    private static void OnDomainUnhandledException(object sender, System.UnhandledExceptionEventArgs e)
    {
        // 这个事件返回后进程就会终止，除了留证据什么都做不了
        TryWriteCrashLog(
            e.IsTerminating ? "非 UI 线程未处理异常（进程即将终止）" : "非 UI 线程未处理异常",
            e.ExceptionObject as Exception);
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        TryWriteCrashLog("未观察的 Task 异常", e.Exception);
        e.SetObserved();
    }

    /// <summary>
    /// 尽力把异常写进 Core\crash.log。任何失败都吞掉 ——
    /// 记录日志的代码自己抛异常会把原始错误彻底埋掉。
    /// </summary>
    private static string? TryWriteCrashLog(string source, Exception? exception)
    {
        try
        {
            var directory = SingBoxService.DefaultCoreDirectory;
            Directory.CreateDirectory(directory);

            var path = Path.Combine(directory, "crash.log");

            var info = new FileInfo(path);
            if (info.Exists && info.Length > CrashLogMaxBytes)
            {
                info.Delete();
            }

            var builder = new StringBuilder();
            builder.Append("===== ")
                   .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture))
                   .Append(" | ")
                   .Append(source)
                   .AppendLine(" =====");
            builder.Append("Process : ").AppendLine(Environment.ProcessPath);
            builder.Append("Pid     : ").AppendLine(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            builder.Append("OS      : ").AppendLine(Environment.OSVersion.ToString());
            builder.Append("Runtime : ").AppendLine(Environment.Version.ToString());
            builder.AppendLine(exception?.ToString() ?? "(no exception object)");
            builder.AppendLine();

            // 带 BOM：crash.log 是给用户和记事本看的，无 BOM 时
            // PowerShell 5.1 的 Get-Content 会按 ANSI(GBK) 解码，中文堆栈直接乱码。
            // AppendAllText 只在文件不存在时写 preamble，后续追加不会重复插 BOM。
            File.AppendAllText(path, builder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            return path;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>目录不可写时的双语提示：这是绿色版最常见的「双击没反应」原因。</summary>
    private static void ShowDirectoryNotWritableDialog(string coreDirectory)
    {
        ShowMessageBox(
            "程序无法启动：当前目录不可写。\r\n\r\n"
            + "目录：" + coreDirectory + "\r\n\r\n"
            + "绿色版的所有配置与缓存都保存在程序目录下，因此不能放在\r\n"
            + "C:\\Program Files 这类受保护目录里。\r\n\r\n"
            + "请任选一种方式：\r\n"
            + "  1. 把整个文件夹移动到有写入权限的位置（例如 D:\\Tools\\）；\r\n"
            + "  2. 右键 SingBoxWinUI.exe →「以管理员身份运行」。\r\n\r\n"
            + "----------------------------------------\r\n"
            + "The application cannot start because its directory is not writable.\r\n\r\n"
            + "Path: " + coreDirectory + "\r\n\r\n"
            + "This portable build keeps all settings next to the executable, so it\r\n"
            + "cannot run from a protected location such as C:\\Program Files.\r\n\r\n"
            + "Either move the whole folder to a writable location (e.g. D:\\Tools\\),\r\n"
            + "or right-click SingBoxWinUI.exe and choose \"Run as administrator\".",
            "Sing-box Runner — 目录不可写 / Directory Not Writable");
    }

    private static void ShowStartupFailureDialog(Exception exception, string? crashLogPath)
    {
        var message = exception.Message;
        if (message.Length > 600)
        {
            message = message[..600] + "…";
        }

        ShowMessageBox(
            "程序启动失败，无法继续运行。\r\n\r\n"
            + message + "\r\n\r\n"
            + "详细信息已写入：\r\n"
            + (crashLogPath ?? "(无法写入 Core\\crash.log)")
            + "\r\n\r\n----------------------------------------\r\n"
            + "The application failed to start.\r\n\r\n"
            + "Details were written to the log file above.",
            "Sing-box Runner — 启动失败 / Startup Failed");
    }

    private static void ShowMessageBox(string text, string caption) =>
        _ = MessageBoxW(
            IntPtr.Zero,
            text,
            caption,
            MessageBoxOk | MessageBoxIconError | MessageBoxSetForeground | MessageBoxTopMost);

    private void HandleFatalStartupException(Exception exception, string source)
    {
        var path = TryWriteCrashLog($"启动失败（{source}）", exception);
        ShowStartupFailureDialog(exception, path);
        Environment.Exit(1);
    }
}
