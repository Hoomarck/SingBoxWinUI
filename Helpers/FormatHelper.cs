namespace SingBoxWinUI.Helpers;

/// <summary>
/// 字节 / 速率的唯一格式化实现。
/// 之前 MainViewModel（long 版）与 ConnectionItem（double 版）各写一套，
/// 舍入规则不一致，同一个数字在两个界面可能显示不同结果。
/// </summary>
public static class FormatHelper
{
    public static string FormatBytes(double bytes)
    {
        if (bytes < 0)
        {
            bytes = 0;
        }

        if (bytes < 1024)
        {
            return $"{bytes:0} B";
        }

        if (bytes < 1024 * 1024)
        {
            return $"{bytes / 1024:0.0} KB";
        }

        if (bytes < 1024d * 1024 * 1024)
        {
            return $"{bytes / 1024 / 1024:0.0} MB";
        }

        return $"{bytes / 1024 / 1024 / 1024:0.00} GB";
    }

    public static string FormatSpeed(double bytesPerSecond) => $"{FormatBytes(bytesPerSecond)}/s";
}
