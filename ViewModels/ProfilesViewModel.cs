using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SingBoxWinUI.Models;
using SingBoxWinUI.Services;

namespace SingBoxWinUI.ViewModels;

/// <summary>
/// 订阅管理页。由 App 持有单例，页面缓存复用。
/// </summary>
public partial class ProfilesViewModel : ObservableObject
{
    /// <summary>定时更新周期选项，索引与 ComboBox 对应。</summary>
    public static readonly (string Label, int Minutes)[] Intervals =
    {
        ("不自动更新", 0),
        ("每 1 小时", 60),
        ("每 6 小时", 360),
        ("每 12 小时", 720),
        ("每 24 小时", 1440),
    };

    private readonly ProfileManagerService _profiles;
    private readonly MainViewModel _main;
    private readonly AppSettingsService _settings;
    private readonly CancellationTokenSource _periodicCts = new();

    /// <summary>定时更新周期下拉框的显示文案。</summary>
    public IReadOnlyList<string> IntervalOptions =>
    [
        LocalizationService.Instance.CommonNever, LocalizationService.Instance.CommonHour1,
        LocalizationService.Instance.CommonHour6, LocalizationService.Instance.CommonHour12,
        LocalizationService.Instance.CommonHour24,
    ];

    /// <summary>重新下发定时更新下拉框的选中索引（ItemsSource 赋值会把它重置成 -1）。</summary>
    public void RefreshComboSelections()
    {
        // ItemsSource 就位前 ComboBox 会把索引写成 -1，恢复成默认值
        if (NewIntervalIndex < 0)
        {
            NewIntervalIndex = 0;
        }

        OnPropertyChanged(nameof(NewIntervalIndex));
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(IntervalOptions));
        RefreshComboSelections();

