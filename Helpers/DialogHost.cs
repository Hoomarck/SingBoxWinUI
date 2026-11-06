using Microsoft.UI.Xaml.Controls;

namespace SingBoxWinUI.Helpers;

/// <summary>
/// ContentDialog 互斥宿主。
/// WinUI 同时只允许一个 ContentDialog，重复 ShowAsync 会直接抛异常；
/// 之前 SettingsPage / ProfilesPage / ConnectionsPage 各复制了一份 _dialogOpen + try/finally，
/// 这里收敛成一处，语义完全一致（XamlRoot 仍由调用方在创建 dialog 时绑定）。
/// </summary>
public sealed class DialogHost
{
    private bool _open;

    public bool IsOpen => _open;

    public async Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
    {
        if (_open)
        {
            return ContentDialogResult.None;
        }

        _open = true;
        try
        {
            return await dialog.ShowAsync();
        }
        catch (Exception)
        {
            // 已有对话框打开 / 窗口正在关闭等，静默忽略，不要让 async void 把进程带崩
            return ContentDialogResult.None;
        }
        finally
        {
            _open = false;
        }
    }
}
