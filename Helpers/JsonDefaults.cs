using System.Text.Encodings.Web;
using System.Text.Json;

namespace SingBoxWinUI.Helpers;

/// <summary>
/// 全项目共用的 JSON 选项。
/// 之前 JsonSerializerOptions 有 3 处各自 new、JsonDocumentOptions（跳过注释 + 允许尾逗号）
/// 在 4 个文件里各写一遍，改一处忘三处。
/// </summary>
public static class JsonDefaults
{
    /// <summary>缩进输出，用于用户可读的配置文件。</summary>
    public static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
    };

    /// <summary>缩进 + 不转义中文（订阅元数据里含中文名称）。</summary>
    public static readonly JsonSerializerOptions IndentedRelaxed = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>sing-box 配置允许 // 注释与尾逗号，解析时统一放宽。</summary>
    public static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
}
