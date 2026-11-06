using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;

namespace SingBoxWinUI.Services;

/// <summary>
/// 备份与恢复：把 Core\ 下所有配置打成一个 zip，支持本地文件与 WebDAV。
/// 刻意不打包 sing-box.exe / cache.db / running_config.json。
/// </summary>
public sealed class BackupRestoreService
{
    private const string LatestFileName = "singbox-backup-latest.zip";

    private static readonly string[] RootFiles =
    {
        "app_settings.json",
        "profiles.json",
        "mixin_pre.json",
        "mixin_post.json",
        "config.json",
    };

    private readonly string _coreDirectory;

    public BackupRestoreService(string coreDirectory)
    {
        _coreDirectory = coreDirectory;
    }

    /// <summary>生成备份包字节流。</summary>
    public byte[] CreateBackup()
    {
        using var buffer = new MemoryStream();

        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var relative in EnumerateBackupFiles())
            {
                var full = Path.Combine(_coreDirectory, relative);
                var entry = archive.CreateEntry(relative.Replace('\\', '/'), CompressionLevel.Optimal);

                using var entryStream = entry.Open();
                using var source = File.OpenRead(full);
                source.CopyTo(entryStream);
            }
        }

        return buffer.ToArray();
    }

    /// <summary>解压覆盖到 Core\，返回恢复的文件数。</summary>
    public int Restore(Stream zipStream)
    {
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);
        var root = Path.GetFullPath(_coreDirectory);
        var count = 0;

        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
            {
                continue; // 目录项
            }

            var destination = Path.GetFullPath(Path.Combine(root, entry.FullName));

            // 防 zip-slip：解压目标必须仍在 Core\ 内
            if (!destination.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(destination, root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"备份包包含非法路径：{entry.FullName}");
            }

            var directory = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            entry.ExtractToFile(destination, overwrite: true);
            count++;
        }

        return count;
    }

    /// <summary>校验字节流是不是一个合法 zip。</summary>
    public static bool IsValidZip(Stream stream)
    {
        try
        {
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            return archive.Entries.Count > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ---- WebDAV ----

    public async Task<string> WebDavBackupAsync(AppSettings settings, byte[] payload, CancellationToken ct = default)
    {
        var baseUrl = NormalizeUrl(settings);

        using var http = CreateClient(settings);

        // 目录不存在时先建；已存在/不允许都会返回非 2xx，这里忽略
        try
        {
            using var mkcol = new HttpRequestMessage(new HttpMethod("MKCOL"), baseUrl + "/");
            using var mkcolResponse = await http.SendAsync(mkcol, ct);
        }
        catch (Exception)
        {
            // 目录已存在等情况，继续尝试 PUT
        }

        using var content = new ByteArrayContent(payload);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");

        using var response = await http.PutAsync($"{baseUrl}/{LatestFileName}", content, ct);
        response.EnsureSuccessStatusCode();

        return $"{baseUrl}/{LatestFileName}";
    }

    public async Task<byte[]> WebDavDownloadAsync(AppSettings settings, CancellationToken ct = default)
    {
        var baseUrl = NormalizeUrl(settings);

        using var http = CreateClient(settings);
        using var response = await http.GetAsync($"{baseUrl}/{LatestFileName}", ct);
        response.EnsureSuccessStatusCode();

        var bytes = await response.Content.ReadAsByteArrayAsync(ct);

        using var check = new MemoryStream(bytes);
        if (!IsValidZip(check))
        {
            throw new InvalidDataException("云端文件不是合法的备份包（zip）");
        }

        return bytes;
    }

    private static string NormalizeUrl(AppSettings settings)
    {
        var url = settings.WebDavUrl?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new InvalidOperationException("尚未配置 WebDAV 地址");
        }

        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            url = "https://" + url;
        }

        return url.TrimEnd('/');
    }

    private static HttpClient CreateClient(AppSettings settings)
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };

        if (!string.IsNullOrEmpty(settings.WebDavUsername))
        {
            var raw = $"{settings.WebDavUsername}:{settings.WebDavPassword}";
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes(raw)));
        }

        return client;
    }

    private IEnumerable<string> EnumerateBackupFiles()
    {
        foreach (var file in RootFiles)
        {
            if (File.Exists(Path.Combine(_coreDirectory, file)))
            {
                yield return file;
            }
        }

        var profilesDirectory = Path.Combine(_coreDirectory, "profiles");
        if (!Directory.Exists(profilesDirectory))
        {
            yield break;
        }

        foreach (var path in Directory.EnumerateFiles(profilesDirectory, "*.json"))
        {
            yield return Path.Combine("profiles", Path.GetFileName(path));
        }
    }
}
