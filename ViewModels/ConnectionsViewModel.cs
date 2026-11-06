using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SingBoxWinUI.Helpers;
using SingBoxWinUI.Services;

namespace SingBoxWinUI.ViewModels;

/// <summary>
/// 活动连接页。每秒拉一次 /connections 快照，按字节差算实时速度，
/// 并对 ObservableCollection 做原位增量同步（不 Clear，避免滚动条跳动）。
/// </summary>
public partial class ConnectionsViewModel : ObservableObject
{
    /// <summary>"全部出站"的显示文案。它是下拉框里的哨兵值，切换语言时要连带迁移选中项。</summary>
    private string _allOutboundsLabel = LocalizationService.Instance.AllOutbounds();

    public string AllOutbounds => _allOutboundsLabel;

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly MainViewModel _main;
    private readonly AppSettingsService _settings;
    private readonly CancellationTokenSource _pollCts = new();
    private readonly ClashApiClientProvider _clashApi = new();

    /// <summary>唤醒信号：页面可见 / 内核恢复运行 / 取消暂停时把轮询循环叫醒。</summary>
    private readonly SemaphoreSlim _wakeup = new(0, 1);

    private readonly Dictionary<string, ConnectionItem> _itemsById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (long Upload, long Download, long Ticks)> _prevSnapshot = new(StringComparer.Ordinal);
    private readonly HashSet<string> _outboundSet = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>ConnectionItem 在 Connections 里的位置，避免循环内 IndexOf 的 O(n)。</summary>
    private readonly Dictionary<ConnectionItem, int> _index = new();

    private bool _isActive;
    private bool _isRefreshing;
    private bool _isLoadingColumns = true;

    /// <summary>轻量模式中：不轮询、不接受页面激活，连接集合保持为空。</summary>
    private bool _isInLightweightMode;

    public ConnectionsViewModel(MainViewModel main, AppSettingsService settings)
    {
        _main = main;
        _settings = settings;

        Columns = new ConnectionColumnSettings();
        LoadColumns(settings.Current.ConnectionColumns);
        _isLoadingColumns = false;
        Columns.PropertyChanged += OnColumnsChanged;

        OutboundOptions.Add(AllOutbounds);
        SelectedOutbound = AllOutbounds;

        main.PropertyChanged += OnMainPropertyChanged;
        LocalizationService.Instance.LanguageChanged += OnLanguageChanged;

        // 在 UI 线程启动，后续 await 续体回到 UI 线程，集合操作才安全
        _ = RunPollLoopAsync(_pollCts.Token);
    }

    public ObservableCollection<ConnectionItem> Connections { get; } = new();

    public ObservableCollection<string> OutboundOptions { get; } = new();

    public ConnectionColumnSettings Columns { get; }

    /// <summary>页面是否在前台；不可见时不轮询，也不产生任何周期唤醒。</summary>
    public bool IsActive
    {
        get => _isActive;
        set
        {
            // 轻量模式期间不允许被页面重新激活：窗口还藏着，激活就等于恢复每秒拉取
            if (value && _isInLightweightMode)
            {
                return;
            }

            if (_isActive == value)
            {
                return;
            }

            _isActive = value;

            if (value)
            {
                WakeUp();
            }
        }
    }

    public bool IsRunning => _main.IsRunning;

    public bool IsNotRunning => !IsRunning;

    /// <summary>
    /// 进入轻量模式：暂停轮询并释放全部 ConnectionItem 引用。
    /// 每条 ConnectionItem 都带一串字符串与 Brush 引用，几百条连接能占十几 MB；
    /// 只停轮询不清表，这部分内存会一直挂到窗口重新可见。
    /// 必须在 UI 线程调用（会改动 Connections 集合）。
    /// </summary>
    public void EnterLightweightMode()
    {
        _isInLightweightMode = true;
        IsActive = false;

        _itemsById.Clear();
        _prevSnapshot.Clear();
        ClearConnections();

        ConnectionCount = 0;
        TotalSpeedText = "↓ 0 B/s  ↑ 0 B/s";
        TotalTrafficText = "↓ 0 B  ↑ 0 B";
        OnPropertyChanged(nameof(HasNoConnections));
    }

