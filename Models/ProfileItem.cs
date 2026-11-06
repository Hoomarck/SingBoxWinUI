using SingBoxWinUI.Services;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SingBoxWinUI.Models;

/// <summary>
/// 一个订阅条目。会被序列化到 Core\profiles.json，
/// 带 [JsonIgnore] 的都是纯 UI 派生属性。
/// </summary>
public partial class ProfileItem : ObservableObject
{
    /// <summary>本地化入口：模板里的 {x:Bind Loc.Xxx} 走这里。纯 UI 属性，绝不落盘。</summary>
    [JsonIgnore]
    public LocalizationService Loc => LocalizationService.Instance;

    [ObservableProperty]
    public partial string Id { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    /// <summary>在线订阅 or 本地导入。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TypeText))]
    [NotifyPropertyChangedFor(nameof(CanUpdate))]
    public partial bool IsRemote { get; set; }

    [ObservableProperty]
    public partial string Url { get; set; } = string.Empty;

    /// <summary>落在 Core\profiles\ 下的文件名。</summary>
    [ObservableProperty]
    public partial string FileName { get; set; } = string.Empty;

    /// <summary>定时更新间隔（分钟），0 表示不自动更新。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IntervalText))]
    [NotifyPropertyChangedFor(nameof(MetaText))]
    public partial int UpdateIntervalMinutes { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LastUpdatedText))]
    [NotifyPropertyChangedFor(nameof(MetaText))]
    public partial DateTimeOffset? LastUpdated { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NodeCountText))]
    [NotifyPropertyChangedFor(nameof(MetaText))]
    public partial int NodeCount { get; set; }

    // ---- 订阅流量/到期（通道 1：Subscription-Userinfo 响应头）----
    [ObservableProperty]
    public partial long? UploadBytes { get; set; }

    [ObservableProperty]
    public partial long? DownloadBytes { get; set; }

    [ObservableProperty]
    public partial long? TotalBytes { get; set; }

    /// <summary>到期时间，Unix 秒级时间戳。</summary>
    [ObservableProperty]
    public partial long? ExpireTimestamp { get; set; }

    // ---- 通道 2：从 outbounds 节点名兜底提取 ----
    [ObservableProperty]
    public partial string ParsedTrafficHint { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ParsedExpireHint { get; set; } = string.Empty;

    /// <summary>是否当前激活，仅用于 UI 高亮。</summary>
    [JsonIgnore]
    [ObservableProperty]
    public partial bool IsActive { get; set; }

    [JsonIgnore]
    public string TypeText => LocalizationService.Instance.ProfileTypeText(IsRemote);

    [JsonIgnore]
    public string NodeCountText => LocalizationService.Instance.NodesCountText(NodeCount);

    [JsonIgnore]
    public string IntervalText => LocalizationService.Instance.ProfileIntervalText(UpdateIntervalMinutes);

    [JsonIgnore]
    public string LastUpdatedText => LocalizationService.Instance.ProfileUpdatedText(LastUpdated);

    [JsonIgnore]
    public bool CanUpdate => IsRemote;

    [JsonIgnore]
    public string MetaText => $"{NodeCountText} · {LastUpdatedText} · {IntervalText}";

    /// <summary>切语言后把上面这些派生文案整批重算（数据本身不变）。</summary>
    public void RefreshLocalizedText()
    {
        OnPropertyChanged(nameof(TypeText));
        OnPropertyChanged(nameof(NodeCountText));
        OnPropertyChanged(nameof(IntervalText));
        OnPropertyChanged(nameof(LastUpdatedText));
        OnPropertyChanged(nameof(MetaText));
    }
}

/// <summary>profiles.json 的根结构。</summary>
public sealed class ProfileStore
{
    public List<ProfileItem> Profiles { get; set; } = new();
}
