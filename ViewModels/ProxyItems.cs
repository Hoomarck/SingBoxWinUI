using System.Text.Json.Serialization;
using SingBoxWinUI.Services;
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media;
using SingBoxWinUI.Helpers;

namespace SingBoxWinUI.ViewModels;

/// <summary>策略组下的一个节点卡片。</summary>
public partial class ProxyNodeItem : ObservableObject
{
    /// <summary>本地化入口：模板里的 {x:Bind Loc.Xxx} 走这里。</summary>
    [JsonIgnore]
    public LocalizationService Loc => LocalizationService.Instance;

    public ProxyNodeItem(string name, string type, string groupName, int delay)
    {
        Name = name;
        Type = type;
        GroupName = groupName;
        Delay = delay;
    }

    public string Name { get; }

    /// <summary>具体协议类型，如 vless / hysteria2 / anytls。</summary>
    public string Type { get; }

    /// <summary>所属策略组，切换节点时要用。</summary>
    public string GroupName { get; }

    /// <summary>-1 未知，0 超时，&gt;0 毫秒。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DelayText))]
    [NotifyPropertyChangedFor(nameof(DelayBrush))]
    public partial int Delay { get; set; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    public string DelayText => Delay switch
    {
        < 0 => "—",
        0 => "Timeout",
        _ => $"{Delay} ms",
    };

    public Brush DelayBrush => Delay switch
    {
        < 0 => ThemeBrushHelper.Get("TextFillColorTertiaryBrush", Microsoft.UI.Colors.Gray),
        0 => ThemeBrushHelper.Get("SystemFillColorCriticalBrush", Microsoft.UI.Colors.Gray),
        < 200 => ThemeBrushHelper.Get("SystemFillColorSuccessBrush", Microsoft.UI.Colors.Gray),
        <= 500 => ThemeBrushHelper.Get("SystemFillColorCautionBrush", Microsoft.UI.Colors.Gray),
        _ => ThemeBrushHelper.Get("SystemFillColorCriticalBrush", Microsoft.UI.Colors.Gray),
    };
}

/// <summary>一个策略组（Selector / URLTest）。</summary>
public partial class ProxyGroupItem : ObservableObject
{
    /// <summary>本地化入口：模板里的 {x:Bind Loc.Xxx} 走这里。</summary>
    [JsonIgnore]
    public LocalizationService Loc => LocalizationService.Instance;

    public ProxyGroupItem(string name, string type, string now, IEnumerable<ProxyNodeItem> nodes)
    {
        Name = name;
        Type = type;
        Now = now;
        Nodes = new ObservableCollection<ProxyNodeItem>(nodes);

        // 记住原始顺序，Default 排序和"未测速保持原序"都要用它
        _originalNodes = Nodes.ToList();
    }

    private readonly List<ProxyNodeItem> _originalNodes;

    /// <summary>中文节点按拼音（A-Z）排序，不受进程文化影响。</summary>
    private static readonly StringComparer NameComparer =
        StringComparer.Create(new CultureInfo("zh-CN"), ignoreCase: true);

    public string Name { get; }

    /// <summary>Selector 或 URLTest，用于 Header 上的类型标签。</summary>
    public string Type { get; }

    [ObservableProperty]
    public partial string Now { get; set; }

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTestEnabled))]
    [NotifyPropertyChangedFor(nameof(TestButtonText))]
    public partial bool IsTesting { get; set; }

    public ObservableCollection<ProxyNodeItem> Nodes { get; }

    /// <summary>当前排序模式。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SortButtonText))]
    public partial ProxySortMode SortMode { get; set; }

    /// <summary>测速中禁用按钮，防连点。</summary>
    public bool IsTestEnabled => !IsTesting;

    public string TestButtonText => IsTesting
        ? LocalizationService.Instance.ProxiesTesting
        : LocalizationService.Instance.ProxiesTestLatency;

    public string SortButtonText => SortMode switch
    {
        ProxySortMode.Delay => LocalizationService.Instance.ProxiesSortDelay,
        ProxySortMode.Name => LocalizationService.Instance.ProxiesSortName,
        _ => LocalizationService.Instance.ProxiesSortDefault,
    };

    /// <summary>语言切换时由 ProxiesViewModel 统一调用，重算按钮文案。</summary>
    public void NotifyLocalized()
    {
        OnPropertyChanged(nameof(TestButtonText));
        OnPropertyChanged(nameof(SortButtonText));
    }

    /// <summary>在 默认 -> 延迟 -> 名称 之间循环。</summary>
    public void CycleSort()
    {
        SortMode = SortMode switch
        {
            ProxySortMode.Default => ProxySortMode.Delay,
            ProxySortMode.Delay => ProxySortMode.Name,
            _ => ProxySortMode.Default,
        };

        ApplySort();
    }

    /// <summary>
    /// 按当前模式重排。始终从原始顺序出发，保证结果可预期。
    /// 延迟排序不能直接比数值：-1 未测速、0 超时，直接升序会把它们顶到最前面。
    /// </summary>
    public void ApplySort()
    {
        IEnumerable<ProxyNodeItem> ordered = SortMode switch
        {
            // 有效延迟(>0) -> 未测速(<0) -> 超时(==0)；OrderBy 是稳定排序，
            // 所以未测速与超时之间会保持原始相对顺序
            ProxySortMode.Delay => _originalNodes
                .OrderBy(RankByDelay)
                .ThenBy(node => node.Delay > 0 ? node.Delay : 0),
            ProxySortMode.Name => _originalNodes
                .OrderBy(node => node.Name, NameComparer),
            _ => _originalNodes,
        };

        var target = ordered.ToList();
        if (Nodes.SequenceEqual(target))
        {
            return;
        }

        // 复用同一批 ProxyNodeItem 实例，IsSelected 高亮不会丢
        Nodes.Clear();
        foreach (var node in target)
        {
            Nodes.Add(node);
        }
    }

    /// <summary>测速完成后，若正按延迟排序则自动重排。</summary>
    public void RefreshSortIfNeeded()
    {
        if (SortMode == ProxySortMode.Delay)
        {
            ApplySort();
        }
    }

    private static int RankByDelay(ProxyNodeItem node) => node.Delay switch
    {
        > 0 => 0,
        < 0 => 1,
        _ => 2,
    };
}

/// <summary>策略组节点排序方式。</summary>
public enum ProxySortMode
{
    Default,
    Delay,
    Name,
}
