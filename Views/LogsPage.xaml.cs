using System.Collections.Specialized;
using System.IO;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using SingBoxWinUI.Services;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using SingBoxWinUI.Helpers;
using SingBoxWinUI.ViewModels;

namespace SingBoxWinUI.Views;

public sealed partial class LogsPage : Page
{
    public LogsViewModel ViewModel => App.Logs;

    /// <summary>本地化入口：界面上的 {x:Bind Loc.Xxx} 走这里。</summary>
    public LocalizationService Loc => LocalizationService.Instance;


    public LogsPage()
    {
        NavigationCacheMode = NavigationCacheMode.Required;
        InitializeComponent();

        // ItemsSource 赋值会把 SelectedIndex 重置成 -1。
        // 页面 Loaded 时 ComboBox 自己的 Items 可能还没建好，直接推会被再次压成 -1，
        // 所以挪到布局跑完之后的低优先级队列里再补一次。
        Loaded += (_, _) => ScheduleComboRefresh();

        // 切语言时选项列表会整表重建，ComboBox 的选中项会在布局之后才被清掉，
        // 所以除了 VM 里的即时校正，这里再排一次低优先级校正兜住。
        LocalizationService.Instance.LanguageChanged += (_, _) => ScheduleComboRefresh();

        ViewModel.FilteredLogs.CollectionChanged += OnFilteredLogsChanged;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        // 进页面先贴到底部（AutoScroll 开启时）
        if (ViewModel.AutoScroll && ViewModel.FilteredLogs.Count > 0)
        {
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, ScrollToEnd);
        }
    }

    private void OnFilteredLogsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add || !ViewModel.AutoScroll)
        {
            return;
        }

        // 等布局跑完再滚，否则拿到的还是旧的 ScrollableHeight
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, ScrollToEnd);
    }

    private void ScrollToEnd()
    {
        if (!ViewModel.AutoScroll || ViewModel.FilteredLogs.Count == 0)
        {
            return;
        }

        LogList.ScrollIntoView(ViewModel.FilteredLogs[^1]);
    }

    private async void OnExportClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.FilteredLogs.Count == 0)
        {
            ViewModel.StatusText = Loc.Say("没有可导出的日志", "No logs to export");
            return;
        }

        var pick = await FilePickerHelper.PickSaveFileAsync(
            $"singbox-logs-{DateTime.Now:yyyyMMdd-HHmmss}.log",
            (Loc.Say("日志文件", "Log files"), ".log"),
            (Loc.Say("文本文件", "Text files"), ".txt"));

        if (pick.Path is null)
        {
            if (pick.Error is not null)
            {
                ViewModel.StatusText = pick.Error;
            }

            return;
        }

        try
        {
            await File.WriteAllTextAsync(pick.Path, ViewModel.BuildText());
            ViewModel.StatusText = Loc.Say(
                $"已导出 {ViewModel.FilteredLogs.Count} 条日志到 {pick.Name}",
                $"Exported {ViewModel.FilteredLogs.Count} entries to {pick.Name}");
        }
        catch (Exception ex)
        {
            ViewModel.StatusText = Loc.Say($"导出失败：{ex.Message}", $"Export failed: {ex.Message}");
        }
    }

    private void ScheduleComboRefresh() => DispatcherQueue.TryEnqueue(
        Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
        () => ViewModel.RefreshComboSelections());
}
