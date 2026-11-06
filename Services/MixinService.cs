using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SingBoxWinUI.Helpers;

namespace SingBoxWinUI.Services;

/// <summary>
/// 全局拓展配置（Mixin）文件的读写与校验。
/// 文件固定放在 Core\mixin_pre.json / Core\mixin_post.json，允许写 // 注释。
/// </summary>
public static class MixinService
{
    public const string PreFileName = "mixin_pre.json";
    public const string PostFileName = "mixin_post.json";

    public static string GetPath(string coreDirectory, bool isPre) =>
        Path.Combine(coreDirectory, isPre ? PreFileName : PostFileName);

    /// <summary>确保两个文件都存在，缺失时写入带注释的示例骨架。</summary>
    public static void EnsureFiles(string coreDirectory)
    {
        EnsureFile(GetPath(coreDirectory, isPre: true));
        EnsureFile(GetPath(coreDirectory, isPre: false));
    }

    public static void EnsureFile(string path)
    {
        if (File.Exists(path))
        {
            return;
        }

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var isPre = string.Equals(Path.GetFileName(path), PreFileName, StringComparison.OrdinalIgnoreCase);
        File.WriteAllText(path, CreateTemplate(isPre), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    public static string Read(string path) => File.Exists(path)
        ? File.ReadAllText(path)
        : CreateTemplate(string.Equals(Path.GetFileName(path), PreFileName, StringComparison.OrdinalIgnoreCase));

    public static void Write(string path, string content) =>
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

    /// <summary>校验并解析为 JSON 对象，语法错误抛 InvalidDataException。</summary>
    public static JsonObject Parse(string text, string source)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new JsonObject();
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(text, nodeOptions: null, documentOptions: JsonDefaults.DocumentOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"JSON 语法错误：{ex.Message}");
        }

        return node switch
        {
            JsonObject obj => obj,
            null => new JsonObject(),
            _ => throw new InvalidDataException("根节点必须是 JSON 对象（{ ... }）"),
        };
    }

    /// <summary>只校验不解析，给编辑弹窗用。</summary>
    public static void Validate(string text) => Parse(text, "mixin");

    private static string CreateTemplate(bool isPre) => isPre
        ? """
{
  // 全局前置拓展（高优先级）
  // · 标量字段会覆盖订阅里的同名字段
  // · 带 tag 的数组元素（outbounds / inbounds / dns.servers / route.rule_set）
  //   同 tag 会被替换，新 tag 插入到最前面
  // · route.rules / dns.rules 会插入到规则链最前面
  //
  // 占位符（合并后自动展开，换订阅无需改文件）：
  //   __ALL__ 全部真实节点 ｜ __AUTO__ 剔除高倍率节点 ｜ __REGION:hk/jp/sg/us/kr/tw__ 按地区
  //   __FAKEIP__ 复用现有 fakeip 服务器 tag
  //   例：{ "type": "urltest", "tag": "自动选择", "outbounds": ["__AUTO__"] }
  //
  // 示例：
  //   "log": { "level": "debug" },
  //   "outbounds": [{ "type": "direct", "tag": "mixin-direct" }],
  //   "route": { "rules": [{ "domain_suffix": ["example.com"], "outbound": "mixin-direct" }] }
}
"""
        : """
{
  // 全局后置拓展（低优先级）
  // · 只在订阅里没有该字段时写入标量
  // · 带 tag 的数组元素同 tag 替换，新 tag 追加到末尾
  // · route.rules / dns.rules 追加到规则链最后面
  //
  // 示例：
  //   "outbounds": [{ "type": "socks", "tag": "my-proxy", "server": "127.0.0.1", "server_port": 1080 }],
  //   "route": { "rules": [{ "domain_suffix": ["intranet.local"], "outbound": "my-proxy" }] }
}
""";
}
