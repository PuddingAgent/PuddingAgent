using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using PuddingCode.Configuration;

namespace PuddingPlatform.Services;

/// <summary>
/// Retention 裁剪归档写入器：在 DELETE 过期事件流之前，把该批完整行追加归档到
/// 按天分片的 WORM jsonl 文件，恢复 append-only 不变量——超期证据不因保留期裁剪而销毁。
///
/// 并发安全：复用 AgentRawLogMirrorService 的模式——按文件路径持有 SemaphoreSlim 锁，
/// 同一进程内对同一归档文件串行 AppendAllTextAsync；跨进程由文件系统 append 语义兜底。
/// </summary>
public class RetentionArchiveWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new();

    private readonly PuddingDataPaths _paths;
    private readonly ILogger<RetentionArchiveWriter> _logger;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _fileLocks = new(StringComparer.OrdinalIgnoreCase);

    public RetentionArchiveWriter(PuddingDataPaths paths, ILogger<RetentionArchiveWriter> logger)
    {
        _paths = paths;
        _logger = logger;
    }

    /// <summary>
    /// 把一批待删行追加归档到当天 jsonl 文件。
    /// 每行 = 实体全部字段 + 归档元数据（archived_at / retention_cutoff / table_name）。
    /// 写文件失败会抛出异常，调用方据此中止对应批次的 DELETE（先归档后删，绝不先删后归档）。
    /// </summary>
    public virtual async Task ArchiveBatchAsync<T>(
        string tableName,
        IReadOnlyList<T> rows,
        DateTimeOffset cutoff,
        CancellationToken ct = default)
    {
        if (rows.Count == 0)
            return;

        var day = DateTimeOffset.UtcNow.UtcDateTime.ToString("yyyy-MM-dd");
        var path = _paths.PlatformRetentionArchiveFile(tableName, day);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var archivedAt = DateTimeOffset.UtcNow.ToString("O");
        var cutoffString = cutoff.ToString("O");

        var sb = new StringBuilder(rows.Count * 256);
        foreach (var row in rows)
        {
            var node = JsonSerializer.SerializeToNode(row, JsonOptions);
            var obj = node as JsonObject ?? new JsonObject { ["row"] = node };
            obj["archived_at"] = archivedAt;
            obj["retention_cutoff"] = cutoffString;
            obj["table_name"] = tableName;
            sb.Append(obj.ToJsonString(JsonOptions)).Append('\n');
        }

        var gate = _fileLocks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            await File.AppendAllTextAsync(path, sb.ToString(), ct);
        }
        finally
        {
            gate.Release();
        }

        _logger.LogDebug(
            "[RetentionArchive] archived table={Table} rows={Rows} cutoff={Cutoff:O} file={File}",
            tableName, rows.Count, cutoff, path);
    }
}
