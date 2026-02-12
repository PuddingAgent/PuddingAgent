using System.Text.Json;
using PuddingCode.Configuration;
using PuddingCode.Observability;

namespace PuddingRuntime.Services.Tools;

/// <summary>高频工具操作审计日志。写入 data/logs/audit/YYYY-MM-DD.jsonl，对 Agent 透明。</summary>
public sealed class AuditLogger
{
    private const long MaxFileSizeBytes = 1_048_576;
    private const int RetainedFileCountLimit = 200;

    private readonly string _auditDir;
    private readonly object _writeLock = new();

    public AuditLogger(PuddingDataPaths dataPaths)
    {
        _auditDir = Path.Combine(dataPaths.LogsRoot, "audit");
        Directory.CreateDirectory(_auditDir);
    }

    public void Write(OperationZone zone, string toolId, string agentInstanceId,
        string path, string? reason, bool success, long durationMs,
        RuntimeTraceContext? trace = null)
    {
        try
        {
            var entry = new AuditEntry
            {
                Timestamp = DateTimeOffset.Now.ToString("o"),
                TraceId = trace?.TraceId,
                CorrelationId = trace?.CorrelationId,
                SessionId = trace?.SessionId,
                Tool = toolId,
                AgentId = agentInstanceId,
                Path = path,
                Zone = zone.ToString(),
                Reason = reason ?? "(none)",
                Result = success ? "success" : "failure",
                DurationMs = durationMs,
            };

            var line = JsonSerializer.Serialize(entry);

            lock (_writeLock)
            {
                var today = DateTimeOffset.Now;
                var datePrefix = today.ToString("yyyy-MM-dd");
                var baseFileName = $"{datePrefix}.jsonl";
                var baseFilePath = Path.Combine(_auditDir, baseFileName);
                var filePath = baseFilePath;

                if (File.Exists(baseFilePath) && new FileInfo(baseFilePath).Length >= MaxFileSizeBytes)
                {
                    var pattern = $"{datePrefix}_*.jsonl";
                    var existing = Directory.GetFiles(_auditDir, pattern);
                    var maxSeq = 0;
                    foreach (var f in existing)
                    {
                        var name = Path.GetFileNameWithoutExtension(f);
                        var underscoreIdx = name.LastIndexOf('_');
                        if (underscoreIdx > 0 && int.TryParse(name[(underscoreIdx + 1)..], out var s) && s > maxSeq)
                            maxSeq = s;
                    }
                    filePath = Path.Combine(_auditDir, $"{datePrefix}_{maxSeq + 1:D3}.jsonl");
                }

                File.AppendAllText(filePath, line + Environment.NewLine, System.Text.Encoding.UTF8);

                var allFiles = Directory.GetFiles(_auditDir, "*.jsonl");
                if (allFiles.Length > RetainedFileCountLimit)
                {
                    var toDelete = allFiles
                        .OrderBy(f => f, StringComparer.Ordinal)
                        .Take(allFiles.Length - RetainedFileCountLimit);
                    foreach (var f in toDelete)
                    {
                        try { File.Delete(f); } catch { }
                    }
                }
            }
        }
        catch
        {
            // 审计日志写入失败不应影响工具执行
        }
    }

    private sealed record AuditEntry
    {
        public required string Timestamp { get; init; }
        public string? TraceId { get; init; }
        public string? CorrelationId { get; init; }
        public string? SessionId { get; init; }
        public required string Tool { get; init; }
        public required string AgentId { get; init; }
        public required string Path { get; init; }
        public required string Zone { get; init; }
        public required string Reason { get; init; }
        public required string Result { get; init; }
        public long DurationMs { get; init; }
    }
}
