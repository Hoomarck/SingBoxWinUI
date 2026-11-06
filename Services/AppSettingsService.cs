using System.Text.Json.Serialization;
using System.IO;
using System.Text;
using System.Text.Json;
using SingBoxWinUI.Helpers;

namespace SingBoxWinUI.Services;

/// <summary>客户端自身设置，序列化到 Core\app_settings.json。</summary>
public sealed class AppSettings
{
    /// <summary>开机自动启动客户端。</summary>
    public bool AutoStartApp { get; set; }

    /// <summary>开机自启时静默隐藏到托盘。</summary>
    public bool SilentStartToTray { get; set; }

    /// <summary>启动客户端后自动拉起内核。</summary>
    public bool AutoStartCore { get; set; }

    /// <summary>点关闭按钮时隐藏到托盘而不是退出。</summary>
    public bool CloseToTray { get; set; } = true;

    /// <summary>界面语言：zh-CN / en-US。</summary>
    public string Language { get; set; } = LocalizationService.ChineseLanguage;

    /// <summary>Default / Light / Dark。</summary>
    public string AppTheme { get; set; } = "Default";

    /// <summary>节点测速地址。</summary>
    public string SpeedTestUrl { get; set; } = DefaultSpeedTestUrl;

    /// <summary>统一延迟：连续探测两次取第二次结果。</summary>
    public bool UnifiedDelay { get; set; } = true;

    /// <summary>启用 IPv6：控制 dns.strategy 与 TUN 网卡双栈地址。</summary>
    public bool EnableIpv6 { get; set; }

    /// <summary>上次退出时内核是否在运行（下次启动据此自动拉起）。</summary>
    public bool LastCoreRunning { get; set; }

    /// <summary>系统代理开关（意图值，跨启动保留；停止内核只清注册表不改这里）。</summary>
    public bool IsSystemProxyEnabled { get; set; }

    /// <summary>记住提权：下次普通启动时自动申请管理员。</summary>
    public bool AlwaysRunAsAdmin { get; set; }

    /// <summary>后台自动检测节点延迟。</summary>
    public bool AutoTestDelay { get; set; }

    /// <summary>自动测速间隔（分钟）。</summary>
    public int AutoTestDelayIntervalMinutes { get; set; } = 15;

    /// <summary>后台轻量模式：隐藏到托盘一段时间后降频省内存。</summary>
    public bool EnableLightweightMode { get; set; } = true;

    /// <summary>隐藏多少分钟后进入轻量模式。</summary>
    public int LightweightDelayMinutes { get; set; } = 5;

    /// <summary>规则集远程下载模板，支持 {category} {short} {name} {file}。</summary>
    public string RuleSetBaseUrl { get; set; } = RuleSetUpdateService.DefaultBaseUrl;

    /// <summary>后台定时更新规则集。</summary>
    public bool AutoUpdateRuleSets { get; set; } = true;

    /// <summary>规则集更新间隔（小时）。</summary>
    public int RuleSetUpdateIntervalHours { get; set; } = 24;

    /// <summary>上次成功更新规则集的时间。</summary>
    public DateTimeOffset? LastRuleSetUpdate { get; set; }

    /// <summary>WebDAV 备份目录地址。</summary>
    public string WebDavUrl { get; set; } = string.Empty;

    public string WebDavUsername { get; set; } = string.Empty;

    public string WebDavPassword { get; set; } = string.Empty;

    /// <summary>每次启动后自动做一次 WebDAV 备份。</summary>
    public bool AutoWebDavBackupOnStartup { get; set; } = true;

    /// <summary>启用全局前置拓展（mixin_pre.json）。</summary>
    public bool EnablePreMixin { get; set; }

    /// <summary>启用全局后置拓展（mixin_post.json）。</summary>
    public bool EnablePostMixin { get; set; }

    /// <summary>
    /// 每个策略组上次手动选中的节点：Key = 策略组 tag，Value = 节点 tag。
    /// 构建运行配置时直接注入 selector.default，即使 cache.db 丢失也能记住选择。
    /// </summary>
    public Dictionary<string, string> LastSelectedNodes { get; set; } = new();

