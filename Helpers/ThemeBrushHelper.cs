using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace SingBoxWinUI.Helpers;

/// <summary>
/// 主题画刷解析（带缓存）。
/// 原来是 LogEntry.Resolve 与 ProxyItems.ThemeBrushes.Get 两份同构实现，且每条日志都要
/// 穿透 MergedDictionaries + ThemeDictionaries 查 3 次；缓存后高频日志路径只剩字典命中。
/// 主题切换时必须调用 <see cref="InvalidateCache"/>，否则会一直拿到旧主题的画刷。
/// </summary>
public static class ThemeBrushHelper
{
    private static readonly Dictionary<string, Brush> Cache = new(StringComparer.Ordinal);
    private static readonly object Gate = new();

    /// <summary>取主题资源画刷；取不到或未初始化时用 fallback 现场造一个。</summary>
    public static Brush Get(string key, Color fallback)
    {
        lock (Gate)
        {
            if (Cache.TryGetValue(key, out var cached))
            {
                return cached;
            }
        }

        Brush brush;
        try
        {
            brush = Application.Current?.Resources.TryGetValue(key, out var value) == true && value is Brush found
                ? found
                : new SolidColorBrush(fallback);
        }
        catch (Exception)
        {
            brush = new SolidColorBrush(fallback);
        }

        lock (Gate)
        {
            Cache[key] = brush;
        }

        return brush;
    }

    /// <summary>主题变化后清空缓存（由窗口的 ActualThemeChanged 调用）。</summary>
    public static void InvalidateCache()
    {
        lock (Gate)
        {
            Cache.Clear();
        }
    }
}