    /// <summary>退出轻量模式：只有当前停在连接页时才恢复拉取。</summary>
    public void ExitLightweightMode(bool onConnectionsPage)
    {
        _isInLightweightMode = false;
        IsActive = onConnectionsPage;

        if (onConnectionsPage)
        {
            _ = RefreshNowAsync();
        }
    }

    [ObservableProperty]
    public partial bool IsPaused { get; set; }

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SelectedOutbound { get; set; } = string.Empty;

    /// <summary>排序维度索引，对应 ConnectionSortMode。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SortDirectionText))]
    public partial int SortModeIndex { get; set; } = (int)ConnectionSortMode.DownloadSpeed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SortDirectionText))]
    public partial bool SortDescending { get; set; } = true;

    [ObservableProperty]
    public partial int ConnectionCount { get; set; }

    [ObservableProperty]
    public partial string TotalSpeedText { get; set; } = "↓ 0 B/s  ↑ 0 B/s";

    [ObservableProperty]
    public partial string TotalTrafficText { get; set; } = "↓ 0 B  ↑ 0 B";

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = string.Empty;

    public string SortDirectionText => SortDescending ? "↓" : "↑";

    /// <summary>排序维度下拉框的显示文案，索引与 ConnectionSortMode 对应。</summary>
    public IReadOnlyList<string> SortModeOptions =>
    [
        LocalizationService.Instance.ConnColStart, LocalizationService.Instance.ConnColDownloadRate,
        LocalizationService.Instance.ConnColUploadRate, LocalizationService.Instance.ConnColDownloadTotal,
        LocalizationService.Instance.ConnColUploadTotal, LocalizationService.Instance.ConnColHost,
        LocalizationService.Instance.ConnColOutboundGroup,
    ];

    public string ConnectionCountText => LocalizationService.Instance.ConnectionsCount(ConnectionCount);

    public bool HasNoConnections => IsRunning && Connections.Count == 0;

    /// <summary>空列表时的文案：筛选无结果和真的没连接要区分开。</summary>
    public string EmptyHintText =>
        !string.IsNullOrWhiteSpace(SearchText)
        || !string.Equals(SelectedOutbound, AllOutbounds, StringComparison.Ordinal)
            ? LocalizationService.Instance.ConnEmptyFiltered
            : LocalizationService.Instance.ConnEmpty;

    partial void OnSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(EmptyHintText));
        ApplyFilterAndSort();
    }

    partial void OnSelectedOutboundChanged(string value)
    {
        OnPropertyChanged(nameof(EmptyHintText));
        ApplyFilterAndSort();
    }

    partial void OnSortModeIndexChanged(int value)
    {
        // 换 ItemsSource 时 ComboBox 会先把 SelectedIndex 打成 -1 再写回
        if (value < 0)
        {
            return;
        }

        ApplyFilterAndSort();
    }

    partial void OnSortDescendingChanged(bool value) => ApplyFilterAndSort();

    partial void OnConnectionCountChanged(int value) => OnPropertyChanged(nameof(ConnectionCountText));

    partial void OnIsPausedChanged(bool value) => WakeUp();

    private void OnMainPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.IsRunning))
        {
            return;
        }

        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsNotRunning));
        OnPropertyChanged(nameof(HasNoConnections));

        if (!IsRunning)
        {
            // 内核停了，连接表全部失效
            _itemsById.Clear();
            _prevSnapshot.Clear();
            ClearConnections();
            ConnectionCount = 0;
            TotalSpeedText = "↓ 0 B/s  ↑ 0 B/s";
            TotalTrafficText = "↓ 0 B  ↑ 0 B";
            return;
        }

        // 内核重新起来：叫醒轮询循环（否则它会一直挂在信号量上）
        WakeUp();
    }

    /// <summary>立即刷新一次（页面进入时调用）。</summary>
    public async Task RefreshNowAsync()
    {
        if (!IsRunning)
        {
            return;
        }

        await RefreshAsync();
    }

    [RelayCommand]
    private void ToggleSortDirection() => SortDescending = !SortDescending;

    [RelayCommand]
    public async Task CloseAllAsync()
    {
        var api = EnsureApi();
        if (api is null)
        {
            return;
        }

        try
        {
            await api.CloseAllConnectionsAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = LocalizationService.Instance.Say($"全部断开失败：{ex.Message}", $"Failed to close all: {ex.Message}");
            return;
        }

        _itemsById.Clear();
        _prevSnapshot.Clear();
        ClearConnections();
        ConnectionCount = 0;
        _main.AppendLog("[连接] 已断开全部活动连接");
    }

    public async Task CloseConnectionAsync(ConnectionItem item)
    {
        var api = EnsureApi();
        if (api is null)
        {
            return;
        }

        try
        {
            await api.CloseConnectionAsync(item.Id);
        }
        catch (Exception ex)
        {
            StatusMessage = LocalizationService.Instance.Say($"断开失败：{ex.Message}", $"Failed to close: {ex.Message}");
            return;
        }

        _itemsById.Remove(item.Id);
        _prevSnapshot.Remove(item.Id);
        ApplyFilterAndSort();
        _main.AppendLog($"[连接] 已断开：{item.Target}");
    }

    private async Task RunPollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            // 不可见 / 暂停 / 内核未运行：挂在信号量上，零周期唤醒。
            // 旧实现用 PeriodicTimer 每秒空转一次，隐藏到托盘 + 轻量模式下依然每秒醒来。
            if (!IsActive || IsPaused || !_main.IsRunning)
            {
                try
                {
                    await _wakeup.WaitAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            try
            {
                await Task.Delay(PollInterval, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (!IsActive || IsPaused || !_main.IsRunning)
            {
                continue;
            }

            await RefreshAsync();
        }
    }

    /// <summary>唤醒轮询循环（幂等）。</summary>
    private void WakeUp()
    {
        try
        {
            if (_wakeup.CurrentCount == 0)
            {
                _wakeup.Release();
            }
        }
        catch (SemaphoreFullException)
        {
            // 已有待处理的唤醒信号
        }
    }

    /// <summary>窗口关闭时停止轮询并释放 HTTP 客户端。</summary>
    public void Stop()
    {
        _pollCts.Cancel();
        _clashApi.Dispose();
    }

    private async Task RefreshAsync()
    {
        if (_isRefreshing)
        {
            return;
        }

        var api = EnsureApi();
        if (api is null)
        {
            return;
        }

        _isRefreshing = true;
        try
        {
            var snapshot = await api.GetConnectionsAsync();
            ApplySnapshot(snapshot);
        }
        catch (Exception)
        {
            // 内核重启瞬间会 5xx / 连接被拒，静默跳过这一轮
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    private void ApplySnapshot(ConnectionsSnapshot snapshot)
    {
        var nowTicks = Environment.TickCount64;
        var alive = new HashSet<string>(snapshot.Connections.Count, StringComparer.Ordinal);
        double totalUpSpeed = 0;
        double totalDownSpeed = 0;

        foreach (var info in snapshot.Connections)
        {
            alive.Add(info.Id);

            if (!_itemsById.TryGetValue(info.Id, out var item))
            {
                item = new ConnectionItem(info, Columns);
                _itemsById[info.Id] = item;
            }

            // 实时速度 = 相邻两次快照的字节差 / 时间差
            double upSpeed = 0;
            double downSpeed = 0;

            if (_prevSnapshot.TryGetValue(info.Id, out var prev))
            {
                var seconds = (nowTicks - prev.Ticks) / 1000.0;
                if (seconds > 0.05)
                {
                    upSpeed = Math.Max(0, info.Upload - prev.Upload) / seconds;
                    downSpeed = Math.Max(0, info.Download - prev.Download) / seconds;
                }
            }

            item.Update(info, upSpeed, downSpeed);
            _prevSnapshot[info.Id] = (info.Upload, info.Download, nowTicks);

            // 顺路累加，省掉两次 LINQ 遍历
            totalUpSpeed += upSpeed;
            totalDownSpeed += downSpeed;
        }

        // 已断开的连接
        var stale = new List<string>();
        foreach (var id in _itemsById.Keys)
        {
            if (!alive.Contains(id))
            {
                stale.Add(id);
            }
        }

        foreach (var id in stale)
        {
            _itemsById.Remove(id);
            _prevSnapshot.Remove(id);
        }

        ConnectionCount = _itemsById.Count;
        TotalSpeedText =
            $"↓ {FormatHelper.FormatSpeed(totalDownSpeed)}"
            + $"  ↑ {FormatHelper.FormatSpeed(totalUpSpeed)}";
        TotalTrafficText =
            $"↓ {FormatHelper.FormatBytes(snapshot.DownloadTotal)}"
            + $"  ↑ {FormatHelper.FormatBytes(snapshot.UploadTotal)}";

        OnPropertyChanged(nameof(HasNoConnections));

        UpdateOutboundOptions();
        ApplyFilterAndSort();
    }

    /// <summary>把当前筛选/排序结果同步到 ObservableCollection，只做增删和 Move。</summary>
    private void ApplyFilterAndSort()
    {
        var target = SortItems(_itemsById.Values.Where(Matches)).ToList();

        // 1) 删掉不该出现的
        var targetSet = new HashSet<ConnectionItem>(target);
        for (var i = Connections.Count - 1; i >= 0; i--)
        {
            if (!targetSet.Contains(Connections[i]))
            {
                RemoveAt(i);
            }
        }

        // 2) 按目标顺序原地摆放，已存在就 Move，避免整表重建。
        //    位置查询走 _index（O(1)）；旧实现用 Connections.IndexOf 在循环里做线性查找，
        //    500 条连接时每秒约 25 万次比较。
        for (var i = 0; i < target.Count; i++)
        {
            var item = target[i];
            var current = _index.TryGetValue(item, out var index) ? index : -1;

            if (current < 0)
            {
                Insert(i, item);
            }
            else if (current != i)
            {
                MoveItem(current, i);
            }
        }

        OnPropertyChanged(nameof(HasNoConnections));
    }

    private void Insert(int index, ConnectionItem item)
    {
        Connections.Insert(index, item);
        ReindexFrom(index);
    }

    private void RemoveAt(int index)
    {
        var item = Connections[index];
        Connections.RemoveAt(index);
        _index.Remove(item);
        ReindexFrom(index);
    }

    private void MoveItem(int from, int to)
    {
        Connections.Move(from, to);
        ReindexFrom(Math.Min(from, to));
    }

    /// <summary>从指定下标起重建位置索引（插入/删除/Move 之后调用）。</summary>
    private void ReindexFrom(int start)
    {
        for (var i = start; i < Connections.Count; i++)
        {
            _index[Connections[i]] = i;
        }
    }

    private void ClearConnections()
    {
        _index.Clear();
        Connections.Clear();
    }

    private bool Matches(ConnectionItem item)
    {
        if (!string.IsNullOrEmpty(SelectedOutbound)
            && !string.Equals(SelectedOutbound, AllOutbounds, StringComparison.Ordinal))
        {
            if (!item.Chains.Any(chain => string.Equals(chain, SelectedOutbound, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }
        }

        var keyword = SearchText?.Trim();
        return string.IsNullOrEmpty(keyword)
               || item.SearchBlob.Contains(keyword, StringComparison.OrdinalIgnoreCase);
    }

    private IEnumerable<ConnectionItem> SortItems(IEnumerable<ConnectionItem> items)
    {
        var asc = !SortDescending;

        return (ConnectionSortMode)SortModeIndex switch
        {
            ConnectionSortMode.UploadSpeed => asc ? items.OrderBy(i => i.UpSpeed) : items.OrderByDescending(i => i.UpSpeed),
            ConnectionSortMode.DownloadTotal => asc ? items.OrderBy(i => i.Download) : items.OrderByDescending(i => i.Download),
            ConnectionSortMode.UploadTotal => asc ? items.OrderBy(i => i.Upload) : items.OrderByDescending(i => i.Upload),
            ConnectionSortMode.Host => asc
                ? items.OrderBy(i => i.Target, StringComparer.CurrentCultureIgnoreCase)
                : items.OrderByDescending(i => i.Target, StringComparer.CurrentCultureIgnoreCase),
            ConnectionSortMode.Outbound => asc
                ? items.OrderBy(i => i.ChainPrimary, StringComparer.CurrentCultureIgnoreCase)
                : items.OrderByDescending(i => i.ChainPrimary, StringComparer.CurrentCultureIgnoreCase),
            ConnectionSortMode.StartTime => asc ? items.OrderBy(i => i.Start) : items.OrderByDescending(i => i.Start),
            _ => asc ? items.OrderBy(i => i.DownSpeed) : items.OrderByDescending(i => i.DownSpeed),
        };
    }

    /// <summary>
    /// 语言切换：哨兵文案变了，下拉框首项与当前选中项都要跟着换，
    /// 否则过滤逻辑会把"全部出站"当成一个具体出站 tag。
    /// </summary>
    /// <summary>重新下发排序维度下拉框的选中索引（ItemsSource 赋值会把它重置成 -1）。</summary>
    public void RefreshComboSelections()
    {
        // ItemsSource 就位前 ComboBox 会把索引写成 -1，恢复成默认排序维度
        if (SortModeIndex < 0)
        {
            SortModeIndex = (int)ConnectionSortMode.DownloadSpeed;
        }

        OnPropertyChanged(nameof(SortModeIndex));
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        var newLabel = LocalizationService.Instance.AllOutbounds();
        if (string.Equals(newLabel, _allOutboundsLabel, StringComparison.Ordinal))
        {
            return;
        }

        var oldLabel = _allOutboundsLabel;
        var index = OutboundOptions.IndexOf(oldLabel);
        if (index >= 0)
        {
            OutboundOptions[index] = newLabel;
        }

        if (string.Equals(SelectedOutbound, oldLabel, StringComparison.Ordinal))
        {
            SelectedOutbound = newLabel;
        }

        _allOutboundsLabel = newLabel;

        OnPropertyChanged(nameof(AllOutbounds));
        OnPropertyChanged(nameof(SortModeOptions));
        RefreshComboSelections();
        OnPropertyChanged(nameof(ConnectionCountText));
        OnPropertyChanged(nameof(EmptyHintText));
    }

    private void UpdateOutboundOptions()
    {
        var tags = _itemsById.Values
            .SelectMany(i => i.Chains)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(tag => tag, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        if (tags.Count == _outboundSet.Count && tags.All(_outboundSet.Contains))
        {
            return;
        }

        _outboundSet.Clear();
        foreach (var tag in tags)
        {
            _outboundSet.Add(tag);
        }

        var selected = SelectedOutbound;

        OutboundOptions.Clear();
        OutboundOptions.Add(AllOutbounds);
        foreach (var tag in tags)
        {
            OutboundOptions.Add(tag);
        }

        SelectedOutbound = OutboundOptions.Contains(selected) ? selected : AllOutbounds;
    }

    private ClashApiService? EnsureApi() => _clashApi.Get(_main.ClashController, _main.ClashSecret);

    /// <summary>恢复备份后把列显示设置重新对齐到磁盘（不触发落盘）。</summary>
    public void ReloadFromSettings()
    {
        _isLoadingColumns = true;
        try
        {
            LoadColumns(_settings.Current.ConnectionColumns);
        }
        finally
        {
            _isLoadingColumns = false;
        }
    }

    private void LoadColumns(ConnectionColumnState state)
    {
        Columns.ShowInbound = state.ShowInbound;
        Columns.ShowProcess = state.ShowProcess;
        Columns.ShowRule = state.ShowRule;
        Columns.ShowChains = state.ShowChains;
        Columns.ShowSpeed = state.ShowSpeed;
        Columns.ShowTraffic = state.ShowTraffic;
        Columns.ShowIp = state.ShowIp;
        Columns.ShowDuration = state.ShowDuration;
    }

    private void OnColumnsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_isLoadingColumns)
        {
            return;
        }

        _settings.Current.ConnectionColumns = new ConnectionColumnState
        {
            ShowInbound = Columns.ShowInbound,
            ShowProcess = Columns.ShowProcess,
            ShowRule = Columns.ShowRule,
            ShowChains = Columns.ShowChains,
            ShowSpeed = Columns.ShowSpeed,
            ShowTraffic = Columns.ShowTraffic,
            ShowIp = Columns.ShowIp,
            ShowDuration = Columns.ShowDuration,
        };

        _settings.Save();
    }
}