    /// <summary>上次选中的配置文件，启动时恢复。</summary>
    public string ConfigPath { get; set; } = string.Empty;

    /// <summary>上次的 TUN 开关状态，启动时恢复。默认关闭：开 TUN 需要管理员权限。</summary>
    public bool IsTunEnabled { get; set; }

    /// <summary>本程序最后一次写入的系统代理地址，用于识别异常退出后的残留。</summary>
    public string LastProxyServer { get; set; } = string.Empty;

    /// <summary>当前激活的订阅 Id。</summary>
    public string ActiveProfileId { get; set; } = string.Empty;

    /// <summary>活动连接页勾选了哪些参数。</summary>
    public ConnectionColumnState ConnectionColumns { get; set; } = new();

    /// <summary>代理模式：Rule / Global / Direct。</summary>
    public string ClashMode { get; set; } = "Rule";

    /// <summary>TUN 网卡设备名（interface_name）。</summary>
    public string TunInterfaceName { get; set; } = "singbox-tun";

    /// <summary>TUN 网络栈：system / gvisor / mixed。</summary>
    public string TunStack { get; set; } = "system";

    /// <summary>TUN MTU。</summary>
    public int TunMtu { get; set; } = 9000;

    /// <summary>TUN 自动路由。</summary>
    public bool TunAutoRoute { get; set; } = true;

    /// <summary>TUN 严格路由。</summary>
    public bool TunStrictRoute { get; set; } = true;

    public const string DefaultSpeedTestUrl = "https://www.gstatic.com/generate_204";
}

/// <summary>活动连接页的参数显示开关，持久化用。</summary>
public sealed class ConnectionColumnState
{
    public bool ShowInbound { get; set; } = true;
    public bool ShowProcess { get; set; } = true;
    public bool ShowRule { get; set; } = true;
    public bool ShowChains { get; set; } = true;
    public bool ShowSpeed { get; set; } = true;
    public bool ShowTraffic { get; set; } = true;
    public bool ShowIp { get; set; }
    public bool ShowDuration { get; set; } = true;
}

/// <summary>
/// 设置读写。启动时 Load，改动后由调用方 Save（小文件，直接全量写）。
/// </summary>
public sealed class AppSettingsService
{
    /// <summary>文本类设置逐字符变化时的落盘防抖窗口。</summary>
    private const int SaveDebounceMs = 300;

    private readonly string _path;
    private readonly object _saveGate = new();

    /// <summary>防抖计时器：WebDAV 地址 / 密码 / 测速地址这类文本逐字符输入时用。</summary>
    private Timer? _saveTimer;

    public AppSettingsService(string coreDirectory)
    {
        _path = Path.Combine(coreDirectory, "app_settings.json");
        Load();
    }

    public AppSettings Current { get; private set; } = new();

    [JsonIgnore]
    public string FilePath => _path;

    public void Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                Current = new AppSettings();
                Save();
                return;
            }

            var json = File.ReadAllText(_path);
            Current = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch (Exception)
        {
            // 文件损坏时退回默认值，不要因为设置读不出来就起不来
            Current = new AppSettings();
        }
    }

    /// <summary>
    /// 防抖落盘：300ms 内没有新的改动才真正写文件。
    /// 旧实现每敲一个字符就全量重写一次 app_settings.json（输入 30 位密码 = 30 次磁盘写）。
    /// </summary>
    public void SaveDebounced()
    {
        var timer = _saveTimer;
        if (timer is null)
        {
            _saveTimer = new Timer(_ => Save(), null, SaveDebounceMs, Timeout.Infinite);
            return;
        }

        timer.Change(SaveDebounceMs, Timeout.Infinite);
    }

    public void Save()
    {
        // 防抖回调在线程池上跑，可能和 UI 线程的显式 Save 撞车，这里串行化
        lock (_saveGate)
        {
            try
            {
                var directory = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(
                    _path,
                    JsonSerializer.Serialize(Current, JsonDefaults.Indented),
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
            catch (Exception)
            {
                // 只读目录等场景，静默失败
            }
        }
    }
}
