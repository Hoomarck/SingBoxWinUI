using Microsoft.UI.Xaml;
using SingBoxWinUI.Services;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using SingBoxWinUI.ViewModels;

namespace SingBoxWinUI.Views;

public sealed partial class ProxiesPage : Page
{
    public ProxiesViewModel ViewModel => App.Proxies;

    /// <summary>本地化入口：界面上的 {x:Bind Loc.Xxx} 走这里。</summary>
    public LocalizationService Loc => LocalizationService.Instance;


    public ProxiesPage()
    {
        // 与 HomePage 一样缓存实例：切走再切回来不会重建、不会重复拉取
        NavigationCacheMode = NavigationCacheMode.Required;

        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        // 列表为空且内核在跑时自动拉一次（方法内部会先判断，已有数据直接返回）
        _ = ViewModel.EnsureLoadedAsync();
    }

    private async void OnNodeClick(object sender, RoutedEventArgs e)
    {
        // ItemsRepeater 的模板根不保证有 DataContext，所以模板里用 Tag="{x:Bind}" 显式传 item
        if (sender is FrameworkElement element && (element.Tag ?? element.DataContext) is ProxyNodeItem node)
        {
            await ViewModel.SelectNodeAsync(node);
        }
    }

    private async void OnTestGroupClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && (element.Tag ?? element.DataContext) is ProxyGroupItem group)
        {
            await ViewModel.TestGroupAsync(group);
        }
    }

    private void OnSortClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && (element.Tag ?? element.DataContext) is ProxyGroupItem group)
        {
            group.CycleSort();
        }
    }
}
