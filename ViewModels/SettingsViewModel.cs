using System.Diagnostics;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Dispatching;
using CommunityToolkit.Mvvm.Input;
using SingBoxWinUI.Services;

namespace SingBoxWinUI.ViewModels;

/// <summary>
/// 设置页。直接读写 AppSettingsService，改动即持久化。
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly AppSettingsService _settings;
    private readonly AutoStartService _autoStart = new();

    private readonly KernelUpdateService _kernelUpdate = new();

    private KernelReleaseInfo? _kernelRelease;

    private string _kernelLocalVersion = string.Empty;

    private string _kernelStatusReason = string.Empty;

    private KernelState _kernelState = KernelState.NotChecked;

    private KernelUpdateStage _kernelStage = KernelUpdateStage.Downloading;

    private readonly DispatcherQueue? _dispatcher = DispatcherQueue.GetForCurrentThread();

    private AppReleaseInfo? _appRelease;

    private string _appUpdateReason = string.Empty;

    private AppUpdateState _appUpdateState = AppUpdateState.NotChecked;

    private enum AppUpdateState
    {
        NotChecked,
        Checking,
        UpToDate,
        Available,
        Failed,
        LightweightBlocked,
    }

    private enum KernelState
    {
        NotChecked,
        Checking,
        UpToDate,
        UpdateAvailable,
        NoAsset,
        CheckFailed,
        Updating,
        Updated,
        UpdateFailed,
        RolledBack,
        LightweightBlocked,
    }

    /// <summary>构造期回填控件值时不要触发保存/系统调用。</summary>
    private bool _isLoading = true;

    public SettingsViewModel(AppSettingsService settings)
    {
        _settings = settings;
        var current = settings.Current;

        AutoStartApp = current.AutoStartApp;
        SilentStartToTray = current.SilentStartToTray;
        AutoStartCore = current.AutoStartCore;
        CloseToTray = current.CloseToTray;
        AppThemeIndex = current.AppTheme switch
        {
            "Light" => 1,
            "Dark" => 2,
            _ => 0,
        };
        LanguageIndex = string.Equals(current.Language, LocalizationService.EnglishLanguage, StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        SpeedTestUrl = current.SpeedTestUrl;
        UnifiedDelay = current.UnifiedDelay;
        EnableIpv6 = current.EnableIpv6;

        AutoTestDelay = current.AutoTestDelay;
        AutoTestDelayIntervalIndex = current.AutoTestDelayIntervalMinutes switch
        {
            5 => 0,
            10 => 1,
            30 => 3,
            60 => 4,
            _ => 2,
        };

        EnableLightweightMode = current.EnableLightweightMode;
        LightweightDelayIndex = current.LightweightDelayMinutes switch
        {
            1 => 0,
            3 => 1,
            10 => 3,
            15 => 4,
            30 => 5,
            _ => 2,
        };

        WebDavUrl = current.WebDavUrl;
        WebDavUsername = current.WebDavUsername;
        WebDavPassword = current.WebDavPassword;
        AutoWebDavBackupOnStartup = current.AutoWebDavBackupOnStartup;
        AutoCheckAppUpdate = current.AutoCheckAppUpdate;

        TunInterfaceName = current.TunInterfaceName;
        TunStackIndex = current.TunStack?.ToLowerInvariant() switch
        {
            "gvisor" => 1,
            "mixed" => 2,
            _ => 0,
        };
        TunMtu = current.TunMtu;
        TunAutoRoute = current.TunAutoRoute;
        TunStrictRoute = current.TunStrictRoute;

        _isLoading = false;

        // 下拉框选项文案要跟着语言走
        LocalizationService.Instance.LanguageChanged += OnLanguageChanged;

        // 后台轻量模式：连本地版本探测都不自动发起，更不会请求 GitHub
        if (App.Main.IsInLightweightMode)
        {
            _kernelState = KernelState.LightweightBlocked;
            CoreVersion = LocalizationService.Instance.KernelVersionDisplay(string.Empty);
            RaiseKernelTexts();
        }
        else
        {
            _ = LoadCoreVersionAsync();
        }

        // 启动期检查可能早于本页创建：先吃缓存结果，再订阅后续广播
        AppUpdateService.CheckCompleted += OnAppUpdateCheckCompleted;
        if (App.Main.IsInLightweightMode)
        {
            _appUpdateState = AppUpdateState.LightweightBlocked;
        }
        else if (AppUpdateService.LastResult is { } cached)
        {
            ApplyAppUpdateResult(cached);
        }

        // 自启状态要查系统（计划任务 / 注册表），异步探测，别在 UI 线程同步等 schtasks
        _ = RefreshAutoStartStateAsync();
    }

    [ObservableProperty]
    public partial bool AutoStartApp { get; set; }

    [ObservableProperty]
    public partial bool SilentStartToTray { get; set; }

    [ObservableProperty]
    public partial bool AutoStartCore { get; set; }

    [ObservableProperty]
    public partial bool CloseToTray { get; set; }

    /// <summary>0 跟随系统 / 1 浅色 / 2 深色。</summary>
    [ObservableProperty]
    public partial int AppThemeIndex { get; set; }

    [ObservableProperty]
    public partial string SpeedTestUrl { get; set; } = AppSettings.DefaultSpeedTestUrl;

    /// <summary>统一延迟：连续探测两次取第二次。</summary>
    [ObservableProperty]
    public partial bool UnifiedDelay { get; set; } = true;

    /// <summary>恢复备份后把界面重新对齐到磁盘上的设置。</summary>
    public void ReloadFromSettings()
    {
        _isLoading = true;
        try
        {
            var current = _settings.Current;
            AppThemeIndex = current.AppTheme switch { "Light" => 1, "Dark" => 2, _ => 0 };
            LanguageIndex = string.Equals(current.Language, LocalizationService.EnglishLanguage, StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            SpeedTestUrl = current.SpeedTestUrl;
            UnifiedDelay = current.UnifiedDelay;
            EnableIpv6 = current.EnableIpv6;
            AutoTestDelay = current.AutoTestDelay;
            AutoTestDelayIntervalIndex = current.AutoTestDelayIntervalMinutes switch { 5 => 0, 10 => 1, 30 => 3, 60 => 4, _ => 2 };
            EnableLightweightMode = current.EnableLightweightMode;
            LightweightDelayIndex = current.LightweightDelayMinutes switch { 1 => 0, 3 => 1, 10 => 3, 15 => 4, 30 => 5, _ => 2 };
            WebDavUrl = current.WebDavUrl;
            WebDavUsername = current.WebDavUsername;
            WebDavPassword = current.WebDavPassword;
            AutoWebDavBackupOnStartup = current.AutoWebDavBackupOnStartup;
            CloseToTray = current.CloseToTray;
            SilentStartToTray = current.SilentStartToTray;
            AutoStartCore = current.AutoStartCore;
            AutoCheckAppUpdate = current.AutoCheckAppUpdate;

            // TUN 参数：恢复备份后必须跟着刷新，否则界面显示旧值、
            // 用户点"应用"时写回的却是他以为没改过的新值。
            TunInterfaceName = current.TunInterfaceName;
            TunStackIndex = current.TunStack?.ToLowerInvariant() switch
            {
                "gvisor" => 1,
                "mixed" => 2,
                _ => 0,
            };
            TunMtu = current.TunMtu;
            TunAutoRoute = current.TunAutoRoute;
            TunStrictRoute = current.TunStrictRoute;
        }
        finally
        {
            _isLoading = false;
        }

        // 主题赋值被 _isLoading 屏蔽了联动，这里补一次立即生效
        App.ApplyTheme(_settings.Current.AppTheme);

        // 自启的真实状态在系统里（计划任务 / Run 键），异步重新探测
        _ = RefreshAutoStartStateAsync();
    }

    // ---- 下拉框选项（切换语言时整表重建，ComboBox 才会重画选中项）----

    /// <summary>
    /// 语言下拉的选项本身不随语言变化（"简体中文"/"English" 用各自的母语书写）。
    /// 刻意不在这里发通知：切语言时重建这个列表会把正在被点击的项销毁，
    /// ComboBox 会短暂把 SelectedIndex 打回 -1，进而把语言又切回去。
    /// </summary>
    public IReadOnlyList<string> LanguageOptions { get; } =
        [LocalizationService.Instance.SetLanguageChinese, LocalizationService.Instance.SetLanguageEnglish];

    public IReadOnlyList<string> ThemeOptions =>
        [LocalizationService.Instance.SetThemeSystem, LocalizationService.Instance.SetThemeLight, LocalizationService.Instance.SetThemeDark];

    public IReadOnlyList<string> LightweightDelayOptions =>
    [
        LocalizationService.Instance.CommonMinutes1, LocalizationService.Instance.CommonMinutes3,
        LocalizationService.Instance.CommonMinutes5, LocalizationService.Instance.CommonMinutes10,
        LocalizationService.Instance.CommonMinutes15, LocalizationService.Instance.CommonMinutes30,
    ];

    public IReadOnlyList<string> AutoTestIntervalOptions =>
    [
        LocalizationService.Instance.CommonMinutes5, LocalizationService.Instance.CommonMinutes10,
        LocalizationService.Instance.CommonMinutes15, LocalizationService.Instance.CommonMinutes30,
        LocalizationService.Instance.CommonMinutes60,
    ];

    public IReadOnlyList<string> TunStackOptions => ["system", "gvisor", "mixed"];

    /// <summary>
    /// 重新下发下拉框的选中索引。
    /// x:Bind 给 ItemsSource 赋值时 ComboBox 会把 SelectedIndex 重置成 -1，
    /// 索引绑定不会再自动补一次，所以页面加载完（以及换语言后）要手动推一遍。
    /// </summary>
    public void RefreshComboSelections()
    {
        // ComboBox 在 ItemsSource 就位之前就把索引写成 -1 了（字段真的被改掉，
        // 上面的 if (value < 0) 只能挡住副作用）。这里按磁盘设置把真实值放回去。
        var current = _settings.Current;

        if (AppThemeIndex < 0)
        {
            AppThemeIndex = current.AppTheme switch { "Light" => 1, "Dark" => 2, _ => 0 };
        }

        if (LightweightDelayIndex < 0)
        {
            LightweightDelayIndex = current.LightweightDelayMinutes switch
            {
                1 => 0,
                3 => 1,
                10 => 3,
                15 => 4,
                30 => 5,
                _ => 2,
            };
        }

        if (AutoTestDelayIntervalIndex < 0)
        {
            AutoTestDelayIntervalIndex = current.AutoTestDelayIntervalMinutes switch
            {
                5 => 0,
                10 => 1,
                30 => 3,
                60 => 4,
                _ => 2,
            };
        }

        // 值没变时 SetProperty 不会发通知，但 ComboBox 自己停在 -1，
        // 所以这里无条件再推一次，让它按 VM 的值重新选中。
        OnPropertyChanged(nameof(AppThemeIndex));
        OnPropertyChanged(nameof(LightweightDelayIndex));
        OnPropertyChanged(nameof(AutoTestDelayIntervalIndex));
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(ThemeOptions));
        OnPropertyChanged(nameof(LightweightDelayOptions));
        OnPropertyChanged(nameof(AutoTestIntervalOptions));

        // 列表换掉后 ComboBox 的选中项会丢，重新下发一次索引把选中态贴回去
        RefreshComboSelections();

        // 自启方式是运行时探测出来的一句话，语言变了要重新探测一次才换文案
        _ = RefreshAutoStartStateAsync();

        // 内核卡片的状态文案是代码拼出来的，切语言要立即重算
        RaiseKernelTexts();
        RaiseAppUpdateTexts();
    }

    /// <summary>0 = 简体中文，1 = English。</summary>
    [ObservableProperty]
    public partial int LanguageIndex { get; set; }

    /// <summary>后台自动检测节点延迟。</summary>
    [ObservableProperty]
    public partial bool AutoTestDelay { get; set; }

    /// <summary>0=5 / 1=10 / 2=15 / 3=30 / 4=60 分钟。</summary>
    [ObservableProperty]
    public partial int AutoTestDelayIntervalIndex { get; set; } = 2;

    /// <summary>后台轻量模式。</summary>
    [ObservableProperty]
    public partial bool EnableLightweightMode { get; set; } = true;

    /// <summary>0=1 / 1=3 / 2=5 / 3=10 / 4=15 / 5=30 分钟。</summary>
    [ObservableProperty]
    public partial int LightweightDelayIndex { get; set; } = 2;

    [ObservableProperty]
    public partial string WebDavUrl { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string WebDavUsername { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string WebDavPassword { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool AutoWebDavBackupOnStartup { get; set; } = true;

    /// <summary>启用 IPv6：影响 dns.strategy 与 TUN 双栈地址。</summary>
    [ObservableProperty]
    public partial bool EnableIpv6 { get; set; }

    /// <summary>TUN 网卡设备名。</summary>
    [ObservableProperty]
    public partial string TunInterfaceName { get; set; } = "singbox-tun";

    /// <summary>0 = system / 1 = gvisor / 2 = mixed。</summary>
    [ObservableProperty]
    public partial int TunStackIndex { get; set; }

    /// <summary>MTU（NumberBox 用 double）。</summary>
    [ObservableProperty]
    public partial double TunMtu { get; set; } = 9000;

    [ObservableProperty]
    public partial bool TunAutoRoute { get; set; } = true;

    [ObservableProperty]
    public partial bool TunStrictRoute { get; set; } = true;

    [ObservableProperty]
    public partial string CoreVersion { get; set; } = LocalizationService.Instance.Say("检测中…", "Detecting...");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    public partial string StatusMessage { get; set; } = string.Empty;

    public bool HasStatus => !string.IsNullOrEmpty(StatusMessage);

    public bool IsAdmin => App.Main.IsAdmin;

    public bool IsNotAdmin => !IsAdmin;

    /// <summary>当前自启方式的实际权限级别（异步探测 schtasks，不阻塞 UI）。</summary>
    [ObservableProperty]
    public partial string AutoStartMode { get; set; } = LocalizationService.Instance.Say("检测中…", "Detecting...");

    /// <summary>到系统里查一次真实的自启状态，回填开关与权限说明。</summary>
    private async Task RefreshAutoStartStateAsync()
    {
        try
        {
            var enabled = await _autoStart.IsEnabledAsync();
            var mode = await _autoStart.GetModeDescriptionAsync();

            _isLoading = true;
            try
            {
                AutoStartApp = enabled;
            }
            finally
            {
                _isLoading = false;
            }

            AutoStartMode = mode;
        }
        catch (Exception)
        {
            AutoStartMode = LocalizationService.Instance.Say("检测失败", "Detection failed");
        }
    }

    partial void OnAutoStartAppChanged(bool value)
    {
        if (_isLoading)
        {
            return;
        }

        _ = ApplyAutoStartAsync(value);
    }

    private async Task ApplyAutoStartAsync(bool enabled)
    {
        var (ok, message) = await _autoStart.ApplyAsync(enabled, IsAdmin);

        if (ok)
        {
            _settings.Current.AutoStartApp = enabled;
            _settings.Save();
            StatusMessage = message;
        }
        else
        {
            StatusMessage = message;

            // 系统层面没改成功，把开关回滚回去
            _isLoading = true;
            try
            {
                AutoStartApp = !enabled;
            }
            finally
            {
                _isLoading = false;
            }
        }

        AutoStartMode = await _autoStart.GetModeDescriptionAsync();
    }

    partial void OnSilentStartToTrayChanged(bool value)
    {
        if (_isLoading)
        {
            return;
        }

        _settings.Current.SilentStartToTray = value;
        _settings.Save();
    }

    partial void OnAutoStartCoreChanged(bool value)
    {
        if (_isLoading)
        {
            return;
        }

        _settings.Current.AutoStartCore = value;
        _settings.Save();
    }

    partial void OnCloseToTrayChanged(bool value)
    {
        if (_isLoading)
        {
            return;
        }

        _settings.Current.CloseToTray = value;
        _settings.Save();
    }

    partial void OnAppThemeIndexChanged(int value)
    {
        // 换 ItemsSource 时 ComboBox 会先把 SelectedIndex 打成 -1 再写回，忽略这个噪声
        if (value < 0)
        {
            return;
        }

        if (_isLoading)
        {
            return;
        }

        var theme = value switch
        {
            1 => "Light",
            2 => "Dark",
            _ => "Default",
        };

        _settings.Current.AppTheme = theme;
        _settings.Save();

        // 立即生效
        App.ApplyTheme(theme);
    }

    partial void OnUnifiedDelayChanged(bool value)
    {
        if (_isLoading)
        {
            return;
        }

        _settings.Current.UnifiedDelay = value;
        _settings.Save();
    }

    partial void OnLanguageIndexChanged(int value)
    {
        var language = value == 1
            ? LocalizationService.EnglishLanguage
            : LocalizationService.ChineseLanguage;

        // 先切界面再落盘：SetLanguage 会同步触发全量刷新，用户立刻看到效果
        LocalizationService.Instance.SetLanguage(language);

        if (_isLoading)
        {
            return;
        }

        _settings.Current.Language = language;
        _settings.Save();
    }

    partial void OnAutoTestDelayChanged(bool value)
    {
        if (_isLoading)
        {
            return;
        }

        _settings.Current.AutoTestDelay = value;
        _settings.Save();
        StatusMessage = value
            ? LocalizationService.Instance.Say("已开启后台自动测速。", "Auto latency test enabled.")
            : LocalizationService.Instance.Say("已关闭后台自动测速。", "Auto latency test disabled.");
    }

    partial void OnAutoTestDelayIntervalIndexChanged(int value)
    {
        // 换 ItemsSource 时 ComboBox 会先把 SelectedIndex 打成 -1 再写回，忽略这个噪声
        if (value < 0)
        {
            return;
        }

        if (_isLoading)
        {
            return;
        }

        _settings.Current.AutoTestDelayIntervalMinutes = value switch
        {
            0 => 5,
            1 => 10,
            3 => 30,
            4 => 60,
            _ => 15,
        };
        _settings.Save();
    }

    partial void OnEnableLightweightModeChanged(bool value)
    {
        if (_isLoading)
        {
            return;
        }

        _settings.Current.EnableLightweightMode = value;
        _settings.Save();
    }

    partial void OnLightweightDelayIndexChanged(int value)
    {
        // 换 ItemsSource 时 ComboBox 会先把 SelectedIndex 打成 -1 再写回，忽略这个噪声
        if (value < 0)
        {
            return;
        }

        if (_isLoading)
        {
            return;
        }

        _settings.Current.LightweightDelayMinutes = value switch
        {
            0 => 1,
            1 => 3,
            3 => 10,
            4 => 15,
            5 => 30,
            _ => 5,
        };
        _settings.Save();
    }

    partial void OnWebDavUrlChanged(string value) => PersistWebDav(debounced: true);

    partial void OnWebDavUsernameChanged(string value) => PersistWebDav(debounced: true);

    partial void OnWebDavPasswordChanged(string value) => PersistWebDav(debounced: true);

    partial void OnAutoWebDavBackupOnStartupChanged(bool value) => PersistWebDav(debounced: false);

    private void PersistWebDav(bool debounced)
    {
        if (_isLoading)
        {
            return;
        }

        _settings.Current.WebDavUrl = WebDavUrl;
        _settings.Current.WebDavUsername = WebDavUsername;
        _settings.Current.WebDavPassword = WebDavPassword;
        _settings.Current.AutoWebDavBackupOnStartup = AutoWebDavBackupOnStartup;

        // 地址/用户名/密码是逐字符输入的，防抖落盘；开关是点一下，立即写
        if (debounced)
        {
            _settings.SaveDebounced();
        }
        else
        {
            _settings.Save();
        }
    }

    /// <summary>IPv6 改动要重建运行配置，运行中直接热重启。</summary>
    partial void OnEnableIpv6Changed(bool value)
    {
        if (_isLoading)
        {
            return;
        }

        _settings.Current.EnableIpv6 = value;
        _settings.Save();

        if (!App.Main.IsRunning)
        {
            StatusMessage = LocalizationService.Instance.Say($"IPv6 已{(value ? "启用" : "禁用")}，将在下次启动内核时生效。", $"IPv6 {(value ? "enabled" : "disabled")}; takes effect the next time the core starts.");
            return;
        }

        _ = RestartForIpv6Async(value);
    }

    private async Task RestartForIpv6Async(bool enabled)
    {
        StatusMessage = LocalizationService.Instance.Say("正在重启内核以应用 IPv6 设置…", "Restarting the core to apply the IPv6 setting...");
        await App.Main.RestartCoreAsync($"[状态] 正在重启内核以应用 IPv6：{(enabled ? "启用" : "禁用")}");
        StatusMessage = LocalizationService.Instance.Say(
            $"已按新的 IPv6 设置（{(enabled ? "启用" : "禁用")}）重启内核。",
            $"Core restarted with IPv6 {(enabled ? "enabled" : "disabled")}.");
    }

    partial void OnSpeedTestUrlChanged(string value)
    {
        if (_isLoading)
        {
            return;
        }

        _settings.Current.SpeedTestUrl = string.IsNullOrWhiteSpace(value)
            ? AppSettings.DefaultSpeedTestUrl
            : value.Trim();
        _settings.SaveDebounced();
    }

    [RelayCommand]
    private void ResetSpeedTestUrl()
    {
        SpeedTestUrl = AppSettings.DefaultSpeedTestUrl;
    }

    /// <summary>应用 TUN 网卡参数：内核正以 TUN 运行时热重启，否则下次启动生效。</summary>
    [RelayCommand]
    private async Task ApplyTunAsync()
    {
        if (!App.Main.IsRunning)
        {
            StatusMessage = LocalizationService.Instance.Say("内核未运行，TUN 参数将在下次启动时生效。", "The core is not running; TUN settings will apply on the next start.");
            return;
        }

        if (!App.Main.IsTunEnabled)
        {
            StatusMessage = LocalizationService.Instance.Say("当前未启用 TUN 模式，参数将在下次启动时生效。", "TUN mode is off; settings will apply on the next start.");
            return;
        }

        StatusMessage = LocalizationService.Instance.Say("正在按新参数重启内核…", "Restarting the core with the new settings...");
        await App.Main.RestartCoreForTunSettingsAsync();
        StatusMessage = LocalizationService.Instance.Say("已按新 TUN 参数重启内核。", "Core restarted with the new TUN settings.");
    }

    partial void OnTunInterfaceNameChanged(string value)
    {
        if (_isLoading)
        {
            return;
        }

        _settings.Current.TunInterfaceName = string.IsNullOrWhiteSpace(value) ? "singbox-tun" : value.Trim();
        _settings.Save();
    }

    partial void OnTunStackIndexChanged(int value)
    {
        if (_isLoading)
        {
            return;
        }

        _settings.Current.TunStack = value switch
        {
            1 => "gvisor",
            2 => "mixed",
            _ => "system",
        };
        _settings.Save();
    }

    partial void OnTunMtuChanged(double value)
    {
        if (_isLoading)
        {
            return;
        }

        // MTU 合法区间，防止 NumberBox 里敲出 0 或离谱值
        var mtu = (int)Math.Clamp(value, 576, 65535);
        _settings.Current.TunMtu = mtu;
        _settings.Save();
    }

    partial void OnTunAutoRouteChanged(bool value)
    {
        if (_isLoading)
        {
            return;
        }

        _settings.Current.TunAutoRoute = value;
        _settings.Save();
    }

    partial void OnTunStrictRouteChanged(bool value)
    {
        if (_isLoading)
        {
            return;
        }

        _settings.Current.TunStrictRoute = value;
        _settings.Save();
    }

    [RelayCommand]
    private void OpenCoreFolder()
    {
        try
        {
            var directory = SingBoxService.DefaultCoreDirectory;
            Directory.CreateDirectory(directory);

            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{directory}\"")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            StatusMessage = LocalizationService.Instance.Say($"打开目录失败：{ex.Message}", $"Failed to open the folder: {ex.Message}");
        }
    }

    private async Task LoadCoreVersionAsync()
    {
        var version = await _kernelUpdate.GetLocalVersionAsync();
        _kernelLocalVersion = version ?? string.Empty;
        CoreVersion = LocalizationService.Instance.KernelVersionDisplay(_kernelLocalVersion);
        RaiseKernelTexts();
    }

    // ---- sing-box 内核版本与更新卡片 ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KernelButtonsEnabled))]
    [NotifyPropertyChangedFor(nameof(ShowKernelUpdate))]
    public partial bool IsKernelBusy { get; set; }

    [ObservableProperty]
    public partial int KernelProgress { get; set; }

    public bool HasKernelUpdate { get; private set; }

    public bool KernelButtonsEnabled => !IsKernelBusy;

    public bool ShowKernelUpdate => HasKernelUpdate && KernelButtonsEnabled;

    public bool ShowKernelChangelog => !string.IsNullOrWhiteSpace(_kernelRelease?.ReleaseUrl);

    /// <summary>仅「正在下载/替换内核」时展开进度条；检查更新阶段保持 Collapsed。</summary>
    public Visibility KernelProgressVisibility => _kernelState == KernelState.Updating
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Uri? KernelReleaseUri => Uri.TryCreate(_kernelRelease?.ReleaseUrl, UriKind.Absolute, out var uri)
        ? uri
        : null;

    public string KernelVersionText => LocalizationService.Instance.KernelVersionDisplay(_kernelLocalVersion);

    public string KernelStatusText => _kernelState switch
    {
        KernelState.Checking => LocalizationService.Instance.KernelChecking,
        KernelState.UpToDate => LocalizationService.Instance.KernelUpToDate,
        KernelState.UpdateAvailable => LocalizationService.Instance.KernelNewVersionText(
            _kernelRelease?.Version ?? string.Empty),
        KernelState.NoAsset => LocalizationService.Instance.KernelNoAsset,
        KernelState.LightweightBlocked => LocalizationService.Instance.KernelLightweightBlocked,
        KernelState.Updated => LocalizationService.Instance.KernelUpdatedText(_kernelLocalVersion),
        KernelState.RolledBack => LocalizationService.Instance.KernelRolledBack,
        KernelState.CheckFailed => LocalizationService.Instance.KernelCheckFailedText(_kernelStatusReason),
        KernelState.UpdateFailed => LocalizationService.Instance.KernelUpdateFailedText(_kernelStatusReason),
        KernelState.Updating => KernelProgressText,
        _ => LocalizationService.Instance.KernelNotChecked,
    };

    public string KernelProgressText => LocalizationService.Instance.KernelStageText(
        _kernelStage switch
        {
            KernelUpdateStage.Extracting => LocalizationService.Instance.KernelInstalling,
            KernelUpdateStage.Validating => LocalizationService.Instance.KernelInstalling,
            KernelUpdateStage.Stopping => LocalizationService.Instance.KernelInstalling,
            KernelUpdateStage.Swapping => LocalizationService.Instance.KernelInstalling,
            KernelUpdateStage.Restarting => LocalizationService.Instance.KernelRestarting,
            KernelUpdateStage.RollingBack => LocalizationService.Instance.KernelRolledBack,
            _ => LocalizationService.Instance.KernelDownloading,
        },
        KernelProgress);

    public string KernelAssetSizeText => _kernelRelease is { AssetSize: > 0 } info
        ? LocalizationService.Instance.KernelAssetSizeText(info.AssetSize)
        : string.Empty;

    private void SetKernelState(KernelState state)
    {
        _kernelState = state;
        RaiseKernelTexts();
    }

    private void RaiseKernelTexts()
    {
        OnPropertyChanged(nameof(KernelVersionText));
        OnPropertyChanged(nameof(KernelStatusText));
        OnPropertyChanged(nameof(KernelProgressText));
        OnPropertyChanged(nameof(KernelAssetSizeText));
        OnPropertyChanged(nameof(HasKernelUpdate));
        OnPropertyChanged(nameof(ShowKernelUpdate));
        OnPropertyChanged(nameof(ShowKernelChangelog));
        OnPropertyChanged(nameof(KernelReleaseUri));
        OnPropertyChanged(nameof(KernelButtonsEnabled));
        OnPropertyChanged(nameof(KernelProgressVisibility));
    }

    private int? ProxyPortForUpdate => App.Main.IsRunning && App.Main.MixedPort > 0
        ? App.Main.MixedPort
        : null;

    /// <summary>手动检查更新：轻量模式下也允许用户主动点，绝不自动跑。</summary>
    [RelayCommand]
    private async Task CheckKernelUpdate()
    {
        if (IsKernelBusy)
        {
            return;
        }

        IsKernelBusy = true;
        _kernelStatusReason = string.Empty;
        SetKernelState(KernelState.Checking);

        try
        {
            var local = await _kernelUpdate.GetLocalVersionAsync();
            _kernelLocalVersion = local ?? string.Empty;
            CoreVersion = LocalizationService.Instance.KernelVersionDisplay(_kernelLocalVersion);

            var release = await _kernelUpdate.CheckLatestAsync(ProxyPortForUpdate);
            _kernelRelease = release;

            if (string.IsNullOrWhiteSpace(release.AssetUrl))
            {
                HasKernelUpdate = false;
                SetKernelState(KernelState.NoAsset);
                return;
            }

            HasKernelUpdate = KernelUpdateService.IsNewer(_kernelLocalVersion, release.Version);
            SetKernelState(HasKernelUpdate ? KernelState.UpdateAvailable : KernelState.UpToDate);
        }
        catch (OperationCanceledException)
        {
            SetKernelState(KernelState.NotChecked);
        }
        catch (Exception ex)
        {
            _kernelStatusReason = ex.Message;
            SetKernelState(KernelState.CheckFailed);
        }
        finally
        {
            IsKernelBusy = false;
        }
    }

    /// <summary>下载 → 预校验 → 停机换核 → 自动恢复；失败自动回滚 .bak。</summary>
    [RelayCommand]
    private async Task UpdateKernel()
    {
        if (IsKernelBusy || _kernelRelease is null)
        {
            return;
        }

        IsKernelBusy = true;
        KernelProgress = 0;
        _kernelStatusReason = string.Empty;
        SetKernelState(KernelState.Updating);

        var progress = new Progress<KernelUpdateProgress>(value =>
        {
            _kernelStage = value.Stage;
            KernelProgress = value.Percent;
            RaiseKernelTexts();
        });

        try
        {
            var outcome = await _kernelUpdate.UpdateKernelAsync(_kernelRelease, ProxyPortForUpdate, progress);
            _kernelLocalVersion = outcome.Version;
            CoreVersion = LocalizationService.Instance.KernelVersionDisplay(_kernelLocalVersion);
            HasKernelUpdate = false;
            SetKernelState(KernelState.Updated);
        }
        catch (Exception ex)
        {
            var reason = ex.Message switch
            {
                "no-asset" => LocalizationService.Instance.KernelNoAsset,
                "version-check-failed" => LocalizationService.Instance.KernelValidateFailed,
                "restart-failed" => LocalizationService.Instance.KernelRolledBack,
                _ => ex.Message,
            };

            _kernelStatusReason = reason;
            HasKernelUpdate = true;
            var rolledBack = string.Equals(ex.Message, "restart-failed", StringComparison.Ordinal);
            _kernelState = rolledBack ? KernelState.RolledBack : KernelState.UpdateFailed;
            _ = LoadCoreVersionAsync();
            RaiseKernelTexts();
        }
        finally
        {
            IsKernelBusy = false;
        }
    }


    // ---- 关于 / 客户端更新检查 ----

    /// <summary>启动应用时自动检查客户端新版本（切换即保存设置）。</summary>
    [ObservableProperty]
    public partial bool AutoCheckAppUpdate { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCheckAppUpdate))]
    public partial bool IsCheckingAppUpdate { get; set; }

    partial void OnAutoCheckAppUpdateChanged(bool value)
    {
        if (_isLoading)
        {
            return;
        }

        _settings.Current.AutoCheckAppUpdate = value;
        _settings.Save();
    }

    public bool CanCheckAppUpdate => !IsCheckingAppUpdate;

    public string AppVersionText => AppUpdateService.CurrentVersionText;

    public string RepositoryUrl => AppUpdateService.RepositoryUrl;

    public string AppUpdateStatusText => _appUpdateState switch
    {
        AppUpdateState.Checking => LocalizationService.Instance.AboutChecking,
        AppUpdateState.UpToDate => LocalizationService.Instance.AboutUpToDate,
        AppUpdateState.Available => LocalizationService.Instance.AppNewVersionText(
            _appRelease?.VersionText ?? string.Empty),
        AppUpdateState.Failed => LocalizationService.Instance.AppCheckFailedText(_appUpdateReason),
        AppUpdateState.LightweightBlocked => LocalizationService.Instance.AboutLightweightBlocked,
        _ => LocalizationService.Instance.AboutNotChecked,
    };

    /// <summary>仅发现新版本时显示「前往下载」。</summary>
    public Visibility AppUpdateDownloadVisibility => _appUpdateState == AppUpdateState.Available
        ? Visibility.Visible
        : Visibility.Collapsed;

    private void SetAppUpdateState(AppUpdateState state)
    {
        _appUpdateState = state;
        RaiseAppUpdateTexts();
    }

    private void RaiseAppUpdateTexts()
    {
        OnPropertyChanged(nameof(AppUpdateStatusText));
        OnPropertyChanged(nameof(AppUpdateDownloadVisibility));
        OnPropertyChanged(nameof(CanCheckAppUpdate));
        OnPropertyChanged(nameof(AppVersionText));
    }

    private void ApplyAppUpdateResult(AppReleaseInfo info)
    {
        _appRelease = info;
        _appUpdateReason = string.Empty;
        SetAppUpdateState(info.IsNewer ? AppUpdateState.Available : AppUpdateState.UpToDate);
    }

    private void OnAppUpdateCheckCompleted(object? sender, AppReleaseInfo info)
    {
        if (_dispatcher is not null && !_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(() => ApplyAppUpdateResult(info));
            return;
        }

        ApplyAppUpdateResult(info);
    }

    /// <summary>手动检查：轻量模式下也允许用户主动点；自动检查才受轻量模式限制。</summary>
    [RelayCommand]
    private async Task CheckAppUpdate()
    {
        if (IsCheckingAppUpdate)
        {
            return;
        }

        IsCheckingAppUpdate = true;
        _appUpdateReason = string.Empty;
        SetAppUpdateState(AppUpdateState.Checking);

        try
        {
            var info = await AppUpdateService.CheckAsync(ProxyPortForUpdate);
            ApplyAppUpdateResult(info);
        }
        catch (OperationCanceledException)
        {
            SetAppUpdateState(AppUpdateState.NotChecked);
        }
        catch (Exception ex)
        {
            _appUpdateReason = ex.Message;
            SetAppUpdateState(AppUpdateState.Failed);
        }
        finally
        {
            IsCheckingAppUpdate = false;
        }
    }

    [RelayCommand]
    private void OpenRepository() => OpenUrl(AppUpdateService.RepositoryUrl);

    [RelayCommand]
    private void OpenAppRelease() => OpenUrl(_appRelease?.ReleaseUrl ?? AppUpdateService.RepositoryUrl);

    private void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusMessage = LocalizationService.Instance.Say(
                $"打开链接失败：{ex.Message}", $"Could not open link: {ex.Message}");
        }
    }

}
