namespace PuddingCode.Configuration;

/// <summary>
/// 跨平台原子文件写入器 — 写入临时文件 → fsync → 替换目标文件。
/// Windows 和 Linux 下均避免半写文件成为下一次启动的配置来源。
/// </summary>
public static class AtomicFileWriter
{
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
            // 写入临时文件
#if NET9_0_OR_GREATER
            await File.WriteAllTextAsync(tempPath, content, ct);
#else
            await File.WriteAllTextAsync(tempPath, content, ct);
#endif

            // fsync 确保数据落盘
            await using (var fs = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                fs.Flush(flushToDisk: true);
            }

            // 替换目标文件（Windows 下使用 File.Replace 保持元数据）
            if (OperatingSystem.IsWindows())
            {
                File.Replace(tempPath, filePath, null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, filePath, overwrite: true);
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
        var json = System.Text.Json.JsonSerializer.Serialize(value, options ?? new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        });
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
