using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Principal;
using System.Threading;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using SingBoxWinUI.Helpers;
using SingBoxWinUI.Models;
using SingBoxWinUI.Services;

namespace SingBoxWinUI.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private const int MaxLogLines = 1000;

    /// <summary>批量落盘间隔：内核一次刷几十行时只投递一次 Dispatcher。</summary>
    private const int LogFlushIntervalMs = 100;

    /// <summary>裁剪批次：超出上限后一次裁这么多，把 RemoveAt(0) 的 O(n) 摊薄。</summary>
    private const int LogTrimChunk = 200;

    /// <summary>轻量模式下的日志上限：窗口不可见，留一小段尾巴足够事后追溯。</summary>
    private const int LightweightLogLines = 100;

    /// <summary>进程退出后等输出管道收尾的时间，避免最后几行 FATAL 漏显。</summary>
    private const int CoreExitLogDrainMs = 120;

    private readonly DispatcherQueue _dispatcher;
    private readonly SingBoxService _service;
    private readonly AppSettingsService _settings;
    private readonly ProfileManagerService _profiles;
    private readonly SystemProxyService _systemProxy = new();

    /// <summary>内核输出缓冲区：后台线程只入队，UI 线程批量落盘。</summary>
    private readonly ConcurrentQueue<string> _pendingCoreLogs = new();

    /// <summary>
    /// 最近若干行内核输出（只保留尾部）。用来看退出原因：
    /// 例如 cache.db 被别的进程锁住时，内核会打印 "initialize cache-file: timeout" 然后退出 1。
    /// </summary>
    private readonly Queue<string> _coreOutputTail = new();

    private const int CoreOutputTailCapacity = 40;

    private readonly object _coreOutputTailGate = new();

    /// <summary>
    /// 上次因 cache.db 占锁自动重试的时间。用时间而不是布尔量：
    /// 进程"启动成功"只代表 CreateProcess 成功，锁冲突要等退出才知道，
    /// 布尔标记会被反复复位导致重试死循环。
    /// </summary>
    private DateTime _cacheLockRetryAt = DateTime.MinValue;

    /// <summary>唤醒日志泵（最多 1 个待处理信号）。</summary>
    private readonly SemaphoreSlim _logSignal = new(0, 1);

    private readonly CancellationTokenSource _logPumpCts = new();

    /// <summary>当前运行配置里的 mixed/http 端口，系统代理要用。</summary>
    private int _mixedPort;

    // ---- 连接性检测与出口 IP 卡片 ----

    private readonly ConnectivityCheckService _connectivity = new();

    private CancellationTokenSource? _connectivityCts;

    private int _connectivityRunning;

    private string _exitIpRaw = string.Empty;

    private string _exitCountry = string.Empty;

    private string _exitIsp = string.Empty;

    private long _googleMs = -1;

    private long _gitHubMs = -1;

    private long _cloudflareMs = -1;

    private ConnectivityHint _connectivityHint = ConnectivityHint.NotConnected;

    private enum ConnectivityHint
    {
        NotConnected,
        CoreStopped,
        Checking,
        ViaProxy,
        ViaDirect,
        Failed,
        Copied,
    }

    /// <summary>仅供切换代理模式用的 Clash API 客户端（节点页有自己的一份）。</summary>
    private readonly ClashApiClientProvider _clashApi = new();

    /// <summary>热重启互斥：同一时刻只允许一个重启流程。</summary>
    private readonly SemaphoreSlim _restartGate = new(1, 1);

    /// <summary>
    /// 内核"世代号"。每成功拉起一次 +1，用来丢弃上一代进程迟到的 Exited 事件——
    /// 热重启时旧进程的退出回调可能在新内核已经起来之后才跑到，
    /// 不认世代号就会把刚起来的内核状态又按回"未运行"。
    /// </summary>
    private int _runEpoch;

    /// <summary>用空骨架兜底过的规则集，等内核起来后再通过代理重试。</summary>
    private readonly List<string> _stubbedRuleSets = new();

    /// <summary>规则集更新服务（App 初始化后注入，避免 VM 直接依赖静态容器）。</summary>
    public RuleSetUpdateService? RuleSetService { get; set; }

    /// <summary>最近一次成功下发的模式，避免控件重复触发时重复 PATCH。</summary>
    private string? _lastAppliedMode;

    /// <summary>流量/内存长连接的取消源。</summary>
    private CancellationTokenSource? _telemetryCts;

    private bool _isShutdown;

    /// <summary>
    /// 被动回写 UI 状态（进程退出、启动失败）时置位，屏蔽 OnIsRunningChanged 里的二次启停，防止死循环重入。
    /// </summary>
    private bool _isUpdatingFromProcess;

    /// <summary>用户主动停止中，期间进程退出属于预期，不再重复打“内核退出”日志。</summary>
    private bool _isStopping;

    /// <summary>程序内部回滚 TUN 开关时置位，屏蔽 OnIsTunEnabledChanged 的二次联动。</summary>
    private bool _isUpdatingTunFromCode;

    /// <summary>程序内部回写系统代理开关时置位，屏蔽 OnIsSystemProxyEnabledChanged 的二次联动。</summary>
    private bool _isUpdatingProxyFromCode;

    /// <summary>热重启中：旧进程退出属于预期，不要把 IsRunning 被动置回 false。</summary>
    private bool _isRestarting;

    /// <summary>
    /// 后台轻量模式中。期间内核输出只进缓冲队列（上限 100 行）、不唤醒日志泵，
    /// 避免每来一行就构造 LogEntry（内含 Brush 引用）并触发 UI 集合通知，
    /// 把刚被 EmptyWorkingSet 换出的页立刻缺页拉回来。
    /// </summary>
    private bool _isInLightweightMode;

    /// <summary>构造完成前不响应属性联动（避免初始值触发日志/回滚）。</summary>
    private bool _isReady;

    /// <summary>两个驱动开关的协调锁：连点排队，每次读最新状态做幂等决策。</summary>
    private readonly SemaphoreSlim _switchGate = new(1, 1);

    /// <summary>当前内核运行配置实际使用的 TUN 值，用于判断是否需要热重启。</summary>
    private bool _appliedTunEnabled;

    public MainViewModel(
        DispatcherQueue dispatcher,
        AppSettingsService settings,
        ProfileManagerService profiles,
        string? initialConfigPath = null)
    {
        _dispatcher = dispatcher;
        _settings = settings;
        _profiles = profiles;
        _service = new SingBoxService();
        _service.OutputReceived += OnCoreOutput;
        _service.Exited += OnCoreExited;

        IsAdmin = CheckIsAdmin();

        // 恢复上次选中的配置与 TUN 开关；没有指定时用当前激活订阅
        var saved = settings.Current;

        // 边界 C：本次是否会自启内核（App.LaunchCore 用同一条件）
        var willAutoStart = saved.AutoStartCore || saved.LastCoreRunning;

        // 绿色版会被移动目录 / 跨机恢复备份，存的绝对路径随时可能失效，
        // 这里统一走"按当前运行时重新定位"的解析，绝不因为换目录就起不来。
        ConfigPath = ResolveConfigPath(initialConfigPath);

        if (!string.Equals(saved.ConfigPath, ConfigPath, StringComparison.OrdinalIgnoreCase))
        {
            saved.ConfigPath = ConfigPath;
            settings.Save();
        }

        // 激活订阅变化 / 重命名 / 内容更新时，同步刷新标题栏与主页订阅卡片
        profiles.ActiveProfileChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(ConfigSummary));
            RefreshSubscriptionInfo();
        };

        profiles.ActiveProfileMetadataChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(ConfigSummary));
            RefreshSubscriptionInfo();
        };

        profiles.ActiveProfileUpdated += (_, _) => RefreshSubscriptionInfo();

        // 内核起来后延迟自动测一次（轻量模式下绝不自动发起，避免摸热内存页）
        KernelStarted += (_, _) => ScheduleConnectivityCheckAfterStart();

        // 语言切换后，代码里拼出来的状态文案要跟着重算
        LocalizationService.Instance.LanguageChanged += OnLanguageChanged;

        // 非管理员不可能建 TUN 网卡：内存里强制关掉，但不覆盖磁盘上的偏好
        var tun = saved.IsTunEnabled;
        if (tun && !IsAdmin)
        {
            tun = false;
            AppendLog("[提示] 当前非管理员权限，已自动关闭 TUN 模式开关");
        }

        if (!willAutoStart)
        {
            // 边界 C：本次不自启内核 -> 未运行态，两个驱动开关必须与运行态对齐为 false。
            // 只改内存显示，磁盘偏好保留，供下次开启自启时按原组合恢复。
            tun = false;
            IsTunEnabled = false;
            IsSystemProxyEnabled = false;
        }
        else
        {
            // 自启：按上次开启的代理 / TUN 组合恢复，AutoStartCoreAsync 负责兜底空组合
            IsTunEnabled = tun;
            IsSystemProxyEnabled = saved.IsSystemProxyEnabled;
        }
        ClashModeIndex = saved.ClashMode?.Trim().ToLowerInvariant() switch
        {
            "global" => 1,
            "direct" => 2,
            _ => 0,
        };

        AppendLog($"内核目录：{_service.CoreDirectory}");
        AppendLog(IsAdmin ? "当前已具备管理员权限，TUN 模式可用。" : "当前非管理员，TUN 模式需要提权。");

        // 上次被强杀/崩溃时系统代理可能没来得及关，启动时兜底清掉，避免浏览器指向死端口
        if (!string.IsNullOrEmpty(saved.LastProxyServer) && _systemProxy.IsEnabledWith(saved.LastProxyServer))
        {
            _systemProxy.Disable();
            AppendLog("[代理] 检测到上次异常退出遗留的系统代理，已自动关闭。");
        }

        _isReady = true;

        // 内核日志批量落盘的消费循环：在 UI 线程启动，续体回到 UI 线程
        _ = RunLogPumpAsync(_logPumpCts.Token);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditSettings))]
    [NotifyPropertyChangedFor(nameof(StatusTitle))]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsToggleEnabled))]
    [NotifyPropertyChangedFor(nameof(CanEditSettings))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = LocalizationService.Instance.StatusStopped;

    /// <summary>概览页大号状态标题：运行中按当前开启的代理 / TUN 组合显示。</summary>
    public string StatusTitle => LocalizationService.Instance.StatusTitle(
        IsRunning, IsSystemProxyEnabled, IsTunEnabled);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConfigFileName))]
    [NotifyPropertyChangedFor(nameof(ConfigSummary))]
    public partial string ConfigPath { get; set; } = string.Empty;

    /// <summary>TUN 虚拟网卡模式。驱动内核启停的核心开关之一。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusTitle))]
    public partial bool IsTunEnabled { get; set; }

    /// <summary>系统代理。驱动内核启停的核心开关之一，运行中立即写注册表。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusTitle))]
    public partial bool IsSystemProxyEnabled { get; set; }

    /// <summary>0 = 规则(Rule) / 1 = 全局(Global) / 2 = 直连(Direct)。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ClashMode))]
    [NotifyPropertyChangedFor(nameof(ClashModeDisplay))]
    public partial int ClashModeIndex { get; set; }

    /// <summary>给 API 与配置用的大小写形式。</summary>
    public string ClashMode => ClashModeIndex switch
    {
        1 => "Global",
        2 => "Direct",
        _ => "Rule",
    };

    public string ClashModeDisplay => ClashModeIndex switch
    {
        1 => LocalizationService.Instance.HomeModeGlobal,
        2 => LocalizationService.Instance.HomeModeDirect,
        _ => LocalizationService.Instance.HomeModeRule,
    };

    // ---- 主页「网络连通性与出口信息」卡片 ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExitIpText))]
    [NotifyPropertyChangedFor(nameof(IpMaskGlyph))]
    [NotifyPropertyChangedFor(nameof(IpMaskHint))]
    public partial bool IsIpMasked { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCheckConnectivity))]
    public partial bool IsCheckingConnectivity { get; set; }

    public bool CanCheckConnectivity => !IsCheckingConnectivity;

    /// <summary>IPv4 隐藏中间两段（104.28.***.***）；IPv6 只保留前两组。</summary>
    public string ExitIpText
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_exitIpRaw))
            {
                return LocalizationService.Instance.ConnNoIp;
            }

            if (!IsIpMasked)
            {
                return _exitIpRaw;
            }

            if (_exitIpRaw.Contains(':', StringComparison.Ordinal))
            {
                var groups = _exitIpRaw.Split(':');
                return groups.Length > 2 ? $"{groups[0]}:{groups[1]}:****:****" : "****";
            }

            var parts = _exitIpRaw.Split('.');
            return parts.Length == 4 ? $"{parts[0]}.{parts[1]}.***.***" : "***";
        }
    }

    public string ExitRegionText => LocalizationService.Instance.ConnRegionText(_exitCountry, _exitIsp);

    public string ConnectivityHintText => _connectivityHint switch
    {
        ConnectivityHint.Checking => LocalizationService.Instance.ConnChecking,
        ConnectivityHint.ViaProxy => LocalizationService.Instance.ConnViaProxy,
        ConnectivityHint.ViaDirect => LocalizationService.Instance.ConnViaDirect,
        ConnectivityHint.Failed => LocalizationService.Instance.ConnFailed,
        ConnectivityHint.Copied => LocalizationService.Instance.ConnCopied,
        ConnectivityHint.CoreStopped => LocalizationService.Instance.ConnCoreStopped,
        _ => LocalizationService.Instance.ConnNotConnected,
    };

    public string GoogleLatencyText => LocalizationService.Instance.ConnLatencyText(_googleMs);

    public string GitHubLatencyText => LocalizationService.Instance.ConnLatencyText(_gitHubMs);

    public string CloudflareLatencyText => LocalizationService.Instance.ConnLatencyText(_cloudflareMs);

    public Brush GoogleLatencyBrush => LatencyBrush(_googleMs);

    public Brush GitHubLatencyBrush => LatencyBrush(_gitHubMs);

    public Brush CloudflareLatencyBrush => LatencyBrush(_cloudflareMs);

    public string IpMaskGlyph => IsIpMasked ? "\uE890" : "\uE7B3";

    public string IpMaskHint => IsIpMasked
        ? LocalizationService.Instance.ConnShowIp
        : LocalizationService.Instance.ConnHideIp;

    private static Brush LatencyBrush(long ms)
    {
        if (ms < 0)
        {
            return ThemeBrushHelper.Get("SystemFillColorCriticalBrush", Color.FromArgb(255, 196, 43, 28));
        }

        if (ms < 250)
        {
            return ThemeBrushHelper.Get("SystemFillColorSuccessBrush", Color.FromArgb(255, 15, 123, 15));
        }

        return ThemeBrushHelper.Get("SystemFillColorCautionBrush", Color.FromArgb(255, 157, 93, 0));
    }

    [RelayCommand]
    private async Task CheckConnectivity()
    {
        if (Interlocked.Exchange(ref _connectivityRunning, 1) == 1)
        {
            return;
        }

        _connectivityCts?.Cancel();
        var cts = new CancellationTokenSource();
        _connectivityCts = cts;

        _dispatcher.TryEnqueue(() =>
        {
            IsCheckingConnectivity = true;
            SetConnectivityHint(ConnectivityHint.Checking);
        });

        try
        {
            // 内核没跑就直连探测（用户手动点也允许），否则借道本地 mixed 端口
            int? port = IsRunning && MixedPort > 0 ? MixedPort : null;
            var result = await _connectivity.CheckAsync(port, cts.Token);
            _dispatcher.TryEnqueue(() => ApplyConnectivityResult(result));
        }
        catch (OperationCanceledException)
        {
            // 停止 / 退出时取消，保持当前结果
        }
        catch (Exception ex)
        {
            _dispatcher.TryEnqueue(() =>
            {
                SetConnectivityHint(ConnectivityHint.Failed);
                AppendLog($"[连通性] 检测失败：{ex.Message}");
            });
        }
        finally
        {
            _dispatcher.TryEnqueue(() => IsCheckingConnectivity = false);
            Interlocked.Exchange(ref _connectivityRunning, 0);
        }
    }

    [RelayCommand]
    private void ToggleIpMask() => IsIpMasked = !IsIpMasked;

    [RelayCommand]
    private void CopyExitIp()
    {
        if (string.IsNullOrWhiteSpace(_exitIpRaw))
        {
            return;
        }

        try
        {
            var package = new DataPackage();
            package.SetText(_exitIpRaw);
            Clipboard.SetContent(package);
            SetConnectivityHint(ConnectivityHint.Copied);
        }
        catch (Exception ex)
        {
            AppendLog($"[连通性] 复制出口 IP 失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 内核启动成功后自动检测。轻量模式下直接放弃：此时工作集刚被压下去，
    /// 任何后台网络请求都会把内存页重新摸热。
    /// </summary>
    private void ScheduleConnectivityCheckAfterStart()
    {
        if (_isInLightweightMode)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(800);
            if (_isInLightweightMode || _isShutdown || !IsRunning)
            {
                return;
            }

            await CheckConnectivity();
        });
    }

    private void ApplyConnectivityResult(ConnectivityResult result)
    {
        _exitIpRaw = result.ExitIp?.Ip ?? string.Empty;
        _exitCountry = result.ExitIp?.Country ?? string.Empty;
        _exitIsp = result.ExitIp?.Isp ?? string.Empty;
        _googleMs = result.GoogleMs;
        _gitHubMs = result.GitHubMs;
        _cloudflareMs = result.CloudflareMs;

        SetConnectivityHint(result.ExitIp is null
            ? ConnectivityHint.Failed
            : result.ViaProxy ? ConnectivityHint.ViaProxy : ConnectivityHint.ViaDirect);
        RaiseConnectivityChanged();
    }

    private void ResetConnectivityState()
    {
        _connectivityCts?.Cancel();
        _exitIpRaw = string.Empty;
        _exitCountry = string.Empty;
        _exitIsp = string.Empty;
        _googleMs = -1;
        _gitHubMs = -1;
        _cloudflareMs = -1;
        SetConnectivityHint(ConnectivityHint.CoreStopped);
        RaiseConnectivityChanged();
    }

    private void SetConnectivityHint(ConnectivityHint hint)
    {
        _connectivityHint = hint;
        OnPropertyChanged(nameof(ConnectivityHintText));
    }

    private void RaiseConnectivityChanged()
    {
        OnPropertyChanged(nameof(ExitIpText));
        OnPropertyChanged(nameof(ExitRegionText));
        OnPropertyChanged(nameof(ConnectivityHintText));
        OnPropertyChanged(nameof(GoogleLatencyText));
        OnPropertyChanged(nameof(GitHubLatencyText));
        OnPropertyChanged(nameof(CloudflareLatencyText));
        OnPropertyChanged(nameof(GoogleLatencyBrush));
        OnPropertyChanged(nameof(GitHubLatencyBrush));
        OnPropertyChanged(nameof(CloudflareLatencyBrush));
        OnPropertyChanged(nameof(IpMaskHint));
        OnPropertyChanged(nameof(IpMaskGlyph));
    }

    // ---- 主页「当前订阅信息」卡片 ----

    public ProfileItem? ActiveProfile => _profiles.ActiveProfile;

    public string SubscriptionName => ActiveProfile?.Name ?? LocalizationService.Instance.NoProfileSelected();

    public string SubscriptionTypeText => ActiveProfile?.IsRemote == true
        ? LocalizationService.Instance.HomeRemoteProfile
        : LocalizationService.Instance.HomeLocalProfile;

    public string SubscriptionUpdateText => ActiveProfile?.LastUpdated is { } updated
        ? updated.LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture)
        : LocalizationService.Instance.HomeLocalProfile;

    public string SubscriptionExpireText
    {
        get
        {
            var profile = ActiveProfile;

            if (profile?.ExpireTimestamp is { } unix and > 0)
            {
                var expire = DateTimeOffset.FromUnixTimeSeconds(unix).LocalDateTime;
                var days = (expire.Date - DateTime.Today).Days;
                var text = expire.ToString("yyyy-MM-dd", CultureInfo.CurrentCulture);
                return days >= 0
                    ? LocalizationService.Instance.ExpiresOn(text, days)
                    : LocalizationService.Instance.ExpiredOn(text);
            }

            return string.IsNullOrEmpty(profile?.ParsedExpireHint)
                ? LocalizationService.Instance.ExpireUnknown()
                : profile!.ParsedExpireHint;
        }
    }

    /// <summary>已用 / 总流量；只有"剩余 X GB"这类兜底信息时直接展示提示文本。</summary>
    public string SubscriptionTrafficText
    {
        get
        {
            var profile = ActiveProfile;
            if (profile is null)
            {
                return "—";
            }

            var used = (profile.UploadBytes ?? 0) + (profile.DownloadBytes ?? 0);

            if (profile.TotalBytes is > 0)
            {
                return $"{FormatHelper.FormatBytes(used)} / {FormatHelper.FormatBytes(profile.TotalBytes.Value)}";
            }

            if (!string.IsNullOrEmpty(profile.ParsedTrafficHint))
            {
                return profile.ParsedTrafficHint;
            }

            return used > 0
                ? LocalizationService.Instance.UsedAmount(FormatHelper.FormatBytes(used))
                : "—";
        }
    }

    public double SubscriptionUsagePercent
    {
        get
        {
            var profile = ActiveProfile;
            if (profile?.TotalBytes is not > 0)
            {
                return 0;
            }

            var used = (profile.UploadBytes ?? 0) + (profile.DownloadBytes ?? 0);
            return Math.Clamp(used * 100.0 / profile.TotalBytes.Value, 0, 100);
        }
    }

    public string SubscriptionUsagePercentText => $"{SubscriptionUsagePercent:0.0}%";

    /// <summary>有真实总量时才显示进度条。</summary>
    public bool HasSubscriptionTraffic => ActiveProfile?.TotalBytes is > 0;

    public bool CanUpdateSubscription => ActiveProfile?.IsRemote == true;

    private void RefreshSubscriptionInfo()
    {
        OnPropertyChanged(nameof(ActiveProfile));
        OnPropertyChanged(nameof(SubscriptionName));
        OnPropertyChanged(nameof(SubscriptionTypeText));
        OnPropertyChanged(nameof(SubscriptionUpdateText));
        OnPropertyChanged(nameof(SubscriptionExpireText));
        OnPropertyChanged(nameof(SubscriptionTrafficText));
        OnPropertyChanged(nameof(SubscriptionUsagePercent));
        OnPropertyChanged(nameof(SubscriptionUsagePercentText));
        OnPropertyChanged(nameof(HasSubscriptionTraffic));
        OnPropertyChanged(nameof(CanUpdateSubscription));
    }

    /// <summary>侧栏底部状态块：实时上传速度。</summary>
    [ObservableProperty]
    public partial string UploadSpeedText { get; set; } = "0 B/s";

    /// <summary>侧栏底部状态块：实时下载速度。</summary>
    [ObservableProperty]
    public partial string DownloadSpeedText { get; set; } = "0 B/s";

    /// <summary>侧栏底部状态块：内核内存占用。</summary>
    [ObservableProperty]
    public partial string MemoryUsageText { get; set; } = "—";

    /// <summary>是否以管理员身份运行（TUN 必需）。</summary>
    public bool IsAdmin { get; }

    /// <summary>用户手动关掉了非管理员提示条。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowAdminHint))]
    public partial bool IsAdminHintDismissed { get; set; }

    public bool ShowAdminHint => !IsAdmin && !IsAdminHintDismissed;

    public ObservableCollection<LogEntry> Logs { get; } = new();

    /// <summary>内核开关与 TUN 开关共用：运行中也可拨动（会触发热重启），只在忙碌时禁用。</summary>
    public bool IsToggleEnabled => !IsBusy;

    /// <summary>运行中或忙碌时不允许改配置 / 改 TUN 开关 / 提权。</summary>
    public bool CanEditSettings => !IsBusy && !IsRunning;

    public string ConfigFileName =>
        string.IsNullOrEmpty(ConfigPath)
            ? LocalizationService.Instance.NotSelected()
            : Path.GetFileName(ConfigPath);

    public string ConfigSummary => _profiles.ActiveProfile is { } profile
        ? LocalizationService.Instance.ProfileSummary(profile.Name, profile.NodeCountText)
        : LocalizationService.Instance.ConfigSummary(ConfigFileName);

    partial void OnIsRunningChanged(bool value)
    {
        StatusText = value
            ? LocalizationService.Instance.StatusRunning
            : LocalizationService.Instance.StatusStopped;

        if (!value)
        {
            ResetConnectivityState();
        }

        // 来自进程侧的被动回写，不再反向触发启停
        if (_isUpdatingFromProcess)
        {
            return;
        }

        _ = HandleToggleAsync(value);
    }

    /// <summary>TUN 开关：交给统一协调器（守卫屏蔽代码回写造成的循环重入）。</summary>
    partial void OnIsTunEnabledChanged(bool value)
    {
        if (!_isReady || _isUpdatingTunFromCode)
        {
            return;
        }

        _ = ReconcileSwitchesAsync();
    }

    /// <summary>系统代理开关：交给统一协调器（守卫屏蔽代码回写造成的循环重入）。</summary>
    partial void OnIsSystemProxyEnabledChanged(bool value)
    {
        if (!_isReady || _isUpdatingProxyFromCode)
        {
            return;
        }

        _ = ReconcileSwitchesAsync();
    }

    /// <summary>
    /// 开关驱动内核的唯一决策点。每次执行都读取「当前」两个开关的最新值，
    /// 因此连点会自然合并到最终状态；不设 IsBusy，热重启期间开关保持可点。
    ///
    /// 5 个分支：
    ///   ① 全关 -> 开系统代理：冷启动 StartCoreAsync（成功后自动挂代理）
    ///   ② 全关 -> 开 TUN：非管理员落盘 + UAC 提权 / 取消则回滚；管理员直接冷启动
    ///   ③ 运行中切 TUN：至少一开，热重启 RestartCoreAsync 注入或移除 TUN 入站
    ///   ④ 运行中只切系统代理：mixed 端口已就绪，Apply / Clear 秒级生效，不重启
    ///   ⑤ 两个全关：StopCoreAsync 停内核并清理系统代理 / 卸载 TUN
    /// </summary>
    private async Task ReconcileSwitchesAsync()
    {
        await _switchGate.WaitAsync();
        try
        {
            if (_isShutdown)
            {
                return;
            }

            var proxy = IsSystemProxyEnabled;
            var tun = IsTunEnabled;

            // ② 非管理员开 TUN：落盘意图 + 提权重启；取消则把开关拨回
            if (tun && !IsAdmin)
            {
                _settings.Current.IsTunEnabled = true;
                _settings.Current.LastCoreRunning = true;
                _settings.Save();

                AppendLog("[提示] TUN 模式需要管理员权限，正在申请提权重启…");
                if (RestartAsAdmin())
                {
                    return; // 提权实例接管，本实例即将退出
                }

                AppendLog("[提示] 已取消管理员提权，TUN 开关已拨回关闭。");
                SetTunEnabledFromCode(false);
                _settings.Current.IsTunEnabled = false;
                _settings.Save();
                tun = false;
                proxy = IsSystemProxyEnabled;
            }
            else
            {
                // 两个开关都是「意图」，落盘供下次启动恢复
                _settings.Current.IsSystemProxyEnabled = proxy;
                _settings.Current.IsTunEnabled = tun;
                if (tun && IsAdmin)
                {
                    _settings.Current.AlwaysRunAsAdmin = true;
                }

                _settings.Save();
            }

            var wantRunning = proxy || tun;

            // ⑤ 全关：停内核
            if (!wantRunning)
            {
                AppendLog("[状态] 系统代理与 TUN 均已关闭，正在停止内核…");
                ClearSystemProxy();

                if (IsRunning || _service.IsRunning)
                {
                    await StopCoreAsync();
                }

                _settings.Current.LastCoreRunning = false;
                _settings.Save();
                SetRunningFromProcess(false);
                return;
            }

            // ① ② 冷启动（此前内核未运行）
            if (!IsRunning)
            {
                AppendLog(proxy
                    ? "[状态] 已开启系统代理，正在启动内核…"
                    : "[状态] 已开启 TUN 模式，正在启动内核…");

                // 用守卫置位：屏蔽 OnIsRunningChanged 里的 HandleToggleAsync，避免重复启动
                SetRunningFromProcess(true);
                await StartCoreAsync();
                // 启动失败由 Fail() 回滚两个开关（边界 A）
                return;
            }

            // ③ 运行中 TUN 组合变化：重建运行配置，热重启
            if (tun != _appliedTunEnabled)
            {
                await RestartCoreAsync("[状态] 正在切换 TUN 模式并重启内核...");
                return;
            }

            // ④ 运行中只切系统代理：mixed 端口已就绪，秒级生效
            if (proxy)
            {
                ApplySystemProxy();
            }
            else
            {
                ClearSystemProxy();
            }
        }
        catch (Exception ex)
        {
            Fail($"开关切换失败：{ex.Message}");
        }
        finally
        {
            _switchGate.Release();
        }
    }

    /// <summary>程序内部回写系统代理开关，屏蔽二次联动。</summary>
    private void SetSystemProxyFromCode(bool value)
    {
        if (IsSystemProxyEnabled == value)
        {
            return;
        }

        _isUpdatingProxyFromCode = true;
        try
        {
            IsSystemProxyEnabled = value;
        }
        finally
        {
            _isUpdatingProxyFromCode = false;
        }
    }

    /// <summary>代理模式变化：运行中实时 PATCH /configs，否则下次启动生效。</summary>
    partial void OnClashModeIndexChanged(int value)
    {
        if (!_isReady)
        {
            return;
        }

        _settings.Current.ClashMode = ClashMode;
        _settings.Save();

        if (!IsRunning)
        {
            AppendLog($"[模式] 已设为：{ClashModeDisplay}（将在下次启动内核时生效）");
            return;
        }

        _ = ApplyClashModeAsync();
    }

    /// <summary>托盘菜单用：切换系统代理偏好。</summary>
    public void ToggleSystemProxy() => IsSystemProxyEnabled = !IsSystemProxyEnabled;

    /// <summary>托盘菜单用：系统代理当前是否真的生效。</summary>
    public bool IsSystemProxyOn => _systemProxy.IsEnabled;

    /// <summary>托盘菜单用：按名字设置代理模式。</summary>
    public void SetClashModeByName(string mode) => ClashModeIndex = mode?.Trim().ToLowerInvariant() switch
    {
        "global" => 1,
        "direct" => 2,
        _ => 0,
    };

    private void ApplySystemProxy()
    {
        // 开关没开就绝不碰注册表：主开关只负责启停内核
        if (!IsSystemProxyEnabled)
        {
            return;
        }

        if (_mixedPort <= 0)
        {
            AppendLog("[代理] 未找到 mixed/http 入站端口，无法开启系统代理。");
            return;
        }

        var server = $"127.0.0.1:{_mixedPort}";
        if (_systemProxy.Enable(server))
        {
            // 记下来，下次启动能识别"上次没关掉"的残留
            _settings.Current.LastProxyServer = server;
            _settings.Save();
            AppendLog($"[代理] 系统代理已开启：{server}");
        }
        else
        {
            AppendLog("[错误] 系统代理写入注册表失败。");
        }
    }

    private void ClearSystemProxy()
    {
        if (!_systemProxy.IsEnabled)
        {
            return;
        }

        if (_systemProxy.Disable())
        {
            AppendLog("[代理] 系统代理已关闭。");
        }
    }

    /// <summary>
    /// 内核已不在（崩溃 / 启动失败 / 用户停止）时统一摘掉系统代理：
    /// 关注册表 -> 开关回落。三处收尾共用，避免只做一半留下死角。
    /// </summary>
    private void DetachSystemProxy()
    {
        ClearSystemProxy();

        if (!IsSystemProxyEnabled)
        {
            return;
        }

        // 内核停了代理必然失效，开关跟着回落到关闭，避免开关与实际状态不一致
        SetSystemProxyFromCode(false);
    }

    private async Task ApplyClashModeAsync()
    {
        // 单选控件可能连发两次变更，已经下发过的模式直接跳过
        if (string.Equals(_lastAppliedMode, ClashMode, StringComparison.Ordinal))
        {
            return;
        }

        var api = EnsureConfigApi();
        if (api is null)
        {
            return;
        }

        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                await api.SetClashModeAsync(ClashMode);
                _lastAppliedMode = ClashMode;
                AppendLog($"[模式] 已切换至：{ClashModeDisplay}");
                return;
            }
            catch (Exception) when (attempt < 4)
            {
                // clash_api 可能还没监听，稍等重试
                await Task.Delay(300);
            }
            catch (Exception ex)
            {
                AppendLog($"[错误] 切换代理模式失败：{ex.Message}");
                return;
            }
        }
    }

    /// <summary>内核起来后开两条长连接：/traffic 与 /memory。</summary>
    private void StartTelemetry()
    {
        // 轻量模式期间一律不开流：内核热重启（补规则集等）会走到这里，
        // 窗口还藏着就重新收包，等于把刚释放的内存又买回来。
        if (_isInLightweightMode)
        {
            return;
        }

        StopTelemetry();

        var api = EnsureConfigApi();
        if (api is null)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _telemetryCts = cts;
        var ct = cts.Token;

        _ = Task.Run(
            () => api.StreamTrafficAsync((up, down) => _dispatcher.TryEnqueue(() =>
            {
                if (ct.IsCancellationRequested)
                {
                    return;
                }

                UploadSpeedText = FormatHelper.FormatSpeed(up);
                DownloadSpeedText = FormatHelper.FormatSpeed(down);
            }), ct),
            ct);

        // 内存走"任务管理器口径"：直接读内核进程的 WorkingSet，
        // 而不是 /memory 的 inuse（Go 堆在用内存，不含未归还给系统的部分）
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(1000, ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                var pid = _service.ProcessId;
                if (pid is null)
                {
                    continue;
                }

                try
                {
                    using var process = System.Diagnostics.Process.GetProcessById(pid.Value);
                    var workingSet = process.WorkingSet64;
                    _dispatcher.TryEnqueue(() =>
                    {
                        if (!ct.IsCancellationRequested)
                        {
                            MemoryUsageText = FormatHelper.FormatBytes(workingSet);
                        }
                    });
                }
                catch (Exception)
                {
                    // 进程刚好退出，下一轮再取
                }
            }
        }, ct);
    }

    /// <summary>恢复备份后重新对齐设置相关的 UI 状态（不触发联动副作用）。</summary>
    public void RefreshFromSettings()
    {
        var current = _settings.Current;

        _isReady = false;
        try
        {
            ClashModeIndex = current.ClashMode?.ToLowerInvariant() switch
            {
                "global" => 1,
                "direct" => 2,
                _ => 0,
            };
            IsTunEnabled = current.IsTunEnabled && IsAdmin;
            IsSystemProxyEnabled = current.IsSystemProxyEnabled;
            _appliedTunEnabled = IsTunEnabled && IsRunning;

            // 备份恢复 / 移动目录后 ConfigPath 可能指向旧路径：统一走重定位。
            // _isReady=false 保证这里不触发落盘。
            ConfigPath = ResolveConfigPath(null);
        }
        finally
        {
            _isReady = true;
        }

        OnPropertyChanged(nameof(ConfigSummary));
        RefreshSubscriptionInfo();
    }

    /// <summary>语言切换后重算所有"由代码拼出来"的展示文案。</summary>
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        StatusText = IsRunning
            ? LocalizationService.Instance.StatusRunning
            : LocalizationService.Instance.StatusStopped;

        OnPropertyChanged(nameof(StatusTitle));
        OnPropertyChanged(nameof(ClashModeDisplay));
        OnPropertyChanged(nameof(ConfigFileName));
        OnPropertyChanged(nameof(ConfigSummary));
        RefreshSubscriptionInfo();
        RaiseConnectivityChanged();
    }

    /// <summary>本地 mixed 入站端口，规则集下载借道它走代理。</summary>
    public int MixedPort => _mixedPort;

    /// <summary>
    /// 内核进程是否真的存活。
    /// 注意不能用 IsRunning：启动流程里它会被提前置 true（此时进程还没起来），
    /// 拿它判断"能不能安全覆盖规则集"会把刚需的文件写进待更新队列。
    /// </summary>
    public bool IsKernelProcessAlive => _service.ProcessId is not null;

    /// <summary>内核进程 Id（未运行时为 null），供轻量模式连带修剪子进程工作集。</summary>
    public int? KernelProcessId => _service.ProcessId;

    /// <summary>是否处于后台轻量模式。</summary>
    public bool IsInLightweightMode => _isInLightweightMode;

    /// <summary>
    /// 进入轻量模式：真正断掉遥测长连接、裁剪日志与日志缓冲、冻结日志泵。
    /// 必须在 UI 线程调用（会改动 Logs 集合）。
    /// </summary>
    public void EnterLightweightMode()
    {
        _isInLightweightMode = true;

        // 自动探测属于后台网络活动，进轻量模式前把在途请求一并掐掉
        _connectivityCts?.Cancel();

        // 1) 真的 Cancel 掉 /traffic 流与内存轮询，而不只是不刷 UI：
        //    流没断的话 HttpClient 每秒仍在收包、每秒仍在分配 JSON 对象。
        StopTelemetry();

        // 2) UI 集合裁到 100 条，把 LogEntry（含 Brush）与 ListView 里的视觉元素一起放掉
        TrimLogs(LightweightLogLines);

        // 3) 缓冲队列同样收到 100 行，避免后台输出把队列撑到几十万条
        TrimPendingLogs(LightweightLogLines);
    }

    /// <summary>退出轻量模式：解冻日志泵并把缓冲的日志一次性刷进 UI，然后恢复遥测。</summary>
    public void ExitLightweightMode()
    {
        var wasLightweight = _isInLightweightMode;
        _isInLightweightMode = false;

        if (wasLightweight)
        {
            // 缓冲里是轻量期间的最新 100 行，恢复显示时补上，不能凭空消失
            FlushPendingLogs();
        }

        ResumeTelemetry();
    }

    /// <summary>轻量模式：暂停实时遥测。</summary>
    public void SuspendTelemetry() => StopTelemetry();

    /// <summary>轻量模式结束：内核在跑就重新开流。</summary>
    public void ResumeTelemetry()
    {
        // 轻量模式期间内核若热重启过，StartTelemetry 会被再次调用；
        // 此时必须拒绝开流，否则窗口还藏着就又开始每秒收包。
        if (_isInLightweightMode)
        {
            return;
        }

        if (IsRunning)
        {
            StartTelemetry();
        }
    }

    /// <summary>把日志裁剪到最近 N 条，给轻量模式释放内存用。</summary>
    public void TrimLogs(int keep)
    {
        while (Logs.Count > keep)
        {
            Logs.RemoveAt(0);
        }
    }

    /// <summary>把内核输出的待落盘缓冲裁到最近 N 行（并发队列，只丢最旧的）。</summary>
    private void TrimPendingLogs(int keep)
    {
        while (_pendingCoreLogs.Count > keep && _pendingCoreLogs.TryDequeue(out _))
        {
            // 丢最旧的
        }
    }

    /// <summary>收掉长连接并把展示值复位，绝不残留旧数字。</summary>
    private void StopTelemetry()
    {
        var cts = _telemetryCts;
        _telemetryCts = null;

        if (cts is not null)
        {
            // 不 Dispose：流内部可能还持有 token 注册，交给 GC 更安全
            try
            {
                cts.Cancel();
            }
            catch (Exception)
            {
                // 已释放等，忽略
            }
        }

        UploadSpeedText = "0 B/s";
        DownloadSpeedText = "0 B/s";
        MemoryUsageText = "—";
    }

    private ClashApiService? EnsureConfigApi()
    {
        if (string.IsNullOrWhiteSpace(ClashController))
        {
            AppendLog("[错误] 尚未取得 clash_api 地址，无法切换模式。");
            return null;
        }

        return _clashApi.Get(ClashController, ClashSecret);
    }

    /// <summary>
    /// 解析当前可用的配置文件路径。存的虽是绝对路径，但每次使用前都会重新定位：
    /// (1) 路径有效直接用 -> (2) 当前激活订阅 -> (3) Core 下同名文件
    /// -> (4) 首个订阅 -> (5) Core 默认 config.json。
    /// 移动软件目录、或把备份恢复到另一台路径不同的机器，都不会再报"找不到配置文件"。
    /// </summary>
    private string ResolveConfigPath(string? initialConfigPath)
    {
        if (!string.IsNullOrWhiteSpace(initialConfigPath) && File.Exists(initialConfigPath))
        {
            return initialConfigPath;
        }

        var coreDirectory = _service.CoreDirectory;
        var stored = _settings.Current.ConfigPath;

        // 相对路径：按当前 Core 目录解析（最抗迁移的存法）
        if (!string.IsNullOrWhiteSpace(stored) && !Path.IsPathRooted(stored))
        {
            var combined = Path.GetFullPath(Path.Combine(coreDirectory, stored));
            if (File.Exists(combined))
            {
                return combined;
            }
        }

        // 绝对路径且仍然有效
        if (!string.IsNullOrWhiteSpace(stored) && File.Exists(stored))
        {
            return stored;
        }

        // (2) 当前激活订阅：ProfileItem 只存 FileName，拼出来的路径天然跟随当前运行目录
        if (_profiles.ActiveProfilePath is { } activePath && File.Exists(activePath))
        {
            NoteConfigRelocated(stored, activePath);
            return activePath;
        }

        // (3) 按文件名在 Core 的 profiles 子目录与 Core 根下找同名文件
        var fileName = string.IsNullOrWhiteSpace(stored) ? string.Empty : Path.GetFileName(stored);
        if (!string.IsNullOrEmpty(fileName))
        {
            foreach (var candidate in new[]
                     {
                         Path.Combine(coreDirectory, "profiles", fileName),
                         Path.Combine(coreDirectory, fileName),
                     })
            {
                if (File.Exists(candidate))
                {
                    NoteConfigRelocated(stored, candidate);
                    return candidate;
                }
            }
        }

        // (4) 首个订阅，再退到 Core 默认配置
        var first = _profiles.Profiles.FirstOrDefault();
        if (first is not null)
        {
            var firstPath = _profiles.GetProfilePath(first);
            if (File.Exists(firstPath))
            {
                NoteConfigRelocated(stored, firstPath);
                return firstPath;
            }
        }

        return _service.DefaultConfigPath;
    }

    private void NoteConfigRelocated(string? oldPath, string newPath)
    {
        if (string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        AppendLog($"[配置] 原配置路径已失效，已自动重定位到：{newPath}");
    }

    private void PersistTunState(bool value)
    {
        _settings.Current.IsTunEnabled = value;

        // 已经是管理员还开着 TUN：说明用户就是要常驻管理员，下次启动直接自动提权
        if (value && IsAdmin)
        {
            _settings.Current.AlwaysRunAsAdmin = true;
        }

        _settings.Save();
    }

    /// <summary>程序内部回写 TUN 开关，屏蔽 OnIsTunEnabledChanged 的二次联动。</summary>
    private void SetTunEnabledFromCode(bool value)
    {
        if (IsTunEnabled == value)
        {
            return;
        }

        _isUpdatingTunFromCode = true;
        try
        {
            IsTunEnabled = value;
        }
        finally
        {
            _isUpdatingTunFromCode = false;
        }
    }

    /// <summary>重建运行配置 -> check -> 优雅停止旧进程 -> 启动新进程。</summary>
    public async Task RestartCoreAsync(string statusMessage)
    {
        // 热重启有 6 个入口（TUN 开关 / 切换订阅 / 订阅后台更新 / IPv6 / Mixin 保存 / 备份恢复）。
        // 不加锁会互相踩：后一个 StopAsync 掉前一个刚启动的进程，甚至同时拉起两个内核。
        if (!await _restartGate.WaitAsync(0))
        {
            AppendLog("[提示] 正在重启内核，已忽略重复的重启请求。");
            return;
        }

        StopTelemetry();
        _isRestarting = true;

        // 注意：这里刻意不置 IsBusy。IsBusy 会让「系统代理 / TUN」开关在重启的
        // 两三秒里变成灰显禁用，用户看到的就是"拨一下闪一下"。防连点靠 _restartGate。
        try
        {
            AppendLog(statusMessage);

            // ① 停旧内核
            AppendLog("[重启] ① 正在停止旧内核…");
            _isStopping = true;
            try
            {
                await _service.StopAsync();
            }
            finally
            {
                _isStopping = false;
            }

            // ② 重新生成配置 → ③ 校验 → ④ 启动（都在 StartCoreAsync 里）
            await StartCoreAsync();

            if (_service.IsRunning)
            {
                AppendLog("[重启] ④ 新内核已启动，热重启完成。");
            }
            else
            {
                // 走到这里说明启动环节被提前 return 或失败，内核现在是停着的
                AppendLog("[错误] 热重启后内核未运行，请查看上方日志定位原因。");
                SetRunningFromProcess(false);
            }
        }
        catch (Exception ex)
        {
            Fail($"重启内核失败：{ex.Message}");
        }
        finally
        {
            _isRestarting = false;
            _restartGate.Release();
        }
    }

    /// <summary>设置页改完 TUN 参数后调用：运行中则热重启让新参数生效。</summary>
    public Task RestartCoreForTunSettingsAsync() =>
        IsRunning
            ? RestartCoreAsync("[状态] 正在重启内核以应用 TUN 网卡设置...")
            : Task.CompletedTask;

    /// <summary>用户主动切换配置文件（选择器在 View 层，这里只接收结果）。</summary>
    public void SetConfigPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        ConfigPath = path;
        AppendLog($"已切换配置文件：{path}");
    }

    partial void OnConfigPathChanged(string value)
    {
        if (!_isReady)
        {
            return;
        }

        _settings.Current.ConfigPath = value;
        _settings.Save();
    }

    /// <summary>开机自启时的自动启动内核（边界 C 的空组合兜底在这里）。</summary>
    public async Task AutoStartCoreAsync()
    {
        await Task.Yield();

        if (IsRunning)
        {
            return;
        }

        // 边界 C：开了自动启动但上次没留下任何开启项 -> 默认启用系统代理
        if (!IsSystemProxyEnabled && !IsTunEnabled)
        {
            SetSystemProxyFromCode(true);
            _settings.Current.IsSystemProxyEnabled = true;
            _settings.Save();
            AppendLog("[状态] 开机自启：未记录代理方式，已默认启用系统代理。");
        }

        // 非管理员又开着 TUN，自启会弹 UAC，登录时体验极差，直接跳过
        if (!IsAdmin && IsTunEnabled)
        {
            AppendLog("[提示] 开机自启：当前非管理员且 TUN 开启，已跳过自动启动内核（避免弹 UAC）。");
            return;
        }

        AppendLog("[状态] 开机自启：正在启动内核…");
        await ReconcileSwitchesAsync();
    }

    /// <summary>以管理员身份重新拉起自身并退出当前实例。返回 false 表示提权被取消或失败。</summary>
    public bool RestartAsAdmin()
    {
        if (IsAdmin)
        {
            return true;
        }

        try
        {
            var startInfo = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = true,
                Verb = "runas",
                // --elevate：新进程知道自己是接管方，会等旧进程释放单实例锁
                Arguments = $"--elevate --config \"{ConfigPath}\"",
            };

            // 必须先交锁：否则提权进程拿到锁失败，会把自己当成重复实例直接退出
            SingleInstance.Release();

            Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            // UAC 被取消等：把锁拿回来，继续当唯一实例
            SingleInstance.TryAcquire(waitForHandover: false, timeoutMs: 0);

            AppendLog($"[错误] 提权失败：{ex.Message}");
            return false;
        }

        AppendLog("已拉起管理员实例，正在退出当前实例…");
        Application.Current?.Exit();
        return true;
    }

    /// <summary>窗口关闭时的兜底清理：确保内核进程树不残留。</summary>
    public void Shutdown()
    {
        if (_isShutdown)
        {
            return;
        }

        _isShutdown = true;

        FlushPendingLogs();

        _connectivityCts?.Cancel();

        // 先停遥测：否则内核被杀后 /traffic 会每秒重连，退出期留下幽灵任务
        StopTelemetry();

        // 退出前一定要关掉系统代理，否则浏览器会指向一个已经没人监听的端口
        ClearSystemProxy();

        _service.OutputReceived -= OnCoreOutput;
        _service.Exited -= OnCoreExited;
        _service.Dispose();

        // 停内核期间可能还有最后几行，落盘后再关日志泵
        FlushPendingLogs();
        _logPumpCts.Cancel();

        _clashApi.Dispose();
    }

    /// <summary>内核启动成功后触发，供节点策略页自动拉取。</summary>
    public event EventHandler? KernelStarted;

    /// <summary>当前运行配置里的 clash_api 地址（host:port）。</summary>
    public string ClashController { get; private set; } = string.Empty;

    /// <summary>clash_api 的 secret，没有则为空。</summary>
    public string ClashSecret { get; private set; } = string.Empty;

    /// <summary>追加一条日志；level 为 null 时按正文关键词自动推断。</summary>
    public void AppendLog(string message, AppLogLevel? level = null)
    {
        Logs.Add(new LogEntry(message, level));
        TrimLogsIfNeeded();
    }

    /// <summary>
    /// 轻量模式专用：只把日志投入缓冲队列，不唤醒日志泵、不碰 <see cref="Logs"/>。
    /// 压缩工作集之后再往 UI 集合里加对象，会立刻分配并把刚换出的页重新摸热。
    /// 这些行会在窗口恢复显示时由 FlushPendingLogs 补进日志页。
    /// </summary>
    public void EnqueueDeferredLog(string message)
    {
        _pendingCoreLogs.Enqueue(message);
        TrimPendingLogs(LightweightLogLines);
    }

    /// <summary>
    /// 日志泵：后台线程每入队一批就唤醒一次，合并 100ms 内的所有行后一次性落盘。
    /// 旧实现是「每行一次 Dispatcher 投递 + 每行一次 RemoveAt(0)」，
    /// 内核开 debug 日志时 UI 线程会被彻底占满。
    /// </summary>
    private async Task RunLogPumpAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _logSignal.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                await Task.Delay(LogFlushIntervalMs, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            // 轻量模式：不构造 LogEntry、不碰 UI 集合，只把唤醒信号吃掉。
            // 缓冲由 OnCoreOutput 自己维持在上限内，恢复显示时统一 FlushPendingLogs。
            if (_isInLightweightMode)
            {
                continue;
            }

            FlushPendingLogs();
        }
    }

    /// <summary>
    /// 立即把缓冲区里的日志同步落盘。
    /// 停止内核 / 内核退出 / 清空日志 / 退出程序都必须调它，
    /// 否则内核启动失败瞬间打出的最后几行 FATAL 会滞留在队列里漏显。
    /// </summary>
    public void FlushPendingLogs()
    {
        if (_pendingCoreLogs.IsEmpty)
        {
            return;
        }

        // 允许从任意线程调用：不在 UI 线程就转投一次，保证集合操作安全
        if (!_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(FlushPendingLogs);
            return;
        }

        var batch = new List<string>();
        while (_pendingCoreLogs.TryDequeue(out var line))
        {
            batch.Add(line);
        }

        foreach (var line in batch)
        {
            Logs.Add(new LogEntry(line));
        }

        TrimLogsIfNeeded();
    }

    private void SignalLogPump()
    {
        try
        {
            if (_logSignal.CurrentCount == 0)
            {
                _logSignal.Release();
            }
        }
        catch (SemaphoreFullException)
        {
            // 已有待处理的唤醒信号
        }
    }

    private void TrimLogsIfNeeded()
    {
        // 轻量模式把上限压到 100 且不留松弛量：留在内存里的每一条 LogEntry 都带着 Brush 引用
        var cap = _isInLightweightMode ? LightweightLogLines : MaxLogLines;
        var slack = _isInLightweightMode ? 0 : LogTrimChunk;

        // 超过上限后一次裁一批：避免每来一行都做一次 O(n) 的 RemoveAt(0)
        if (Logs.Count <= cap + slack)
        {
            return;
        }

        var remove = Logs.Count - cap;
        for (var i = 0; i < remove; i++)
        {
            Logs.RemoveAt(0);
        }
    }

    [RelayCommand]
    private void ToggleCore()
    {
        // 托盘菜单的启停等价于「全关」或「默认开系统代理」，
        // 与概览页两个开关共用同一个协调器，避免两条启停链路互相踩状态。
        if (IsRunning)
        {
            SetSystemProxyFromCode(false);
            SetTunEnabledFromCode(false);
        }
        else
        {
            SetSystemProxyFromCode(true);
        }

        _ = ReconcileSwitchesAsync();
    }

    [RelayCommand]
    private void ClearLogs()
    {
        // 先排空缓冲再清空，否则清完又会冒出几条"清空前"的日志
        FlushPendingLogs();
        Logs.Clear();
    }

    private async Task HandleToggleAsync(bool start)
    {
        // 热重启正在进行：此时拨主开关会和重启流程抢同一个内核进程
        if (_isRestarting)
        {
            AppendLog("[提示] 内核正在重启，请稍候再操作主开关。");
            return;
        }

        IsBusy = true;
        try
        {
            if (start)
            {
                await StartCoreAsync();
            }
            else
            {
                await StopCoreAsync();
            }
        }
        catch (Exception ex)
        {
            // 这是"火忘"调用链的最后一层兜底：异常绝不能逃逸成未观察的 Task，
            // 否则 IsRunning 会停在错误的一侧，界面与真实状态永久不一致。
            Fail($"内核{(start ? "启动" : "停止")}失败：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 启动内核。外层统一兜底：任何未预料的异常（进程拉起失败、被安全软件拦截、
    /// 校验超时）都必须落回 <see cref="Fail"/>，否则 IsRunning 会卡在 true。
    /// </summary>
    public async Task StartCoreAsync()
    {
        try
        {
            await StartCoreCoreAsync();
        }
        catch (Exception ex)
        {
            Fail($"启动内核失败：{ex.Message}");
        }
    }

    private async Task StartCoreCoreAsync()
    {
        // 非管理员下开 TUN
        if (!IsAdmin && IsTunEnabled)
        {
            if (_isRestarting)
            {
                // 热重启时旧内核已经停了：这里再走提权分支就等于"只停不启"。
                // 直接把 TUN 关掉继续启动，保证内核一定能回来。
                AppendLog("[提示] 当前非管理员，TUN 无法生效，本次重启以非 TUN 模式继续。");
                SetTunEnabledFromCode(false);
            }
            else
            {
                // 冷启动：走提权重启，不在特权不足的状态下硬起内核
                AppendLog("[提示] TUN 模式需要管理员权限，正在申请提权重启…");
                if (!RestartAsAdmin())
                {
                    Fail("未获得管理员权限，已取消启动。");
                }

                return;
            }
        }

        if (!File.Exists(_service.ExecutablePath))
        {
            Fail($"找不到内核可执行文件：{_service.ExecutablePath}");
            return;
        }

        // 启动前再兜一次：运行期间软件目录可能被移动，或设置被外部改坏
        var resolvedConfig = ResolveConfigPath(null);
        if (!string.Equals(ConfigPath, resolvedConfig, StringComparison.OrdinalIgnoreCase))
        {
            ConfigPath = resolvedConfig;
        }

        if (!File.Exists(ConfigPath))
        {
            Fail($"找不到配置文件：{ConfigPath}");
            return;
        }

        RuntimeConfigResult runtime;
        try
        {
            var options = new RuntimeConfigOptions(
                _service.CoreDirectory,
                _service.RulesetsDirectory,
                IsTunEnabled,
                ClashMode,
                new TunSettings(
                    _settings.Current.TunInterfaceName,
                    _settings.Current.TunStack,
                    _settings.Current.TunMtu,
                    _settings.Current.TunAutoRoute,
                    _settings.Current.TunStrictRoute),
                _settings.Current.EnableIpv6,
                _settings.Current.EnablePreMixin,
                _settings.Current.EnablePostMixin,
                _settings.Current.LastSelectedNodes);

            runtime = RuntimeConfigBuilder.Build(ConfigPath, options);
        }
        catch (Exception ex)
        {
            Fail($"生成运行配置失败：{ex.Message}");
            return;
        }

        AppendLog($"TUN 模式：{(IsTunEnabled ? "开启" : "关闭")} | 代理模式：{ClashModeDisplay}");
        foreach (var note in runtime.Notes)
        {
            AppendLog($"· {note}");
        }

        ClashController = runtime.ClashController;
        ClashSecret = runtime.ClashSecret;
        _mixedPort = runtime.MixedPort;
        AppendLog($"Clash API：{ClashController} | 代理端口：{_mixedPort}");

        AppendLog($"运行配置：{runtime.Path}");

        // 启动前置 0：把内核运行期间下载好的待更新规则集换上（此刻内核确实没在跑）
        ApplyPendingRuleSets();

        // 启动前置 1：running_config 引用了但本地没有的规则集，先补齐（含空骨架兜底）
        await EnsureRuleSetsReadyAsync(runtime.Path);

        // 启动前置 2：订阅里的 remote 规则集改由客户端先下好再以 local 引用。
        // 交给内核自己下的话，启动瞬间代理链还没就绪，必然 FATAL。
        await LocalizeRemoteRuleSetsAsync(runtime.Path);

        // 启动前置 2：先让内核自己校验运行配置，避免带病启动后再回滚
        AppendLog($"校验配置：\"{_service.ExecutablePath}\" check -c \"{runtime.Path}\"");
        var check = await _service.CheckConfigAsync(runtime.Path);
        if (!check.Success)
        {
            foreach (var line in check.Output)
            {
                AppendLog($"[校验] {line}");
            }

            // 最常见的失败原因就是规则集没放对位置，直接给出绝对路径，省得去翻 stderr。
            // 注意措辞有两种：整目录缺失是 "cannot find the path"，目录在但文件缺失是 "cannot find the file"。
            if (IsMissingRuleSet(check.Output))
            {
                AppendLog($"[提示] 配置引用了本地规则集但文件不存在，请把 rulesets 文件夹放到：{_service.RulesetsDirectory}");
            }

            Fail($"配置校验未通过（退出码 {check.ExitCode}），已取消启动。");
            return;
        }

        AppendLog("配置校验通过。");
        AppendLog($"启动内核：\"{_service.ExecutablePath}\" run -c \"{runtime.Path}\" -D \"{_service.CoreDirectory}\"");

        // 启动前置 3：清掉上次 App 被强杀后留下的孤儿内核。
        // 孤儿会一直持有 Core\cache.db 的排他锁，不清掉新内核必定
        // "initialize cache-file: timeout" 退出 1（常见于任务管理器结束进程、关机中断）。
        var strays = _service.KillStrayInstances();
        if (strays.Killed > 0)
        {
            AppendLog($"[提示] 发现 {strays.Killed} 个上次未退出的残留内核进程，已结束并释放 cache.db。");
        }

        if (strays.Denied > 0)
        {
            AppendLog($"[警告] 有 {strays.Denied} 个残留内核权限更高（管理员启动的），无法结束；"
                + "请以管理员身份重启本程序，或在任务管理器里手动结束 sing-box.exe。");
        }

        ResetCoreOutputTail();

        if (!_service.Start(runtime.Path))
        {
            Fail("内核进程启动失败。");
            return;
        }

        AppendLog("内核已启动。");

        // 新一代内核诞生：之前那代的 Exited 事件全部作废
        _runEpoch++;

        // 配置里的 default_mode 就是当前模式，启动后内核已处于该模式
        _lastAppliedMode = ClashMode;

        _settings.Current.LastCoreRunning = true;
        _settings.Save();

        // 记录本次运行配置实际注入的 TUN 值：开关协调器据此判断是否需要热重启
        _appliedTunEnabled = IsTunEnabled;

        StartTelemetry();

        ApplySystemProxy();
        KernelStarted?.Invoke(this, EventArgs.Empty);

        // 有规则集是用空骨架顶上的：代理端口就绪后偷偷补真身，补到了再热重启
        if (_stubbedRuleSets.Count > 0)
        {
            _ = RetryStubbedRuleSetsAsync();
        }
    }

    private async Task RetryStartAfterCacheLockAsync(int epoch)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(1500));
        }
        catch (Exception)
        {
            return;
        }

        // 期间用户点了停止 / 又触发了别的重启 / 新一代内核已经起来了：放弃这次重试
        if (_isStopping || _isRestarting || IsRunning || epoch != _runEpoch)
        {
            return;
        }

        AppendLog("正在重试启动内核…");
        SetRunningFromProcess(true);
        await StartCoreAsync();
    }

    public async Task StopCoreAsync()
    {
        _isStopping = true;
        try
        {
            // 先收掉长连接，避免内核退出后流还在重连
            StopTelemetry();

            // 先关系统代理再停内核，避免中间出现"代理指向死端口"的窗口。
            // 只清注册表，保留 AppSettings 里的开关意图 —— 下次启动内核要自动挂回来。
            ClearSystemProxy();

            AppendLog("正在停止内核（发送中断信号）…");

            var outcome = await _service.StopAsync();

            // 停内核期间的最后几行（含 Ctrl+C 触发的清理日志）也要立刻可见
            FlushPendingLogs();

            AppendLog(outcome switch
            {
                StopOutcome.Graceful => "内核已优雅退出，虚拟网卡与路由已清理。",
                StopOutcome.Forced => "[警告] 内核未响应 Ctrl+C，已强制终止，TUN 路由可能残留。",
                _ => "内核已停止。",
            });

            // 只有用户主动停止才写 false；Shutdown / 关机不算
            _settings.Current.LastCoreRunning = false;
            _settings.Save();
            _appliedTunEnabled = false;
        }
        finally
        {
            _isStopping = false;
        }
    }

    /// <summary>
    /// 把运行配置里的 remote 规则集改成本地引用（客户端负责下载）。
    /// </summary>
    private async Task LocalizeRemoteRuleSetsAsync(string runningConfigPath)
    {
        if (RuleSetService is null)
        {
            return;
        }

        int localized;
        try
        {
            localized = await RuleSetService.LocalizeRemoteRuleSetsAsync(runningConfigPath);
        }
        catch (Exception ex)
        {
            AppendLog($"[提示] 远端规则集本地化失败：{ex.Message}");
            return;
        }

        if (localized > 0)
        {
            AppendLog($"[规则集] 已把 {localized} 个远端规则集改为本地引用（客户端已下好）");
        }
    }

    /// <summary>
    /// 替换掉上次内核运行期间下载好的待更新规则集。
    /// 放在 check 之前：check 读的是磁盘上的真实文件，晚一步就会被旧内容骗过去。
    /// </summary>
    private void ApplyPendingRuleSets()
    {
        if (RuleSetService is null)
        {
            return;
        }

        int applied;
        try
        {
            applied = RuleSetService.ApplyPendingRuleSets();
        }
        catch (Exception ex)
        {
            AppendLog($"[提示] 应用待更新规则集失败：{ex.Message}");
            return;
        }

        if (applied > 0)
        {
            AppendLog($"[规则集] 已应用 {applied} 个待更新规则集（内核启动前的替换）");

            // 队列已清空，设置页列表里的"待更新"标记要跟着消失
            try
            {
                App.RuleSets?.Refresh();
            }
            catch (Exception)
            {
                // 界面还没建好时忽略
            }
        }
    }

    /// <summary>
    /// 把 running_config 里引用、但磁盘上缺失的本地规则集补齐。
    /// 远端拉不到时写一个合法的最小空规则集，保证 sing-box check 能过、内核能起来。
    /// </summary>
    private async Task EnsureRuleSetsReadyAsync(string runningConfigPath)
    {
        if (RuleSetService is null)
        {
            return;
        }

        List<(string FileName, string FullPath)> missing;
        try
        {
            missing = RuleSetService.FindMissingRuleSets(runningConfigPath);
        }
        catch (Exception)
        {
            return;
        }

        if (missing.Count == 0)
        {
            return;
        }

        AppendLog($"[规则集] 运行配置引用了 {missing.Count} 个尚未就位的本地规则集，开始自动补全…");

        foreach (var (fileName, fullPath) in missing)
        {
            AppendLog($"[规则集] 检测到缺失的规则集 {fileName}，正在自动拉取…");

            if (await RuleSetService.EnsureFileAsync(fileName))
            {
                AppendLog($"[规则集] 已补全 {fileName}");
                continue;
            }

            try
            {
                var directory = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                await File.WriteAllTextAsync(fullPath, "{\n  \"version\": 1,\n  \"rules\": []\n}\n");
                _stubbedRuleSets.Add(fileName);
                AppendLog($"[提示] 规则集 {fileName} 暂无法下载，已生成空规则集保底以允许内核启动；待内核启动后将通过代理重试");
            }
            catch (Exception ex)
            {
                AppendLog($"[错误] 生成空规则集失败：{fileName}（{ex.Message}）");
            }
        }
    }

    /// <summary>内核起来后（代理可用）重新拉取用空骨架顶上的规则集，补到了就热重启。</summary>
    private async Task RetryStubbedRuleSetsAsync()
    {
        var pending = _stubbedRuleSets.ToList();
        _stubbedRuleSets.Clear();

        if (pending.Count == 0 || RuleSetService is null)
        {
            return;
        }

        // 等 mixed 端口真正可用，否则下载还是走直连
        await Task.Delay(TimeSpan.FromSeconds(4));

        var recovered = 0;
        foreach (var fileName in pending)
        {
            if (await RuleSetService.EnsureFileAsync(fileName, force: true))
            {
                recovered++;
            }
        }

        if (recovered == 0)
        {
            AppendLog($"[提示] 仍有 {pending.Count} 个规则集未能补全（当前为空规则集），可稍后在设置页手动更新。");
            return;
        }

        AppendLog($"[规则集] 已通过代理补全 {recovered} 个规则集，正在热重启使其生效…");
        _dispatcher.TryEnqueue(() => _ = RestartCoreAsync("[状态] 规则集已补全，正在热重启内核…"));
    }

    private void OnCoreOutput(object? sender, string line)
    {
        // 后台线程只入队，由日志泵合并后一次性落盘
        _pendingCoreLogs.Enqueue(line);

        // 退出时要靠尾部这几行判断原因（cache.db 占锁等），所以另存一份
        lock (_coreOutputTailGate)
        {
            _coreOutputTail.Enqueue(line);
            while (_coreOutputTail.Count > CoreOutputTailCapacity)
            {
                _coreOutputTail.Dequeue();
            }
        }

        // 轻量模式：不唤醒日志泵（不构造 LogEntry、不触发 UI 集合通知），
        // 队列超过 100 行就丢最旧的，防止后台 debug 日志把缓冲撑爆。
        if (_isInLightweightMode)
        {
            TrimPendingLogs(LightweightLogLines);
            return;
        }

        SignalLogPump();
    }

    /// <summary>尾部输出里是否出现了 cache.db 等锁失败的特征。</summary>
    private bool CoreOutputMentionsCacheLock()
    {
        lock (_coreOutputTailGate)
        {
            return _coreOutputTail.Any(line =>
                line.Contains("cache-file", StringComparison.OrdinalIgnoreCase)
                || line.Contains("cache_file", StringComparison.OrdinalIgnoreCase)
                || line.Contains("cache.db", StringComparison.OrdinalIgnoreCase));
        }
    }

    private void ResetCoreOutputTail()
    {
        lock (_coreOutputTailGate)
        {
            _coreOutputTail.Clear();
        }
    }

    private void OnCoreExited(object? sender, int exitCode)
    {
        // 在事件触发的瞬间判定"这次退出是不是预期内"：
        // 排空日志要等 120ms，那时 _isStopping 可能已被 StopCoreAsync 复位，
        // 再判断就会把用户主动停止误报成"[内核退出]"。
        var expected = _isStopping || _isRestarting;
        var epoch = _runEpoch;
        _dispatcher.TryEnqueue(() => _ = HandleCoreExitedAsync(exitCode, expected, epoch));
    }

    private async Task HandleCoreExitedAsync(int exitCode, bool expected, int epoch)
    {
        // 进程退出与输出管道读完有先后：先排空一次，等一小会儿再排空一次，
        // 内核最后的 FATAL 才不会因为 Exited 先到而漏显。
        FlushPendingLogs();

        try
        {
            await Task.Delay(CoreExitLogDrainMs);
        }
        catch (Exception)
        {
            // 忽略
        }

        FlushPendingLogs();

        var willRetry = false;
        if (!expected)
        {
            // 非用户触发（崩溃 / 配置错误 / 异常退出），补一条日志再被动回写状态
            AppendLog($"[内核退出] 退出码 {exitCode}");

            // cache.db 被别的进程锁住时，内核 1 秒内拿不到 flock 就退出 1。
            // 前面已经清过孤儿进程，这里再兜一次：延迟 1.5 秒（给上一个进程真正释放句柄）后自动重试。
            if (exitCode == 1
                && CoreOutputMentionsCacheLock()
                && DateTime.UtcNow - _cacheLockRetryAt > TimeSpan.FromSeconds(20))
            {
                _cacheLockRetryAt = DateTime.UtcNow;
                AppendLog("[提示] cache.db 被占用（initialize cache-file: timeout），1.5 秒后自动重试启动…");
                _ = RetryStartAfterCacheLockAsync(epoch);
                willRetry = true;
            }
        }

        // 热重启期间旧进程退出是预期，IsRunning 由重启流程自己收尾
        if (_isRestarting)
        {
            return;
        }

        // 世代号变了说明新内核已经起来了，这条是上一代迟到的退出通知：
        // 绝不能再去摘系统代理或把 IsRunning 按回 false。
        if (epoch != _runEpoch)
        {
            return;
        }

        // 内核已经没了：先把长连接和系统代理收掉，再回写运行状态。
        // 少了这一步，崩溃退出后浏览器会一直指向死端口，/traffic 还会每秒重连。
        StopTelemetry();

        if (willRetry)
        {
            // cache.db 占锁是可自愈的瞬态故障：只摘注册表代理，保留两个开关的意图，
            // 让 RetryStartAfterCacheLockAsync 重启后能按原组合把代理挂回来。
            ClearSystemProxy();
        }
        else
        {
            DetachSystemProxy();

            // 边界 B：意外退出且不会自动重试时，把两个开关拨回 false，
            // 避免「开关开着但内核已死」。
            if (!expected)
            {
                RollbackSwitches("[提示] 内核意外退出，代理与 TUN 开关已自动关闭。");
            }
        }

        SetRunningFromProcess(false);
    }

    /// <summary>由进程侧回写运行状态，期间屏蔽 OnIsRunningChanged 的启停逻辑。</summary>
    private void SetRunningFromProcess(bool value)
    {
        if (IsRunning == value)
        {
            return;
        }

        _isUpdatingFromProcess = true;
        try
        {
            IsRunning = value;
        }
        finally
        {
            _isUpdatingFromProcess = false;
        }
    }

    private void Fail(string message)
    {
        AppendLog($"[错误] {message}");

        // 启动/重启失败时内核已经不在了：系统代理必须立刻摘掉，
        // 否则浏览器会一直指向一个没人监听的端口。
        DetachSystemProxy();

        // 边界 A：内核没起来却把开关留在开启状态，会让界面显示「运行中」
        // 甚至把系统代理指向死端口。用代码守卫把两个开关拨回 false 并落盘。
        RollbackSwitches("[提示] 内核启动未成功，代理与 TUN 开关已自动关闭。");

        SetRunningFromProcess(false);
    }

    /// <summary>两个驱动开关的代码守卫回滚 + 落盘，绝不触发二次联动。</summary>
    private void RollbackSwitches(string reason)
    {
        SetSystemProxyFromCode(false);
        SetTunEnabledFromCode(false);

        _settings.Current.IsSystemProxyEnabled = false;
        _settings.Current.IsTunEnabled = false;
        _settings.Current.LastCoreRunning = false;
        _settings.Save();
        _appliedTunEnabled = false;

        AppendLog(reason);
    }

    /// <summary>判断 check 失败是否由"规则集文件不存在"引起。</summary>
    private static bool IsMissingRuleSet(IReadOnlyList<string> output) =>
        output.Any(line =>
            line.Contains("rule-set", StringComparison.OrdinalIgnoreCase)
            && (line.Contains("cannot find the path", StringComparison.OrdinalIgnoreCase)
                || line.Contains("cannot find the file", StringComparison.OrdinalIgnoreCase)
                || line.Contains("no such file", StringComparison.OrdinalIgnoreCase)));

    private static bool CheckIsAdmin()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
