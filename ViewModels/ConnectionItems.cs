using System.Text.Json.Serialization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using SingBoxWinUI.Helpers;
using SingBoxWinUI.Services;

namespace SingBoxWinUI.ViewModels;

/// <summary>连接列表排序维度，索引与 XAML 里的 ComboBox 对应。</summary>
public enum ConnectionSortMode
{
    StartTime,
    DownloadSpeed,
    UploadSpeed,
    DownloadTotal,
    UploadTotal,
    Host,
    Outbound,
}

/// <summary>
/// 各连接共享的一份"显示哪些参数"设置，勾选变化时全列表即时响应。
/// </summary>
public partial class ConnectionColumnSettings : ObservableObject
{
    [ObservableProperty]
    public partial bool ShowInbound { get; set; } = true;

    [ObservableProperty]
    public partial bool ShowProcess { get; set; } = true;

    [ObservableProperty]
    public partial bool ShowRule { get; set; } = true;

    [ObservableProperty]
    public partial bool ShowChains { get; set; } = true;

    [ObservableProperty]
    public partial bool ShowSpeed { get; set; } = true;

    [ObservableProperty]
    public partial bool ShowTraffic { get; set; } = true;

    [ObservableProperty]
    public partial bool ShowIp { get; set; } = false;

    [ObservableProperty]
    public partial bool ShowDuration { get; set; } = true;
}

/// <summary>
/// 单条活动连接。原始字段不参与绑定（筛选/排序用），
/// 绑定的都是每秒刷新一次的展示字符串。
/// </summary>
public sealed partial class ConnectionItem : ObservableObject
{
    /// <summary>本地化入口：模板里的 {x:Bind Loc.Xxx} 走这里。</summary>
    [JsonIgnore]
    public LocalizationService Loc => LocalizationService.Instance;

    public ConnectionItem(ConnectionInfo info, ConnectionColumnSettings columns)
    {
        Id = info.Id;
        Columns = columns;
        Update(info, 0, 0);
    }

    public string Id { get; }

    public ConnectionColumnSettings Columns { get; }

    /// <summary>静态文本是否已经按首帧数据建过一次。</summary>
    private bool _metadataReady;

    // ---- 原始数据（不绑定） ----
    public DateTimeOffset Start { get; private set; }
    public long Upload { get; private set; }
    public long Download { get; private set; }
    public double UpSpeed { get; private set; }
    public double DownSpeed { get; private set; }
    public string Host { get; private set; } = string.Empty;
    public string DestinationIp { get; private set; } = string.Empty;
    public int DestinationPort { get; private set; }
    public string Process { get; private set; } = string.Empty;
    public string Rule { get; private set; } = string.Empty;
    public string RulePayload { get; private set; } = string.Empty;
    public string Network { get; private set; } = string.Empty;
    public string InboundLabel { get; private set; } = string.Empty;
    public IReadOnlyList<string> Chains { get; private set; } = Array.Empty<string>();

    /// <summary>主策略组（chains 第一项），用于排序与筛选。</summary>
    public string ChainPrimary { get; private set; } = string.Empty;

    /// <summary>预拼的小写搜索串，避免每次筛选都拼字符串。</summary>
    public string SearchBlob { get; private set; } = string.Empty;

