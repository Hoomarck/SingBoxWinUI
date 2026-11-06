using System.Text.Json.Serialization;
using System.Globalization;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using SingBoxWinUI.Services;
using SingBoxWinUI.Helpers;
using Windows.UI;

namespace SingBoxWinUI.Models;

/// <summary>日志级别。</summary>
public enum AppLogLevel
{
    Debug,
    Info,
    Warning,
    Error,
}

/// <summary>
/// 一条结构化日志。级别由正文关键词自动推断，画刷在构造时一次性算好，
/// 避免每帧渲染都去查主题资源。
/// </summary>
public sealed class LogEntry
{
    /// <summary>本地化入口：模板里的 {x:Bind Loc.Xxx} 走这里。</summary>
    [JsonIgnore]
    public LocalizationService Loc => LocalizationService.Instance;

    // 徽标底色：带透明度的色块，浅色/深色主题下都能用
    private static readonly SolidColorBrush ErrorTint = new(Color.FromArgb(0x26, 0xC4, 0x2B, 0x1C));
    private static readonly SolidColorBrush WarningTint = new(Color.FromArgb(0x26, 0x9A, 0x5B, 0x00));
    private static readonly SolidColorBrush DebugTint = new(Color.FromArgb(0x1A, 0x80, 0x80, 0x80));
    private static readonly SolidColorBrush InfoTint = new(Color.FromArgb(0x1F, 0x00, 0x78, 0xD4));

    public LogEntry(string message, AppLogLevel? level = null, DateTime? timestamp = null)
    {
        var time = timestamp ?? DateTime.Now;

        Timestamp = time.ToString("HH:mm:ss", CultureInfo.CurrentCulture);
        Message = message;
        Level = level ?? DetectLevel(message);
        RawLine = $"[{Timestamp}] {message}";

        LevelText = Level switch
        {
            AppLogLevel.Error => "ERROR",
            AppLogLevel.Warning => "WARN",
            AppLogLevel.Debug => "DEBUG",
            _ => "INFO",
        };

        LevelBrush = Level switch
        {
            AppLogLevel.Error => Resolve("SystemFillColorCriticalBrush", Colors.OrangeRed),
            AppLogLevel.Warning => Resolve("SystemFillColorCautionBrush", Colors.Orange),
            AppLogLevel.Debug => Resolve("TextFillColorTertiaryBrush", Colors.Gray),
            _ => Resolve("SystemFillColorAttentionBrush", Colors.DodgerBlue),
        };

        BadgeBackground = Level switch
        {
            AppLogLevel.Error => ErrorTint,
            AppLogLevel.Warning => WarningTint,
            AppLogLevel.Debug => DebugTint,
            _ => InfoTint,
        };

        // 只有 Error / Warning 的正文跟着染色，Info / Debug 用常规文本色
        MessageBrush = Level switch
        {
            AppLogLevel.Error => Resolve("SystemFillColorCriticalBrush", Colors.OrangeRed),
            AppLogLevel.Warning => Resolve("SystemFillColorCautionBrush", Colors.Orange),
            AppLogLevel.Debug => Resolve("TextFillColorTertiaryBrush", Colors.Gray),
            _ => Resolve("TextFillColorPrimaryBrush", Colors.Black),
        };
    }

    public string Timestamp { get; }

    public AppLogLevel Level { get; }

    public string LevelText { get; }

    public string Message { get; }

    /// <summary>复制 / 导出用的完整原始行。</summary>
    public string RawLine { get; }

    [JsonIgnore]
    public Brush LevelBrush { get; }

    [JsonIgnore]
    public Brush BadgeBackground { get; }

    [JsonIgnore]
    public Brush MessageBrush { get; }

    /// <summary>级别阈值比较用：数值越大越严重。</summary>
    [JsonIgnore]
    public int Severity => Level switch
    {
        AppLogLevel.Debug => 0,
        AppLogLevel.Warning => 2,
        AppLogLevel.Error => 3,
        _ => 1,
    };

    /// <summary>
    /// 关键词推断级别。顺序很重要：先判错误，再判警告，最后 Debug，
    /// 否则 "ERROR ... timeout" 会被判成 Warning。
    /// </summary>
    public static AppLogLevel DetectLevel(string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return AppLogLevel.Info;
        }

        if (Contains(message, "FATAL") || Contains(message, "ERROR") || Contains(message, "panic")
            || Contains(message, "[错误]") || HasFailure(message))
        {
            return AppLogLevel.Error;
        }

        if (Contains(message, "WARN") || Contains(message, "[提示]") || Contains(message, "timeout"))
        {
            return AppLogLevel.Warning;
        }

        if (Contains(message, "DEBUG"))
        {
            return AppLogLevel.Debug;
        }

        return AppLogLevel.Info;
    }

    private static bool Contains(string text, string keyword) =>
        text.Contains(keyword, StringComparison.OrdinalIgnoreCase);

    /// <summary>汇总里的「失败 0 个」不是错误，别把成功统计染成红色。</summary>
    private static readonly string[] ZeroFailureForms = { "失败 0", "失败0", "失败：0", "失败:0" };

    private static bool HasFailure(string message)
    {
        if (!Contains(message, "失败"))
        {
            return false;
        }

        foreach (var form in ZeroFailureForms)
        {
            if (message.Contains(form, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static Brush Resolve(string key, Color fallback) => ThemeBrushHelper.Get(key, fallback);
}
