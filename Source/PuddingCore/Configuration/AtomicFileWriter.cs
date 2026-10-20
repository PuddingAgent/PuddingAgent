using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace PuddingCode.Configuration;

/// <summary>
/// 跨平台原子文件写入器 — 写入临时文件 → fsync → 替换目标文件。
/// Windows 和 Linux 下均避免半写文件成为下一次启动的配置来源。
/// </summary>
public static class AtomicFileWriter
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// 原子写入：先将内容写入临时文件，再替换目标文件。
    /// 写入失败时不会破坏原文件。
    /// </summary>
    /// <param name="filePath">目标文件绝对路径。</param>
    /// <param name="content">要写入的文本内容。</param>
    /// <param name="ct">取消令牌。</param>
    /// <exception cref="IOException">写入或替换失败时抛出。</exception>
    public static async Task WriteAsync(string filePath, string content, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var tempPath = filePath + ".tmp." + Guid.NewGuid().ToString("N");

        try
        {
            // 配置统一使用无 BOM UTF-8；拒绝无效字符，避免静默替换后损坏原文。
            await File.WriteAllTextAsync(tempPath, content, Utf8, ct);

            // fsync 确保数据落盘
            await using (var fs = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                fs.Flush(flushToDisk: true);
            }

            // 替换/写入目标文件
            // File.Replace 要求目标已存在（适合更新已有配置）；
            // 若目标不存在则用 File.Move（适合首次创建）。
            if (File.Exists(filePath))
            {
                if (OperatingSystem.IsWindows())
                {
                    File.Replace(tempPath, filePath, null, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(tempPath, filePath, overwrite: true);
                }
            }
            else
            {
                File.Move(tempPath, filePath);
            }
        }
        catch
        {
            // 清理临时文件
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { /* 忽略清理失败 */ }
            }
            throw;
        }
    }

    /// <summary>
    /// 原子写入 JSON 内容到文件。
    /// </summary>
    public static async Task WriteJsonAsync<T>(string filePath, T value, System.Text.Json.JsonSerializerOptions? options = null, CancellationToken ct = default)
    {
        // 这里只生成磁盘配置，不用于内联 HTML。保留中文原文和调用方的命名/转换器约定，
        // 不修改可能已被冻结或同时用于 HTTP 响应的 options。
        var fileOptions = options is null ? new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        } : new JsonSerializerOptions(options);
        fileOptions.Encoder ??= JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
        var json = JsonSerializer.Serialize(value, fileOptions);
        await WriteAsync(filePath, json, ct);
    }

    /// <summary>
    /// 从文件读取并反序列化 JSON。
    /// </summary>
    public static async Task<T?> ReadJsonAsync<T>(string filePath, System.Text.Json.JsonSerializerOptions? options = null, CancellationToken ct = default)
    {
        if (!File.Exists(filePath))
            return default;

        await using var stream = File.OpenRead(filePath);
        return await System.Text.Json.JsonSerializer.DeserializeAsync<T>(stream, options ?? new System.Text.Json.JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
        }, ct);
    }
}
