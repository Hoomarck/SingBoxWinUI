using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using SingBoxWinUI.Helpers;
using SingBoxWinUI.Models;
using SingBoxWinUI.Services;
using SingBoxWinUI.ViewModels;

namespace SingBoxWinUI.Views;

public sealed partial class ProfilesPage : Page
{
    public ProfilesViewModel ViewModel => App.Profiles;

    /// <summary>WinUI 同时只允许一个 ContentDialog，重复 ShowAsync 会直接抛异常。</summary>
    private readonly DialogHost _dialogs = new();

    /// <summary>本地化入口：界面上的 {x:Bind Loc.Xxx} 走这里。</summary>
    public LocalizationService Loc => LocalizationService.Instance;


    public ProfilesPage()
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

    private async void OnImportLocalClick(object sender, RoutedEventArgs e)
    {
        var pick = await FilePickerHelper.PickOpenFileAsync(".json");
        if (pick.Path is null)
        {
            // 用户取消才静默返回；对话框真失败时必须给明确提示，不能又是「点了没反应」
            if (pick.Error is not null)
            {
                await ShowImportErrorAsync(pick.Error);
            }

            return;
        }

        // ImportLocal 返回 false 时 StatusMessage 里已是本地化的失败原因
        if (!ViewModel.ImportLocal(pick.Path))
        {
            await ShowImportErrorAsync(ViewModel.StatusMessage);
        }
    }

    private async Task ShowImportErrorAsync(string message)
    {
        var dialog = new ContentDialog
        {
            Title = LocalizationService.Instance.ProfilesImportFailedTitle,
            Content = message,
            CloseButtonText = LocalizationService.Instance.CommonOk,
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };

        await _dialogs.ShowAsync(dialog);
    }

