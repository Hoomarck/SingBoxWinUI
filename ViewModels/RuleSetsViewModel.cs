using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SingBoxWinUI.Services;

namespace SingBoxWinUI.ViewModels;

/// <summary>规则集管理：扫描本地文件、批量更新、配置远程模板。</summary>
public partial class RuleSetsViewModel : ObservableObject
{
    private readonly RuleSetUpdateService _service;
    private readonly AppSettingsService _settings;
    private readonly MainViewModel _main;
    private bool _isLoading = true;

    public RuleSetsViewModel(RuleSetUpdateService service, AppSettingsService settings, MainViewModel main)
    {
        _service = service;
        _settings = settings;
        _main = main;

        AutoUpdate = settings.Current.AutoUpdateRuleSets;
        BaseUrl = settings.Current.RuleSetBaseUrl;
        _isLoading = false;

        Refresh();

        // 摘要与进度都是拼接文案，状态键不变但译文要重取一遍
        LocalizationService.Instance.LanguageChanged += OnLanguageChanged;
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        UpdateSummary();

        foreach (var item in Items)
        {
            item.RefreshStatusText();
        }

        if (!string.IsNullOrEmpty(ProgressText) && !IsUpdating)
        {
            // 已结束的进度/结果文案是上一次操作的残留，切语言后无法重算，
            // 直接清掉，避免中英混排
            ProgressText = string.Empty;
        }
    }

    public ObservableCollection<RuleSetFileInfo> Items { get; } = new();

    [ObservableProperty]
    public partial bool AutoUpdate { get; set; }

    [ObservableProperty]
    public partial string BaseUrl { get; set; } = RuleSetUpdateService.DefaultBaseUrl;

    [ObservableProperty]
    public partial bool IsUpdating { get; set; }

    [ObservableProperty]
    public partial string ProgressText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SummaryText { get; set; } = string.Empty;

    public bool CanUpdate => !IsUpdating;

    partial void OnAutoUpdateChanged(bool value)
    {
        if (_isLoading)
        {
            return;
        }

        _settings.Current.AutoUpdateRuleSets = value;
        _settings.Save();
    }

    partial void OnBaseUrlChanged(string value)
    {
        if (_isLoading)
        {
            return;
        }

        _settings.Current.RuleSetBaseUrl = string.IsNullOrWhiteSpace(value)
            ? RuleSetUpdateService.DefaultBaseUrl
            : value.Trim();
        _settings.Save();
    }

    partial void OnIsUpdatingChanged(bool value) => OnPropertyChanged(nameof(CanUpdate));

    /// <summary>恢复备份后把开关与模板地址重新对齐到磁盘（不触发保存）。</summary>
    public void ReloadFromSettings()
    {
        _isLoading = true;
        try
        {
            AutoUpdate = _settings.Current.AutoUpdateRuleSets;
            BaseUrl = _settings.Current.RuleSetBaseUrl;
        }
        finally
        {
            _isLoading = false;
        }

        Refresh();
    }

    /// <summary>重新扫描本地规则集。</summary>
    public void Refresh()
    {
        var previous = Items.ToDictionary(item => item.FileName, item => item.Status, StringComparer.OrdinalIgnoreCase);

        Items.Clear();
        foreach (var item in _service.Scan())
        {
            // "待更新"是从磁盘（.pending 有没有对应文件）推出来的，必须最优先：
            // 上一轮的"就绪/已更新"和它冲突时以磁盘为准，否则队列里的新版本会看不出来。
            // 反过来，磁盘上已经没有待更新文件时，也不能把旧的"待更新"贴回去。
            var scannedPending = item.Status == RuleSetStatus.PendingRestart;

            if (!scannedPending
                && previous.TryGetValue(item.FileName, out var status)
                && status != RuleSetStatus.PendingRestart)
            {
                item.Status = status;
            }

            Items.Add(item);
        }

        UpdateSummary();
    }

    private void UpdateSummary()
    {
        var last = _settings.Current.LastRuleSetUpdate;

        // 队列里有等待替换的文件时点明，否则用户会以为已经更新到本地了
        var pending = Items.Count(item => item.Status == RuleSetStatus.PendingRestart);
        SummaryText = LocalizationService.Instance.RuleSetSummary(Items.Count, last?.LocalDateTime, pending);
    }

    [RelayCommand]
    private async Task UpdateAllAsync()
    {
        if (IsUpdating)
        {
            return;
        }

        IsUpdating = true;
        ProgressText = LocalizationService.Instance.RuleSetPreparing();

        // Progress<T> 的回调是异步投递的，最后一条可能在"完成"之后才到，把结果文案盖回进度文案。
        // 用一个标志位让迟到的回调直接失效。
        var finished = false;

        try
        {
            var progress = new Progress<(int Done, int Total, string FileName)>(p =>
            {
                if (!finished)
                {
                    ProgressText = LocalizationService.Instance.RuleSetUpdating(p.Done, p.Total);
                }
            });

            var result = await _service.UpdateAllAsync(progress);

            finished = true;
            ProgressText = LocalizationService.Instance.RuleSetDone(result.Describe());

            _main.AppendLog($"[规则集] 手动更新完成：{result.Describe()}");

            if (result.Pending > 0)
            {
                _main.AppendLog("[提示] 内核运行中，新规则集已排队；重启内核后会自动替换生效。");
            }

            foreach (var failure in result.Failures.Take(3))
            {
                _main.AppendLog($"[提示] 规则集下载失败 {failure}");
            }

            Refresh();
        }
        catch (Exception ex)
        {
            finished = true;
            ProgressText = LocalizationService.Instance.RuleSetFailed(ex.Message);
            _main.AppendLog($"[提示] 规则集更新失败：{ex.Message}");
        }
        finally
        {
            IsUpdating = false;
            UpdateSummary();
        }
    }

    [RelayCommand]
    private void ResetBaseUrl() => BaseUrl = RuleSetUpdateService.DefaultBaseUrl;
}
