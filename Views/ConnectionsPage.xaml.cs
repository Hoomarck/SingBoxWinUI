using Microsoft.UI.Xaml;
using SingBoxWinUI.Services;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using SingBoxWinUI.Helpers;
using SingBoxWinUI.ViewModels;

namespace SingBoxWinUI.Views;

public sealed partial class ConnectionsPage : Page
{
    /// <summary>WinUI 同时只允许一个 ContentDialog。</summary>
    private readonly DialogHost _dialogs = new();

    public ConnectionsViewModel ViewModel => App.Connections;

    /// <summary>本地化入口：界面上的 {x:Bind Loc.Xxx} 走这里。</summary>
    public LocalizationService Loc => LocalizationService.Instance;


    public ConnectionsPage()
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
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        // 只有页面可见时才轮询，切走就停
        ViewModel.IsActive = true;
        _ = ViewModel.RefreshNowAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        ViewModel.IsActive = false;
    }

    private async void OnCloseConnectionClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ConnectionItem item })
        {
            await ViewModel.CloseConnectionAsync(item);
        }
    }

    private async void OnCloseAllClick(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = Loc.Say("断开全部连接", "Close All Connections"),
            Content = Loc.Say(
                $"将断开当前全部 {ViewModel.ConnectionCount} 条活动连接，确定继续？",
                $"This will close all {ViewModel.ConnectionCount} active connections. Continue?"),
            PrimaryButtonText = Loc.Say("全部断开", "Close All"),
            CloseButtonText = Loc.CommonCancel,
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };

        if (await _dialogs.ShowAsync(dialog) == ContentDialogResult.Primary)
        {
            await ViewModel.CloseAllAsync();
        }
    }

    private void ScheduleComboRefresh() => DispatcherQueue.TryEnqueue(
        Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
        () => ViewModel.RefreshComboSelections());
}