    private async void OnEnableClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ProfileItem item })
        {
            await ViewModel.EnableAsync(item);
        }
    }

    private async void OnUpdateClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ProfileItem item })
        {
            await ViewModel.UpdateAsync(item);
        }
    }

    private async void OnEditClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ProfileItem item })
        {
            return;
        }

        var nameBox = new TextBox
        {
            Header = LocalizationService.Instance.ProfilesNewName,
            Text = item.Name,
        };

        var urlBox = new TextBox
        {
            Header = LocalizationService.Instance.ProfilesNewUrl,
            Text = item.Url,
            IsEnabled = item.IsRemote,
            PlaceholderText = item.IsRemote ? "https://..." : LocalizationService.Instance.ProfilesLocalUrlReadonly,
        };

        var intervalBox = new ComboBox
        {
            Header = LocalizationService.Instance.ProfilesInterval,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        foreach (var (label, _) in ProfilesViewModel.Intervals)
        {
            intervalBox.Items.Add(label);
        }

        intervalBox.SelectedIndex = ProfilesViewModel.IndexOfMinutes(item.UpdateIntervalMinutes);

        var panel = new StackPanel { Spacing = 12, MinWidth = 360 };
        panel.Children.Add(nameBox);
        panel.Children.Add(urlBox);
        panel.Children.Add(intervalBox);

        var dialog = new ContentDialog
        {
            Title = LocalizationService.Instance.ProfilesEditTitle,
            Content = panel,
            PrimaryButtonText = LocalizationService.Instance.CommonSave,
            CloseButtonText = LocalizationService.Instance.CommonCancel,
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        if (await _dialogs.ShowAsync(dialog) == ContentDialogResult.Primary)
        {
            ViewModel.EditProfile(
                item,
                nameBox.Text,
                urlBox.Text,
                ProfilesViewModel.MinutesAt(intervalBox.SelectedIndex));
        }
    }

    private async void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ProfileItem item })
        {
            return;
        }

        if (ViewModel.Profiles.Count <= 1)
        {
            ViewModel.DeleteProfile(item);
            return;
        }

        var dialog = new ContentDialog
        {
            Title = LocalizationService.Instance.ProfilesDeleteTitle,
            Content = LocalizationService.Instance.DeleteProfileConfirm(item.Name),
            PrimaryButtonText = LocalizationService.Instance.CommonDelete,
            CloseButtonText = LocalizationService.Instance.CommonCancel,
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };

        if (await _dialogs.ShowAsync(dialog) == ContentDialogResult.Primary)
        {
            ViewModel.DeleteProfile(item);
        }
    }

    private async void OnEditPreMixinClick(object sender, RoutedEventArgs e) => await EditMixinAsync(isPre: true);

    private async void OnEditPostMixinClick(object sender, RoutedEventArgs e) => await EditMixinAsync(isPre: false);

    /// <summary>每次都从磁盘实时读取；WinUI 3 的 TextBox 只认 \r\n，统一规范化避免多行被吞。</summary>
    private static string ReadMixinContent(string path)
    {
        var content = MixinService.Read(path);
        return content.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");
    }

    /// <summary>内置 JSON 编辑器：保存前校验语法，出错就在弹窗里标红并拒绝关闭。</summary>
    private async Task EditMixinAsync(bool isPre)
    {
        if (_dialogs.IsOpen)
        {
            return;
        }

        var path = MixinService.GetPath(SingBoxService.DefaultCoreDirectory, isPre);
        MixinService.EnsureFile(path);

        // 属性必须先设齐（尤其是 AcceptsReturn），Text 不要在这里赋值：
        // TextBoxView 模板尚未加载时注入多行文本会被单行缓冲区截断，界面只剩首行 "{"
        var editor = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"),
            FontSize = 13,
            Height = 360,
            MinWidth = 500,
            IsSpellCheckEnabled = false,
        };

        ScrollViewer.SetVerticalScrollBarVisibility(editor, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(editor, ScrollBarVisibility.Auto);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            editor,
            isPre ? LocalizationService.Instance.ProfilesPreMixinJson : LocalizationService.Instance.ProfilesPostMixinJson);

        // 每次刷新都实时回读磁盘，避免外部编辑器改完文件后被编辑框里的旧缓冲覆盖
        void RefreshFromDisk() => editor.Text = ReadMixinContent(path);

        // 模板加载完成后再注入完整多行文本；每次弹窗显示也强制回读一次
        editor.Loaded += (_, _) => RefreshFromDisk();

        var error = new TextBlock
        {
            Foreground = ThemeBrushHelper.Get("SystemFillColorCriticalBrush", Microsoft.UI.Colors.Gray),
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };

        var openExternal = new Button
        {
            Content = LocalizationService.Instance.ProfilesOpenWithEditor,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        var reload = new Button
        {
            Content = LocalizationService.Instance.ProfilesMixinReload,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        openExternal.Click += async (_, _) =>
        {
            // 弹窗保持打开：外部编辑器退出后把磁盘最新内容灌回编辑框。
            // 若关联程序是「交给已运行实例就退出」的类型，用户可点「重新读取文件」手动刷新。
            try
            {
                var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                if (process is not null)
                {
                    try
                    {
                        await process.WaitForExitAsync();
                    }
                    catch (Exception)
                    {
                        // 进程不可等待时忽略，下面的回读仍然执行
                    }
                }
            }
            catch (Exception)
            {
                // 没有关联程序等，忽略
            }

            RefreshFromDisk();
        };

        reload.Click += (_, _) => RefreshFromDisk();

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(openExternal);
        actions.Children.Add(reload);

        // 不写死宽度：交给 ContentDialog 撑开，错误提示才能正常换行
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(editor);
        panel.Children.Add(error);
        panel.Children.Add(actions);

        var dialog = new ContentDialog
        {
            Title = isPre ? LocalizationService.Instance.ProfilesEditPreMixinTitle : LocalizationService.Instance.ProfilesEditPostMixinTitle,
            Content = panel,
            PrimaryButtonText = LocalizationService.Instance.CommonSave,
            CloseButtonText = LocalizationService.Instance.CommonCancel,
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
            MaxWidth = 760,
        };

        dialog.Opened += (_, _) => RefreshFromDisk();

        dialog.PrimaryButtonClick += (_, args) =>
        {
            try
            {
                MixinService.Validate(editor.Text);
                MixinService.Write(path, editor.Text);
            }
            catch (Exception ex)
            {
                // 语法错误时保持弹窗打开，把原因标红
                args.Cancel = true;
                error.Text = ex.Message;
                error.Visibility = Visibility.Visible;
            }
        };

        if (await _dialogs.ShowAsync(dialog) == ContentDialogResult.Primary)
        {
            await ViewModel.OnMixinSavedAsync(isPre);
        }
    }

    private void ScheduleComboRefresh() => DispatcherQueue.TryEnqueue(
        Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
        () => ViewModel.RefreshComboSelections());
}
