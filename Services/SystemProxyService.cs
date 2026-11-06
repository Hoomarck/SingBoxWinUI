using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace SingBoxWinUI.Services;

/// <summary>
/// Windows 系统代理（WinINet）。
/// 只在内核运行期间开启，停止或退出时必须关掉，否则浏览器会指向一个已经没人监听的端口。
/// </summary>
public sealed class SystemProxyService
{
    private const string SettingsKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    private const string ProxyOverride =
        "localhost;127.*;10.*;172.16.*;172.17.*;172.18.*;172.19.*;172.20.*;172.21.*;172.22.*;"
        + "172.23.*;172.24.*;172.25.*;172.26.*;172.27.*;172.28.*;172.29.*;172.30.*;172.31.*;"
        + "192.168.*;<local>";

    private const int InternetOptionRefresh = 37;
    private const int InternetOptionSettingsChanged = 39;

    /// <summary>当前是否由本程序打开了系统代理。</summary>
    public bool IsEnabled { get; private set; }

    public string? CurrentServer { get; private set; }

    /// <summary>开启系统代理，server 形如 127.0.0.1:20122。</summary>
    public bool Enable(string server)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(SettingsKeyPath, writable: true);
            if (key is null)
            {
                return false;
            }

            key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
            key.SetValue("ProxyServer", server, RegistryValueKind.String);
            key.SetValue("ProxyOverride", ProxyOverride, RegistryValueKind.String);

            NotifySettingsChanged();

            IsEnabled = true;
            CurrentServer = server;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>关闭系统代理（把 ProxyEnable 置 0，保留地址方便下次复用）。</summary>
    public bool Disable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(SettingsKeyPath, writable: true);
            if (key is null)
            {
                return false;
            }

            key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
            NotifySettingsChanged();

            IsEnabled = false;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>读取当前系统代理状态：开启时返回 ProxyServer，否则 null。</summary>
    public string? GetEnabledServer()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(SettingsKeyPath);
            if (key is null)
            {
                return null;
            }

            var enable = Convert.ToInt32(key.GetValue("ProxyEnable") ?? 0, CultureInfo.InvariantCulture);
            return enable == 1 ? key.GetValue("ProxyServer") as string : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>当前系统代理是否正好指向我们上次写入的地址（=上次异常退出留下的残留）。</summary>
    public bool IsEnabledWith(string server)
    {
        var current = GetEnabledServer();
        return !string.IsNullOrEmpty(current)
               && string.Equals(current, server, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>让改动立即生效：先通知设置变化，再刷新 WinINet 缓存。</summary>
    private static void NotifySettingsChanged()
    {
        InternetSetOptionW(IntPtr.Zero, InternetOptionSettingsChanged, IntPtr.Zero, 0);
        InternetSetOptionW(IntPtr.Zero, InternetOptionRefresh, IntPtr.Zero, 0);
    }

    [DllImport("wininet.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool InternetSetOptionW(IntPtr internet, int option, IntPtr buffer, int bufferLength);
}
