namespace SingBoxWinUI.Helpers;

/// <summary>
/// 主窗口 HWND 的统一入口。
/// Unpackaged 模式下 FileOpenPicker / FileSavePicker 必须显式绑定 HWND，
/// 之前每个页面都自己 GetWindowHandle 一次，缓存后只需解析一次。
/// </summary>
public static class WindowHelper
{
    private static IntPtr _handle;

    public static IntPtr MainWindowHandle
    {
        get
        {
            if (_handle != IntPtr.Zero)
            {
                return _handle;
            }

            try
            {
                _handle = WinRT.Interop.WindowNative.GetWindowHandle(App.Window);
            }
            catch (Exception)
            {
                // 窗口尚未创建（或已销毁）：返回 0，调用方据此跳过 picker
                _handle = IntPtr.Zero;
            }

            return _handle;
        }
    }
}
