using Microsoft.UI.Xaml;
using SingBoxWinUI.Services;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using SingBoxWinUI.ViewModels;

namespace SingBoxWinUI.Views;

public sealed partial class HomePage : Page
{
    public MainViewModel ViewModel => App.Main;

    /// <summary>本地化入口：界面上的 {x:Bind Loc.Xxx} 走这里。</summary>
    public LocalizationService Loc => LocalizationService.Instance;


    public HomePage()
    {
        // 缓存页面实例：来回切侧边栏不会重建，日志滚动位置与内核状态都不丢
        NavigationCacheMode = NavigationCacheMode.Required;

        InitializeComponent();
    }

    /// <summary>x:Bind 函数绑定用：非管理员时显示提权提示条。</summary>
    private bool NotAdmin(bool isAdmin) => !isAdmin;

    /// <summary>x:Bind 函数绑定用：判断当前代理模式索引。</summary>
    private bool IsMode(int index, int current) => index == current;

    private void OnClashModeChecked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string mode })
        {
            ViewModel.SetClashModeByName(mode);
        }
    }

    private void OnManageProfilesClick(object sender, RoutedEventArgs e)
    {
        // 配置切换统一收敛到订阅管理页
        App.RequestNavigation("profiles");
    }

    private void OnDismissAdminHintClick(object sender, RoutedEventArgs e) =>
        ViewModel.IsAdminHintDismissed = true;

    private void OnElevateClick(object sender, RoutedEventArgs e)
    {
        ViewModel.RestartAsAdmin();
    }

    /// <summary>主页订阅卡片上的快捷更新。</summary>
    private async void OnUpdateSubscriptionClick(object sender, RoutedEventArgs e)
    {
        var profile = App.ProfileManager.ActiveProfile;
        if (profile is null)
        {
            return;
        }

        await App.Profiles.UpdateAsync(profile);
    }
}
