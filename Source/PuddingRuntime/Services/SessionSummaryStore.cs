using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using PuddingCode.Configuration;

namespace PuddingRuntime.Services;

/// <summary>
/// Session 压缩摘要持久化存储。
///
/// 路径规则：agents/{agentId}/memory/session-summaries/{date}/{sequence}.summary.md
/// 写入时机：Session 压缩完成后（一次性）
/// 读取时机：后续 Session 首次构建 L2-MEMORY-SUMMARY 层
/// </summary>
public sealed class SessionSummaryStore
{
    private readonly PuddingDataPaths _paths;
    private readonly ILogger<SessionSummaryStore>? _logger;

    public SessionSummaryStore(PuddingDataPaths paths, ILogger<SessionSummaryStore>? logger = null)
    {
        _paths = paths;
        _logger = logger;
    }

    /// <summary>
    /// 保存统一历史上下文文件（摘要 + 原文片段）。
    /// </summary>
    public async Task<string?> SaveAsync(
        string agentInstanceId,
        string sourceSessionId,
        string summary,
        IReadOnlyList<string>? recentMessages = null,
        DateTimeOffset? compressedAt = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(agentInstanceId) || string.IsNullOrWhiteSpace(summary))
            return null;

        var timestamp = compressedAt ?? DateTimeOffset.Now;
        var date = timestamp.ToString("yyyy-MM-dd");
        var dayRoot = _paths.AgentInstanceSessionSummaryDayRoot(agentInstanceId, date);

        try
        {
            Directory.CreateDirectory(dayRoot);

            var nextSeq = GetNextSequence(dayRoot);
            var filePath = _paths.AgentInstanceSessionSummaryFile(agentInstanceId, date, nextSeq);

            var content = BuildSummaryFile(sourceSessionId, summary, recentMessages, timestamp, agentInstanceId, nextSeq);
            await File.WriteAllTextAsync(filePath, content, Encoding.UTF8, ct);

            _logger?.LogInformation(
                "[SessionSummaryStore] saved agent={AgentId} sourceSession={SourceSession} file={File} summaryLen={SummaryLen} recentMsg={RecentCount}",
                agentInstanceId, sourceSessionId, filePath, summary.Length, recentMessages?.Count ?? 0);
            return filePath;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SessionSummaryStore] Save failed: {ex}");
            return null;
        }
    }

    /// <summary>
    /// 读取最近一条 Session 摘要（按日期倒序，取最新的）。
    /// </summary>
    public async Task<string?> LoadLatestAsync(
        string agentInstanceId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(agentInstanceId))
            return null;

        var root = _paths.AgentInstanceSessionSummaryRoot(agentInstanceId);
        if (!Directory.Exists(root))
            return null;

        try
        {
            var today = DateTime.Now.Date;

            // 从今天开始向后搜索最多 7 天
            for (int i = 0; i < 7; i++)
            {
                ct.ThrowIfCancellationRequested();

                var day = today.AddDays(-i).ToString("yyyy-MM-dd");
                var dayRoot = _paths.AgentInstanceSessionSummaryDayRoot(agentInstanceId, day);
                if (!Directory.Exists(dayRoot))
                    continue;

                var files = Directory.GetFiles(dayRoot, "*.summary.md")
                    .OrderByDescending(f => f)
                    .ToList();

                if (files.Count > 0)
                {
                    var content = await File.ReadAllTextAsync(files[0], Encoding.UTF8, ct);
                    _logger?.LogInformation(
                        "[SessionSummaryStore] recallHit agent={AgentId} file={File} day={Day} totalFiles={TotalFiles} contentLen={ContentLen}",
                        agentInstanceId, files[0], day, files.Count, content.Length);
                    return content;
                }
            }

            _logger?.LogInformation(
                "[SessionSummaryStore] recallMiss agent={AgentId} searchedDays=7",
                agentInstanceId);
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 清理超过指定天数的过期摘要文件。
    /// </summary>
    public void Cleanup(int retentionDays = 7)
    {
        var dataRoot = _paths.AgentInstancesRoot;
        if (!Directory.Exists(dataRoot))
            return;

        var cutoff = DateTime.Now.Date.AddDays(-retentionDays);

        foreach (var agentDir in Directory.GetDirectories(dataRoot))
        {
            var summaryRoot = Path.Combine(agentDir, "memory", "session-summaries");
            if (!Directory.Exists(summaryRoot))
                continue;

            try
            {
                foreach (var dayDir in Directory.GetDirectories(summaryRoot))
                {
                    var dayName = Path.GetFileName(dayDir);
                    if (!DateTime.TryParseExact(dayName, "yyyy-MM-dd",
                            CultureInfo.InvariantCulture, DateTimeStyles.None, out var dayDate))
                        continue;

                    if (dayDate < cutoff)
                    {
                        Directory.Delete(dayDir, recursive: true);
                    }
                }
            }
            catch
            {
                // 跳过无法清理的目录
            }
        }
    }

    private static string GetNextSequence(string dayRoot)
    {
        if (!Directory.Exists(dayRoot))
            return "0001";

        var existing = Directory.GetFiles(dayRoot, "*.summary.md");
        var maxSeq = 0;
        foreach (var file in existing)
        {
            var name = Path.GetFileNameWithoutExtension(file);
            // filename format: "0001.summary" → sequence = "0001"
            var seqName = name.EndsWith(".summary")
                ? name[..^8] // remove ".summary"
                : name;

            if (int.TryParse(seqName, NumberStyles.None, CultureInfo.InvariantCulture, out var seq))
                maxSeq = Math.Max(maxSeq, seq);
        }

        return (maxSeq + 1).ToString("D4");
    }

    private static string BuildSummaryFile(
        string sourceSessionId,
        string summary,
        IReadOnlyList<string>? recentMessages,
        DateTimeOffset compressedAt,
        string agentInstanceId,
        string sequence)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Session-SUMMARY (Unified)");
        sb.AppendLine($"- agent_id: {agentInstanceId}");
        sb.AppendLine($"- source_session_id: {sourceSessionId}");
        sb.AppendLine($"- compressed_at: {compressedAt:yyyy-MM-ddTHH:mm:ssZ}");
        sb.AppendLine($"- sequence: {sequence}");
        sb.AppendLine();
        sb.AppendLine("## Summary");
        sb.AppendLine(summary);

        if (recentMessages is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("## Recent Messages");
            foreach (var msg in recentMessages)
                sb.AppendLine(msg);
        }

        return sb.ToString();
    }
}
