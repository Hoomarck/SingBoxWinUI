using Microsoft.UI.Xaml;
using SingBoxWinUI.Services;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using SingBoxWinUI.Helpers;
using SingBoxWinUI.ViewModels;
using Windows.Storage;

namespace SingBoxWinUI.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsViewModel ViewModel { get; }

    public RuleSetsViewModel RuleSets => App.RuleSets;

    private readonly DialogHost _dialogs = new();

    /// <summary>本地化入口：界面上的 {x:Bind Loc.Xxx} 走这里。</summary>
    public LocalizationService Loc => LocalizationService.Instance;


    public SettingsPage()
    {
        // 与其它页一致：缓存实例，切走再回来不重建（也就不会重复探测内核版本）
        NavigationCacheMode = NavigationCacheMode.Required;

        ViewModel = new SettingsViewModel(App.AppSettings);

        InitializeComponent();

        // ItemsSource 赋值会把 SelectedIndex 重置成 -1。
        // 页面 Loaded 时 ComboBox 自己的 Items 可能还没建好，直接推会被再次压成 -1，
        // 所以挪到布局跑完之后的低优先级队列里再补一次。
        Loaded += (_, _) => ScheduleComboRefresh();

        // 切语言时选项列表会整表重建，ComboBox 的选中项会在布局之后才被清掉，
        // 所以除了 VM 里的即时校正，这里再排一次低优先级校正兜住。
        LocalizationService.Instance.LanguageChanged += (_, _) => ScheduleComboRefresh();
    }

    // ---- sing-box 内核更新 ----

    private async void OnUpdateKernelClick(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = Loc.KernelUpdateConfirmTitle,
            Content = Loc.KernelUpdateConfirm,
            PrimaryButtonText = Loc.CommonOk,
            CloseButtonText = Loc.CommonCancel,
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };

        if (await _dialogs.ShowAsync(dialog) == ContentDialogResult.Primary)
        {
            await ViewModel.UpdateKernelCommand.ExecuteAsync(null);
        }
    }

    // ---- 备份与恢复 ----

    private async void OnExportBackupClick(object sender, RoutedEventArgs e)
    {
        var file = await FilePickerHelper.PickSaveFileAsync(
            $"singbox-backup-{DateTime.Now:yyyyMMdd-HHmmss}.zip",
            (LocalizationService.Instance.SetBackupFileType, ".zip"));

        if (file is null)
        {
            return;
        }

        try
        {
            var data = App.Backup.CreateBackup();
            await FileIO.WriteBytesAsync(file, data);
            ViewModel.StatusMessage = Loc.Say(
                $"已导出备份 {file.Name}（{data.Length / 1024} KB）",
                $"Backup exported to {file.Name} ({data.Length / 1024} KB)");
        }
        catch (Exception ex)
        {
            ViewModel.StatusMessage = Loc.Say($"导出失败：{ex.Message}", $"Export failed: {ex.Message}");
        }
    }

    private async void OnRestoreLocalClick(object sender, RoutedEventArgs e)
    {
        var file = await FilePickerHelper.PickOpenFileAsync(".zip");
        if (file is null)
        {
            return;
        }

        if (!await ConfirmRestoreAsync(LocalizationService.Instance.SetRestoreLocal))
        {
            return;
        }

        try
        {
            await using var stream = await file.OpenStreamForReadAsync();
            var count = App.Backup.Restore(stream);
            await App.ReloadAfterRestoreAsync();
            ViewModel.ReloadFromSettings();
            ViewModel.StatusMessage = Loc.Say(
                $"已从 {file.Name} 恢复 {count} 个文件",
                $"Restored {count} files from {file.Name}");
        }
        catch (Exception ex)
        {
            ViewModel.StatusMessage = Loc.Say($"恢复失败：{ex.Message}", $"Restore failed: {ex.Message}");
        }
    }

    private async void OnTestWebDavClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var data = App.Backup.CreateBackup();
            var target = await App.Backup.WebDavBackupAsync(App.AppSettings.Current, data);
            ViewModel.StatusMessage = Loc.Say(
                $"WebDAV 可用，测试备份已上传：{target}",
                $"WebDAV reachable, test backup uploaded: {target}");
        }
        catch (Exception ex)
        {
            ViewModel.StatusMessage = Loc.Say($"WebDAV 连接失败：{ex.Message}", $"WebDAV connection failed: {ex.Message}");
        }
    }

    private async void OnWebDavBackupClick(object sender, RoutedEventArgs e)
    {
        try
        {
            ViewModel.StatusMessage = LocalizationService.Instance.Say("正在上传 WebDAV 备份…", "Uploading WebDAV backup...");
            var data = App.Backup.CreateBackup();
            var target = await App.Backup.WebDavBackupAsync(App.AppSettings.Current, data);
            ViewModel.StatusMessage = Loc.Say(
                $"已上传 {data.Length / 1024} KB 到 {target}",
                $"Uploaded {data.Length / 1024} KB to {target}");
        }
        catch (Exception ex)
        {
            ViewModel.StatusMessage = Loc.Say($"WebDAV 备份失败：{ex.Message}", $"WebDAV backup failed: {ex.Message}");
        }
    }

    private async void OnRestoreWebDavClick(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmRestoreAsync(LocalizationService.Instance.SetRestoreWebDav))
        {
            return;
        }

        try
        {
            ViewModel.StatusMessage = LocalizationService.Instance.Say("正在下载云端备份…", "Downloading cloud backup...");
            var bytes = await App.Backup.WebDavDownloadAsync(App.AppSettings.Current);

            using var stream = new MemoryStream(bytes);
            var count = App.Backup.Restore(stream);

            await App.ReloadAfterRestoreAsync();
            ViewModel.ReloadFromSettings();
            ViewModel.StatusMessage = Loc.Say(
                $"已从 WebDAV 恢复 {count} 个文件",
                $"Restored {count} files from WebDAV");
        }
        catch (Exception ex)
        {
            ViewModel.StatusMessage = Loc.Say($"WebDAV 恢复失败：{ex.Message}", $"WebDAV restore failed: {ex.Message}");
        }
    }

    private void OnWebDavPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox box)
        {
            ViewModel.WebDavPassword = box.Password;
        }
    }

    private async Task<bool> ConfirmRestoreAsync(string title)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = LocalizationService.Instance.SetRestoreConfirm,
            PrimaryButtonText = LocalizationService.Instance.SetRestoreConfirmButton,
            CloseButtonText = LocalizationService.Instance.CommonCancel,
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };

        return await _dialogs.ShowAsync(dialog) == ContentDialogResult.Primary;
    }

    private void ScheduleComboRefresh() => DispatcherQueue.TryEnqueue(
        Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
        () => ViewModel.RefreshComboSelections());
}