        // 每条订阅的「N 个节点 / 最后更新 / 每 X 小时」都是派生文案，整批重算
        foreach (var item in Profiles)
        {
            item.RefreshLocalizedText();
        }
    }

    /// <summary>构造期回填开关值时不要触发保存/热重启。</summary>
    private bool _isLoading = true;

    public ProfilesViewModel(
        ProfileManagerService profiles,
        MainViewModel main,
        AppSettingsService settings)
    {
        _profiles = profiles;
        _main = main;
        _settings = settings;

        EnablePreMixin = settings.Current.EnablePreMixin;
        EnablePostMixin = settings.Current.EnablePostMixin;
        _isLoading = false;

        LocalizationService.Instance.LanguageChanged += OnLanguageChanged;

        profiles.ActiveProfileUpdated += OnActiveProfileUpdated;

        // 在 UI 线程启动，后续 await 的续体会回到 UI 线程，属性更新不会跨线程
        _ = RunPeriodicAsync(_periodicCts.Token);
    }

    public ObservableCollection<ProfileItem> Profiles => _profiles.Profiles;

    [ObservableProperty]
    public partial string NewName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NewUrl { get; set; } = string.Empty;

    /// <summary>新建表单里的周期索引，默认"不自动更新"。</summary>
    [ObservableProperty]
    public partial int NewIntervalIndex { get; set; }

    /// <summary>启用全局前置拓展。</summary>
    [ObservableProperty]
    public partial bool EnablePreMixin { get; set; }

    /// <summary>启用全局后置拓展。</summary>
    [ObservableProperty]
    public partial bool EnablePostMixin { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    public partial string StatusMessage { get; set; } = string.Empty;

    public bool HasStatus => !string.IsNullOrEmpty(StatusMessage);

    public static int MinutesAt(int index) =>
        index >= 0 && index < Intervals.Length ? Intervals[index].Minutes : 0;

    public static int IndexOfMinutes(int minutes)
    {
        for (var i = 0; i < Intervals.Length; i++)
        {
            if (Intervals[i].Minutes == minutes)
            {
                return i;
            }
        }

        return 0;
    }

    [RelayCommand]
    private async Task ImportOnlineAsync()
    {
        if (string.IsNullOrWhiteSpace(NewUrl))
        {
            StatusMessage = LocalizationService.Instance.Say("请填写订阅链接。", "Please enter a subscription URL.");
            return;
        }

        IsBusy = true;
        try
        {
            var item = await _profiles.ImportOnlineAsync(NewName, NewUrl, MinutesAt(NewIntervalIndex));
            _main.AppendLog($"[订阅] 已导入在线订阅：{item.Name}（{item.NodeCount} 个节点）");

            NewName = string.Empty;
            NewUrl = string.Empty;
            NewIntervalIndex = 0;
            StatusMessage = LocalizationService.Instance.Say($"已导入「{item.Name}」，{item.NodeCount} 个节点。", $"Imported \"{item.Name}\" with {item.NodeCount} nodes.");
        }
        catch (Exception ex)
        {
            StatusMessage = LocalizationService.Instance.Say($"导入失败：{ex.Message}", $"Import failed: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task UpdateAllAsync()
    {
        var targets = Profiles.Where(p => p.IsRemote).ToList();
        if (targets.Count == 0)
        {
            StatusMessage = LocalizationService.Instance.Say("没有可更新的在线订阅。", "No remote profiles to update.");
            return;
        }

        IsBusy = true;
        var ok = 0;
        var failed = new List<string>();

        try
        {
            foreach (var item in targets)
            {
                try
                {
                    await _profiles.UpdateProfileAsync(item);
                    ok++;
                }
                catch (Exception ex)
                {
                    failed.Add($"{item.Name}（{ex.Message}）");
                }
            }

            _main.AppendLog($"[订阅] 全部更新完成：成功 {ok} 个，失败 {failed.Count} 个");
            StatusMessage = failed.Count == 0
                ? $"已更新 {ok} 个订阅。"
                : $"更新 {ok} 个，失败 {failed.Count} 个：{string.Join("；", failed)}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>启用某个订阅：切换 ConfigPath，运行中则重启内核。</summary>
    public async Task EnableAsync(ProfileItem item)
    {
        _profiles.SetActive(item);
        _main.SetConfigPath(_profiles.GetProfilePath(item));
        _main.AppendLog($"[订阅] 已启用：{item.Name}（{item.NodeCount} 个节点）");

        if (_main.IsRunning)
        {
            await _main.RestartCoreAsync("[状态] 正在重启内核以加载新订阅...");
        }

        StatusMessage = LocalizationService.Instance.Say($"已切换到「{item.Name}」。", $"Switched to \"{item.Name}\".");
    }

    public async Task UpdateAsync(ProfileItem item)
    {
        if (!item.IsRemote)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await _profiles.UpdateProfileAsync(item);
            _main.AppendLog($"[订阅] {item.Name} 已更新（{item.NodeCount} 个节点）");
            StatusMessage = LocalizationService.Instance.Say($"「{item.Name}」已更新，{item.NodeCount} 个节点。", $"\"{item.Name}\" updated with {item.NodeCount} nodes.");
        }
        catch (Exception ex)
        {
            StatusMessage = LocalizationService.Instance.Say($"更新「{item.Name}」失败：{ex.Message}", $"Failed to update \"{item.Name}\": {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void EditProfile(ProfileItem item, string name, string url, int intervalMinutes)
    {
        _profiles.UpdateMetadata(item, name, url, intervalMinutes);
        StatusMessage = LocalizationService.Instance.Say($"已保存「{item.Name}」的修改。", $"Changes to \"{item.Name}\" saved.");
    }

    /// <summary>删除：唯一订阅或运行中的激活订阅不允许删。</summary>
    public void DeleteProfile(ProfileItem item)
    {
        if (Profiles.Count <= 1)
        {
            StatusMessage = LocalizationService.Instance.Say("至少需要保留一个订阅。", "At least one profile must be kept.");
            return;
        }

        if (item.IsActive && _main.IsRunning)
        {
            StatusMessage = LocalizationService.Instance.Say("当前订阅正在使用，请先停止内核再删除。", "This profile is in use; stop the core before deleting it.");
            return;
        }

        var wasActive = item.IsActive;
        _profiles.Delete(item);
        _main.AppendLog($"[订阅] 已删除：{item.Name}");

        // 删的是当前激活项：把 ConfigPath 切到新的激活订阅，否则会指向已删文件
        if (wasActive && _profiles.ActiveProfile is { } next)
        {
            _main.SetConfigPath(_profiles.GetProfilePath(next));
            _main.AppendLog($"[订阅] 已自动切换到：{next.Name}");
        }

        StatusMessage = LocalizationService.Instance.Say($"已删除「{item.Name}」。", $"Deleted \"{item.Name}\"");
    }

    public void ImportLocal(string path)
    {
        try
        {
            var item = _profiles.ImportLocal(path);
            _main.AppendLog($"[订阅] 已导入本地配置：{item.Name}（{item.NodeCount} 个节点）");
            StatusMessage = LocalizationService.Instance.Say($"已导入「{item.Name}」，{item.NodeCount} 个节点。", $"Imported \"{item.Name}\" with {item.NodeCount} nodes.");
        }
        catch (Exception ex)
        {
            StatusMessage = LocalizationService.Instance.Say($"导入失败：{ex.Message}", $"Import failed: {ex.Message}");
        }
    }

    partial void OnEnablePreMixinChanged(bool value)
    {
        if (_isLoading)
        {
            return;
        }

        _settings.Current.EnablePreMixin = value;
        _settings.Save();
        _ = ApplyMixinToggleAsync(value, isPre: true);
    }

    partial void OnEnablePostMixinChanged(bool value)
    {
        if (_isLoading)
        {
            return;
        }

        _settings.Current.EnablePostMixin = value;
        _settings.Save();
        _ = ApplyMixinToggleAsync(value, isPre: false);
    }

    /// <summary>拓展 JSON 保存成功后调用：已开启且内核在跑就热重启。</summary>
    public async Task OnMixinSavedAsync(bool isPre)
    {
        var label = isPre ? LocalizationService.Instance.ProfilesPreMixin : LocalizationService.Instance.ProfilesPostMixin;
        var enabled = isPre ? EnablePreMixin : EnablePostMixin;

        if (!enabled)
        {
            StatusMessage = $"{label}已保存（当前未启用）。";
            return;
        }

        if (!_main.IsRunning)
        {
            StatusMessage = $"{label}已保存，将在下次启动内核时生效。";
            return;
        }

        StatusMessage = $"{label}已保存，正在重启内核…";
        await _main.RestartCoreAsync($"[状态] {label}已更新，正在重启内核以应用...");
        StatusMessage = $"{label}已保存并生效。";
    }

    private async Task ApplyMixinToggleAsync(bool enabled, bool isPre)
    {
        var label = isPre ? LocalizationService.Instance.ProfilesPreMixin : LocalizationService.Instance.ProfilesPostMixin;

        if (!_main.IsRunning)
        {
            StatusMessage = $"{label}已{(enabled ? "启用" : "关闭")}，将在下次启动内核时生效。";
            return;
        }

        StatusMessage = $"正在重启内核以{(enabled ? "应用" : "移除")}{label}…";
        await _main.RestartCoreAsync($"[状态] 正在重启内核以{(enabled ? "应用" : "移除")}{label}...");
        StatusMessage = $"{label}已{(enabled ? "启用" : "关闭")}并生效。";
    }

    private void OnActiveProfileUpdated(object? sender, ProfileItem item)
    {
        _main.AppendLog("[订阅] 当前激活订阅已更新");

        if (_main.IsRunning)
        {
            _ = _main.RestartCoreAsync("[状态] 激活订阅已更新，正在重启内核以应用新节点...");
        }
    }

    /// <summary>恢复备份后把 Mixin 开关重新对齐到磁盘（不触发保存与热重启）。</summary>
    public void ReloadFromSettings()
    {
        _isLoading = true;
        try
        {
            EnablePreMixin = _settings.Current.EnablePreMixin;
            EnablePostMixin = _settings.Current.EnablePostMixin;
        }
        finally
        {
            _isLoading = false;
        }
    }

    /// <summary>窗口关闭时停止定时更新循环。</summary>
    public void Stop() => _periodicCts.Cancel();

    /// <summary>每 60 秒检查一次到期的在线订阅。</summary>
    private async Task RunPeriodicAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(60));

        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                var due = Profiles.Where(_profiles.IsDue).ToList();

                foreach (var item in due)
                {
                    if (ct.IsCancellationRequested)
                    {
                        return;
                    }

                    try
                    {
                        await _profiles.UpdateProfileAsync(item);
                        _main.AppendLog($"[订阅] {item.Name} 自动更新完成（{item.NodeCount} 个节点）");
                    }
                    catch (Exception ex)
                    {
                        _main.AppendLog($"[订阅] {item.Name} 自动更新失败：{ex.Message}");
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 退出时正常取消
        }
    }
}
