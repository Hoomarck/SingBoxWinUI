using Windows.Storage;
using Windows.Storage.Pickers;

namespace SingBoxWinUI.Helpers;

/// <summary>
/// 文件选择器封装。
/// 两个要点必须保留：① Unpackaged 模式必须先 InitializeWithWindow 绑定 HWND；
/// ② 选择器在资源管理器重启 / 会话切换 / 被其它模态抢走时会抛 COMException，
/// 调用点都是 async void 事件处理器，异常一旦逃逸就是进程静默闪退，
/// 所以这里统一 try/catch，失败返回 null 并记一条日志。
/// </summary>
public static class FilePickerHelper
{
    /// <summary>打开单个文件；extensions 形如 ".json"。</summary>
    public static async Task<StorageFile?> PickOpenFileAsync(params string[] extensions)
    {
        try
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                ViewMode = PickerViewMode.List,
            };

            foreach (var extension in extensions)
            {
                picker.FileTypeFilter.Add(extension);
            }

            var hwnd = WindowHelper.MainWindowHandle;
            if (hwnd == IntPtr.Zero)
            {
                return null;
            }

            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            return await picker.PickSingleFileAsync();
        }
        catch (Exception ex)
        {
            LogFailure("打开文件选择器", ex);
            return null;
        }
    }

    /// <summary>另存为；types 为 (显示名, 扩展名) 列表，第一个作为默认类型。</summary>
    public static async Task<StorageFile?> PickSaveFileAsync(
        string suggestedFileName,
        params (string Label, string Extension)[] types)
    {
        try
        {
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = suggestedFileName,
            };

            foreach (var (label, extension) in types)
            {
                picker.FileTypeChoices.Add(label, new List<string> { extension });
            }

            var hwnd = WindowHelper.MainWindowHandle;
            if (hwnd == IntPtr.Zero)
            {
                return null;
            }

            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            return await picker.PickSaveFileAsync();
        }
        catch (Exception ex)
        {
            LogFailure("打开保存对话框", ex);
            return null;
        }
    }

    private static void LogFailure(string action, Exception ex)
    {
        try
        {
            var main = App.Main;
            main?.AppendLog($"[提示] {action}失败：{ex.Message}");
        }
        catch (Exception)
        {
            // 日志系统本身不可用时不再抛
        }
    }
}