    // ---- 绑定用 ----
    [ObservableProperty]
    public partial string Target { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string InboundText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SpeedText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string TrafficText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ProcessText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string RuleText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ChainsText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string IpText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DurationText { get; set; } = string.Empty;

    public void Update(ConnectionInfo info, double upSpeed, double downSpeed)
    {
        // start 解析失败时只在首次兜底：不能每秒重置，否则时长永远显示 00:00
        if (Start == default)
        {
            Start = info.Start == default ? DateTimeOffset.Now : info.Start;
        }
        else if (info.Start != default)
        {
            Start = info.Start;
        }

        Upload = info.Upload;
        Download = info.Download;
        UpSpeed = upSpeed;
        DownSpeed = downSpeed;

        var inboundLabel = string.IsNullOrEmpty(info.InboundTag)
            ? info.InboundType
            : $"{info.InboundType}/{info.InboundTag}";

        // 只有"元数据"真的变了才重建静态文本。
        // 每秒 × N 条连接时，主机名 / 进程 / 规则 / 链路基本不变，
        // 没必要每帧重新拼 9 个字符串（旧实现每秒约产生 1000 个临时串）。
        var metadataChanged = !_metadataReady
            || !string.Equals(Host, info.Host, StringComparison.Ordinal)
            || !string.Equals(DestinationIp, info.DestinationIp, StringComparison.Ordinal)
            || DestinationPort != info.DestinationPort
            || !string.Equals(Process, info.Process, StringComparison.Ordinal)
            || !string.Equals(Rule, info.Rule, StringComparison.Ordinal)
            || !string.Equals(RulePayload, info.RulePayload, StringComparison.Ordinal)
            || !string.Equals(Network, info.Network, StringComparison.Ordinal)
            || !string.Equals(InboundLabel, inboundLabel, StringComparison.Ordinal)
            || !ChainsEqual(Chains, info.Chains);

        Host = info.Host;
        DestinationIp = info.DestinationIp;
        DestinationPort = info.DestinationPort;
        Process = info.Process;
        Rule = info.Rule;
        RulePayload = info.RulePayload;
        Network = info.Network;
        Chains = info.Chains;
        InboundLabel = inboundLabel;
        ChainPrimary = info.Chains.Count > 0 ? info.Chains[0] : string.Empty;

        if (metadataChanged)
        {
            var port = DestinationPort > 0 ? $":{DestinationPort}" : string.Empty;
            Target = string.IsNullOrEmpty(Host)
                ? $"{DestinationIp}{port}"
                : $"{Host}{port}";

            InboundText = string.IsNullOrEmpty(Network)
                ? InboundLabel
                : $"{Network.ToUpperInvariant()} · {InboundLabel}";

            ProcessText = string.IsNullOrEmpty(Process)
                ? LocalizationService.Instance.Say("未知进程", "Unknown")
                : Process;

            RuleText = string.IsNullOrEmpty(RulePayload)
                ? (string.IsNullOrEmpty(Rule) ? "final" : Rule)
                : $"{Rule} => {RulePayload}";

            ChainsText = Chains.Count == 0 ? "—" : string.Join(" → ", Chains);
            IpText = string.IsNullOrEmpty(DestinationIp) ? "—" : DestinationIp;

            SearchBlob = BuildSearchBlob();
            _metadataReady = true;
        }

        // 每秒必变的三项
        SpeedText = $"↓ {FormatHelper.FormatSpeed(DownSpeed)}  ↑ {FormatHelper.FormatSpeed(UpSpeed)}";
        TrafficText = $"↓ {FormatHelper.FormatBytes(Download)}  ↑ {FormatHelper.FormatBytes(Upload)}";

        var elapsed = DateTimeOffset.Now - Start;
        DurationText = elapsed.TotalHours >= 1
            ? $"{(int)elapsed.TotalHours}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
            : $"{elapsed.Minutes:00}:{elapsed.Seconds:00}";
    }

    /// <summary>预拼搜索串。用 StringBuilder 逐段拼接，避免每帧 new 数组 + LINQ。</summary>
    private string BuildSearchBlob()
    {
        var builder = new StringBuilder(64);

        Append(Host);
        Append(DestinationIp);
        Append(Process);
        Append(Rule);
        Append(RulePayload);
        Append(ChainsText);
        Append(Target);

        return builder.ToString();

        void Append(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return;
            }

            if (builder.Length > 0)
            {
                builder.Append('\u0001');
            }

            builder.Append(value);
        }
    }

    private static bool ChainsEqual(IReadOnlyList<string> current, IReadOnlyList<string> next)
    {
        if (ReferenceEquals(current, next))
        {
            return true;
        }

        if (current.Count != next.Count)
        {
            return false;
        }

        for (var i = 0; i < current.Count; i++)
        {
            if (!string.Equals(current[i], next[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
