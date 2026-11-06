using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SingBoxWinUI.Models;
using SingBoxWinUI.Services;
using Windows.ApplicationModel.DataTransfer;

namespace SingBoxWinUI.ViewModels;

/// <summary>
/// 运行日志页。日志本体在 MainViewModel.Logs，这里维护过滤后的视图。
/// 新增/裁剪走增量同步，只有切换筛选条件时才整表重建。
/// </summary>
public partial class LogsViewModel : ObservableObject
{
    /// <summary>搜索框防抖窗口：逐字符输入时不必每键都重建过滤结果。</summary>
    private const int SearchDebounceMs = 250;

    private readonly MainViewModel _main;

    private CancellationTokenSource? _searchCts;

    public LogsViewModel(MainViewModel main)
    {
        _main = main;
        _main.Logs.CollectionChanged += OnLogsChanged;
        LocalizationService.Instance.LanguageChanged += OnLanguageChanged;
        Rebuild();
    }

    public ObservableCollection<LogEntry> FilteredLogs { get; } = new();

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    /// <summary>0 全部 / 1 Info 及以上 / 2 Warning 及以上 / 3 仅 Error。</summary>
    [ObservableProperty]
    public partial int LevelFilterIndex { get; set; }

    /// <summary>新日志进来时是否自动滚到底部。</summary>
    [ObservableProperty]
    public partial bool AutoScroll { get; set; } = true;

    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    /// <summary>级别筛选下拉框的显示文案。</summary>
    public IReadOnlyList<string> LevelOptions =>
    [
        LocalizationService.Instance.LogsLevelAll, LocalizationService.Instance.LogsLevelInfoUp,
        LocalizationService.Instance.LogsLevelWarnUp, LocalizationService.Instance.LogsLevelError,
    ];

    public string CountText => LocalizationService.Instance.LogCount(FilteredLogs.Count);

    public bool HasLogs => FilteredLogs.Count > 0;

    public bool HasNoLogs => FilteredLogs.Count == 0;

    partial void OnSearchTextChanged(string value) => ScheduleRebuild();

    partial void OnLevelFilterIndexChanged(int value)
    {
        // 换 ItemsSource 时 ComboBox 会先把 SelectedIndex 打成 -1 再写回
        if (value < 0)
        {
            return;
        }

        Rebuild();
    }

    /// <summary>250ms 内没有新的输入才真正重建（下拉筛选是点选，走即时路径）。</summary>
    private void ScheduleRebuild()
    {
        var previous = _searchCts;
        var cts = new CancellationTokenSource();
        _searchCts = cts;

        previous?.Cancel();
        previous?.Dispose();

        _ = RebuildDebouncedAsync(cts.Token);
    }

    private async Task RebuildDebouncedAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(SearchDebounceMs, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        Rebuild();
    }

    private void OnLogsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add when e.NewItems is not null:
                foreach (var item in e.NewItems.OfType<LogEntry>())
                {
                    if (Matches(item))
                    {
                        FilteredLogs.Add(item);
                    }
                }

                break;

            case NotifyCollectionChangedAction.Remove when e.OldItems is not null:
                foreach (var item in e.OldItems.OfType<LogEntry>())
                {
                    FilteredLogs.Remove(item);
                }

                break;

            default:
                Rebuild();
                return;
        }

        NotifyCounts();
    }

    private void Rebuild()
    {
        var target = new List<LogEntry>(_main.Logs.Count);
        foreach (var entry in _main.Logs)
        {
            if (Matches(entry))
            {
                target.Add(entry);
            }
        }

        // 增量对齐：只做删除 / 插入 / Move，不 Clear。
        // 旧实现 Clear + 逐条 Add 会让 ItemsRepeater 整体重建，1000 条日志下每次按键都明显卡顿。
        var targetSet = new HashSet<LogEntry>(target);
        for (var i = FilteredLogs.Count - 1; i >= 0; i--)
        {
            if (!targetSet.Contains(FilteredLogs[i]))
            {
                FilteredLogs.RemoveAt(i);
            }
        }

        for (var i = 0; i < target.Count; i++)
        {
            var item = target[i];

            // 常见情况（筛选条件没变）在这里 O(1) 直接跳过
            if (i < FilteredLogs.Count && ReferenceEquals(FilteredLogs[i], item))
            {
                continue;
            }

            var current = FilteredLogs.IndexOf(item);
            if (current < 0)
            {
                FilteredLogs.Insert(i, item);
            }
            else
            {
                FilteredLogs.Move(current, i);
            }
        }

        NotifyCounts();
    }

    /// <summary>重新下发级别筛选下拉框的选中索引（ItemsSource 赋值会把它重置成 -1）。</summary>
    public void RefreshComboSelections()
    {
        // ItemsSource 就位前 ComboBox 会把索引写成 -1，恢复成"全部级别"
        if (LevelFilterIndex < 0)
        {
            LevelFilterIndex = 0;
        }

        OnPropertyChanged(nameof(LevelFilterIndex));
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(LevelOptions));
        RefreshComboSelections();
    }

    private void NotifyCounts()
    {
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(HasLogs));
        OnPropertyChanged(nameof(HasNoLogs));
    }

    private bool Matches(LogEntry entry)
    {
        var threshold = LevelFilterIndex switch
        {
            1 => 1, // Info 及以上
            2 => 2, // Warning 及以上
            3 => 3, // 仅 Error
            _ => 0,
        };

        if (entry.Severity < threshold)
        {
            return false;
        }

        var keyword = SearchText?.Trim();
        return string.IsNullOrEmpty(keyword)
               || entry.Message.Contains(keyword, StringComparison.OrdinalIgnoreCase)
               || entry.RawLine.Contains(keyword, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>当前过滤结果拼成纯文本，供复制/导出复用。</summary>
    public string BuildText() => string.Join(Environment.NewLine, FilteredLogs.Select(entry => entry.RawLine));

    [RelayCommand]
    private void CopyAll()
    {
        if (FilteredLogs.Count == 0)
        {
            StatusText = LocalizationService.Instance.LogsCopyEmpty();
            return;
        }

        try
        {
            var package = new DataPackage();
            package.SetText(BuildText());
            Clipboard.SetContent(package);
            StatusText = LocalizationService.Instance.LogsCopied(FilteredLogs.Count);
        }
        catch (Exception ex)
        {
            StatusText = LocalizationService.Instance.Say($"复制失败：{ex.Message}", $"Copy failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private void Clear()
    {
        _main.Logs.Clear();
        FilteredLogs.Clear();
        StatusText = LocalizationService.Instance.LogsCleared();
        NotifyCounts();
    }
}
