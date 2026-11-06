using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using SingBoxWinUI.Services;

namespace SingBoxWinUI.ViewModels;

/// <summary>
/// 节点策略页的 ViewModel。由 App 持有单例，页面切换不重建。
/// </summary>
public partial class ProxiesViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly DispatcherQueue _dispatcher;
    private readonly AppSettingsService _settings;
    private readonly CancellationTokenSource _autoTestCts = new();
    private readonly ClashApiClientProvider _clashApi = new();

    public ProxiesViewModel(MainViewModel main, DispatcherQueue dispatcher, AppSettingsService settings)
    {
        _main = main;
        _dispatcher = dispatcher;
        _settings = settings;

        IsRunning = main.IsRunning;
        main.PropertyChanged += OnMainPropertyChanged;
        main.KernelStarted += OnKernelStarted;

        // 语言切换：分组头部的按钮文案是每条自算的，统一通知一遍
        LocalizationService.Instance.LanguageChanged += OnLanguageChanged;

        // 后台自动测速循环：在 UI 线程启动，续体回 UI 线程，集合操作才安全
        _ = RunAutoTestLoopAsync(_autoTestCts.Token);
    }

    public ObservableCollection<ProxyGroupItem> ProxyGroups { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotRunning))]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    public partial bool IsRefreshing { get; set; }

    /// <summary>未运行时显示空状态提示。</summary>
    public bool IsNotRunning => !IsRunning;

    [RelayCommand]
    private async Task RefreshAsync()
    {
        await LoadAsync(quiet: false);
    }

    /// <summary>后台定时测速：只在开关打开且内核运行中才真正执行。</summary>
    private async Task RunAutoTestLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var minutes = Math.Clamp(_settings.Current.AutoTestDelayIntervalMinutes, 1, 24 * 60);

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(minutes), ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (!_settings.Current.AutoTestDelay || !_main.IsRunning)
            {
                continue;
            }

            await RunAutoTestAsync(ct);
        }
    }

    private async Task RunAutoTestAsync(CancellationToken ct)
    {
        var api = EnsureApi(quiet: true);
        if (api is null || ProxyGroups.Count == 0)
        {
            return;
        }

        api.SpeedTestUrl = _settings.Current.SpeedTestUrl;
        api.UnifiedDelay = _settings.Current.UnifiedDelay;

        // 优先测当前展开的组；都没展开就只测第一个（主 Selector / URLTest）
        var targets = ProxyGroups.Where(group => group.IsExpanded).ToList();
        if (targets.Count == 0)
        {
            targets = ProxyGroups.Take(1).ToList();
        }

        foreach (var group in targets)
        {
            if (ct.IsCancellationRequested)
            {
                return;
            }

            try
            {
                await api.TestGroupDelayAsync(group, _dispatcher, ct);
                group.RefreshSortIfNeeded();
            }
            catch (Exception)
            {
                // 单组失败不影响其它组
            }
        }

        _main.AppendLog($"[节点] 后台自动测速完成（{targets.Count} 个策略组）");
    }

    /// <summary>切换到本页时调用：列表为空且内核在跑就自动拉一次（带端口就绪重试）。</summary>
    public async Task EnsureLoadedAsync()
    {
        if (ProxyGroups.Count > 0)
        {
            return;
        }

        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (!IsRunning)
            {
                return;
            }

            if (await LoadAsync(quiet: attempt > 0))
            {
                return;
            }

            await Task.Delay(300);
        }

        _main.AppendLog("[节点] Clash API 尚未就绪，可稍后点「刷新节点」。");
    }

    public async Task TestGroupAsync(ProxyGroupItem group)
    {
        if (group.IsTesting)
        {
            return;
        }

        var api = EnsureApi();
        if (api is null)
        {
            return;
        }

        group.IsTesting = true;
        try
        {
            // 测速参数以设置页为准，每次开测前同步一次
            api.SpeedTestUrl = _settings.Current.SpeedTestUrl;
            api.UnifiedDelay = _settings.Current.UnifiedDelay;
            await api.TestGroupDelayAsync(group, _dispatcher);

            // 正按延迟排序时，测完自动重排
            group.RefreshSortIfNeeded();
            _main.AppendLog($"[节点] {group.Name} 测速完成（{group.Nodes.Count} 个节点）");
        }
        catch (Exception ex)
        {
            _main.AppendLog($"[节点] 测速失败：{ex.Message}");
        }
        finally
        {
            group.IsTesting = false;
        }
    }

    /// <summary>切换节点。只允许操作 Selector 组。</summary>
    public async Task SelectNodeAsync(ProxyNodeItem node)
    {
        var group = ProxyGroups.FirstOrDefault(g => g.Name == node.GroupName);
        if (group is null)
        {
            return;
        }

        if (!string.Equals(group.Type, "Selector", StringComparison.OrdinalIgnoreCase))
        {
            _main.AppendLog($"[节点] {group.Name} 是 {group.Type} 组，不支持手动切换");
            return;
        }

        if (string.Equals(group.Now, node.Name, StringComparison.Ordinal))
        {
            return;
        }

        var api = EnsureApi();
        if (api is null)
        {
            return;
        }

        try
        {
            await api.SelectProxyAsync(group.Name, node.Name);

            group.Now = node.Name;
            foreach (var item in group.Nodes)
            {
                item.IsSelected = string.Equals(item.Name, node.Name, StringComparison.Ordinal);
            }

            // 客户端侧记住选择：cache.db 丢失或跨机恢复备份时靠它把 default 写回去
            RememberSelection(group.Name, node.Name);

            _main.AppendLog($"[节点] 已将 {group.Name} 切换至 {node.Name}");
        }
        catch (Exception ex)
        {
            _main.AppendLog($"[节点] 切换失败：{ex.Message}");
        }
    }

    private async Task<bool> LoadAsync(bool quiet)
    {
        if (!IsRunning || IsRefreshing)
        {
            return false;
        }

        var api = EnsureApi(quiet);
        if (api is null)
        {
            return false;
        }

        IsRefreshing = true;
        try
        {
            var groups = await api.GetProxyGroupsAsync();

            // 刷新前记住展开态与排序方式：每次刷新都重建实例，
            // 不保留的话用户刚展开第 3 组、切成"按延迟排序"，热重启后又回到第一组默认排序。
            var previous = ProxyGroups.ToDictionary(
                group => group.Name,
                group => (group.IsExpanded, group.SortMode),
                StringComparer.Ordinal);

            ProxyGroups.Clear();
            foreach (var info in groups)
            {
                var nodes = info.Nodes.Select(n => new ProxyNodeItem(n.Name, n.Type, info.Name, n.Delay)
                {
                    IsSelected = string.Equals(n.Name, info.Now, StringComparison.Ordinal),
                });

                var item = new ProxyGroupItem(info.Name, info.Type, info.Now, nodes);

                if (previous.TryGetValue(info.Name, out var state))
                {
                    item.IsExpanded = state.IsExpanded;
                    item.SortMode = state.SortMode;
                    item.ApplySort();
                }

                ProxyGroups.Add(item);
            }

            // 一组都没展开时兜底展开第一个，保证首次进入有内容可看
            if (ProxyGroups.Count > 0 && !ProxyGroups.Any(group => group.IsExpanded))
            {
                ProxyGroups[0].IsExpanded = true;
            }

            _main.AppendLog($"[节点] 已加载 {ProxyGroups.Count} 个策略组");

            // 内核刚起来时 selector 的 now 来自配置/缓存，可能与客户端记录不一致，对齐一次
            await SyncSavedSelectionsAsync(api);

            return ProxyGroups.Count > 0;
        }
        catch (Exception ex)
        {
            if (!quiet)
            {
                _main.AppendLog($"[节点] 拉取失败：{ex.Message}");
            }

            return false;
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    /// <summary>记录某个策略组手动选中的节点，并立即落盘。</summary>
    private void RememberSelection(string groupName, string nodeName)
    {
        if (string.IsNullOrEmpty(groupName) || string.IsNullOrEmpty(nodeName))
        {
            return;
        }

        if (_settings.Current.LastSelectedNodes.TryGetValue(groupName, out var existing)
            && string.Equals(existing, nodeName, StringComparison.Ordinal))
        {
            return;
        }

        _settings.Current.LastSelectedNodes[groupName] = nodeName;
        _settings.Save();
    }

    /// <summary>
    /// 内核起来后校验各 Selector 的当前节点是否与客户端记录一致。
    /// 不一致且目标节点仍存在时补一次 PUT，保证"看起来停在哪"和"实际走哪"是同一个。
    /// </summary>
    private async Task SyncSavedSelectionsAsync(ClashApiService api)
    {
        var saved = _settings.Current.LastSelectedNodes;
        if (saved.Count == 0)
        {
            return;
        }

        var aligned = 0;

        foreach (var group in ProxyGroups)
        {
            if (!string.Equals(group.Type, "Selector", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!saved.TryGetValue(group.Name, out var wanted) || string.IsNullOrWhiteSpace(wanted))
            {
                continue;
            }

            if (string.Equals(group.Now, wanted, StringComparison.Ordinal))
            {
                continue;
            }

            var node = group.Nodes.FirstOrDefault(n => string.Equals(n.Name, wanted, StringComparison.Ordinal));
            if (node is null)
            {
                // 换订阅后旧节点没了，记录留着也没用，清掉避免一直提示
                saved.Remove(group.Name);
                _settings.Save();
                continue;
            }

            try
            {
                await api.SelectProxyAsync(group.Name, wanted);

                group.Now = wanted;
                foreach (var item in group.Nodes)
                {
                    item.IsSelected = string.Equals(item.Name, wanted, StringComparison.Ordinal);
                }

                aligned++;
            }
            catch (Exception)
            {
                // 单组对齐失败不影响其它组
            }
        }

        if (aligned > 0)
        {
            _main.AppendLog($"[节点] 已按记忆恢复 {aligned} 个策略组的节点选择");
        }
    }

    private ClashApiService? EnsureApi(bool quiet = false)
    {
        var controller = _main.ClashController;
        if (string.IsNullOrWhiteSpace(controller))
        {
            if (!quiet)
            {
                _main.AppendLog("[节点] 尚未取得 clash_api 地址，请先启动内核。");
            }

            return null;
        }

        var previous = _clashApi.Current;
        var api = _clashApi.Get(controller, _main.ClashSecret);

        if (api is not null && !ReferenceEquals(previous, api) && !quiet)
        {
            _main.AppendLog($"[节点] Clash API：{api.BaseUrl}");
        }

        return api;
    }

    /// <summary>窗口关闭时停止自动测速循环并释放 HTTP 客户端。</summary>
    public void Stop()
    {
        _autoTestCts.Cancel();
        _clashApi.Dispose();
    }

    private void OnMainPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.IsRunning))
        {
            return;
        }

        IsRunning = _main.IsRunning;

        if (!IsRunning)
        {
            ProxyGroups.Clear();
        }
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        foreach (var group in ProxyGroups)
        {
            group.NotifyLocalized();
        }
    }

    private void OnKernelStarted(object? sender, EventArgs e)
    {
        // 内核可能是热重启（换订阅 / 切 TUN），节点列表必须重新拉
        ProxyGroups.Clear();
        _ = EnsureLoadedAsync();
    }
}
