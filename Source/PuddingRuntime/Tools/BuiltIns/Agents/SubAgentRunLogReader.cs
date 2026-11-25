using System.Globalization;
using System.Text;
using System.Text.Json;
using PuddingCode.SubAgents;

namespace PuddingRuntime.Services.Skills;

/// <summary>子代理运行日志的一小段快照（给主代理看的）。</summary>
public sealed record SubAgentRunLogSnapshot(
    string RunId,
    string Status,
    int EventCount,
    int ToolCallCount,
    IReadOnlyList<string> EventLines,
    IReadOnlyList<string> ToolLines,
    SubAgentLatencyBreakdown? Latency = null)
{
    /// <summary>渲染成紧凑文本。</summary>
    public string ToDisplayText()
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"run={RunId} status={Status} events={EventCount} tool_calls={ToolCallCount}");

        sb.AppendLine();
        sb.AppendLine("--- events (tail) ---");
        if (EventLines.Count == 0)
            sb.AppendLine("(none)");
        foreach (var line in EventLines)
            sb.AppendLine(line);

        sb.AppendLine("--- tools (tail) ---");
        if (ToolLines.Count == 0)
            sb.AppendLine("(none)");
        foreach (var line in ToolLines)
            sb.AppendLine(line);

        if (Latency is { } latency)
        {
            // 2026-11-14：把时间去哪了直接摆出来。
            // 「看」到事件不等于「看懂」——LLM 占多少、工具占多少、开销占多少才是判断依据。
            sb.AppendLine("--- latency ---");
            sb.Append(CultureInfo.InvariantCulture,
                $"total={latency.TotalDurationMs}ms llm={latency.LlmDurationMs}ms({latency.LlmPct}%) " +
                $"tool={latency.ToolDurationMs}ms({latency.ToolPct}%) " +
                $"overhead={latency.OverheadMs}ms({latency.OverheadPct}%) " +
                $"rounds={latency.RoundCount} tool_calls={latency.ToolCallCount}");
        }

        return sb.ToString().TrimEnd();
    }
}

/// <summary>
/// 2026-11-14：从 run 目录读「运行中/已结束」子代理的事件与工具审计尾部。
///
/// <para>
/// 用途：主代理要能回答「它现在在做什么」，而不只是「它跑了几轮」。
/// 按 subSessionId 定位 run 目录：run.json 里带 <c>subSessionId</c>，
/// 目录名 <c>run_yyyyMMdd_HHmmss_xxx</c> 天然按时间排序，因此从新到旧扫，
/// 命中即返回，并用 <paramref name="maxScanned"/> 兜住最坏情况（不回退全盘扫描）。
/// </para>
/// <para>
/// 2026-11-25：扫描范围收窄到调用方传入的单个 agent runs 根。旧实现枚举
/// <c>WorkspacesRoot</c> 下所有 workspace / agent 的 run 目录——「状态查不到即放行」的
/// 归属校验一旦命中那条分支，别人的运行内容就会被读出来。读范围必须与写范围一致。
/// </para>
/// </summary>
internal static class SubAgentRunLogReader
{
    private const string RunJsonFile = "run.json";
    private const string EventsFile = "events.jsonl";
    private const string ToolsFile = "tools.jsonl";

    /// <summary>
    /// 在指定 agent 的 runs 根下按 subSessionId 找最新的 run 目录；找不到返回 null。
    /// <para>
    /// 只扫传入的根，不越界到其他 workspace / agent。原因：归属校验在「状态查不到」时是放行的
    /// （不知道属于谁 ≠ 不是你的），若这里仍全盘扫描，那条放行就会把别人的运行内容读出来。
    /// 读取范围必须与写入范围一致。<param name="maxScanned"/> 依旧兜住最坏情况。
    /// </para>
    /// </summary>
    internal static string? TryFindLatestRunDirectory(
        string agentRunsRoot,
        string subSessionId,
        int maxScanned = 300)
    {
        if (string.IsNullOrWhiteSpace(subSessionId) || string.IsNullOrWhiteSpace(agentRunsRoot))
            return null;

        var scanned = 0;
        foreach (var runDir in EnumerateRunDirectoriesNewestFirst(agentRunsRoot))
        {
            if (++scanned > maxScanned)
                return null;

            var runJsonPath = Path.Combine(runDir, RunJsonFile);
            if (!File.Exists(runJsonPath))
                continue;

            var candidate = ReadSubSessionId(runJsonPath);
            if (string.Equals(candidate, subSessionId, StringComparison.Ordinal))
                return runDir;
        }

        return null;
    }

    /// <summary>读取 run 目录的事件/工具审计尾部；目录无效返回 null。</summary>
    internal static SubAgentRunLogSnapshot? Read(string? runDirectory, int tail)
    {
        if (string.IsNullOrWhiteSpace(runDirectory) || !Directory.Exists(runDirectory))
            return null;

        var safeTail = Math.Clamp(tail, 1, 200);
        var runJsonPath = Path.Combine(runDirectory, RunJsonFile);
        var (runId, status) = ReadRunIdentity(runJsonPath);

        var eventsPath = Path.Combine(runDirectory, EventsFile);
        var toolsPath = Path.Combine(runDirectory, ToolsFile);

        // 单次扫描：同一趟里算出延迟分解、取事件尾部、数总行数。
        var aggregator = new SubAgentLatencyAggregator();
        var events = ReadEventTail(eventsPath, safeTail, aggregator, out var eventCount);
        var tools = ReadTail(toolsPath, safeTail, FormatToolLine);

        return new SubAgentRunLogSnapshot(
            runId,
            status,
            eventCount,
            CountLines(toolsPath),
            events,
            tools,
            aggregator.Build(runId));
    }

    /// <summary>一步到位：在指定 agent 的 runs 根下定位 + 读取。</summary>
    internal static SubAgentRunLogSnapshot? ReadLatest(string agentRunsRoot, string subSessionId, int tail)
    {
        var dir = TryFindLatestRunDirectory(agentRunsRoot, subSessionId);
        return dir is null ? null : Read(dir, tail);
    }

    private static IEnumerable<string> EnumerateRunDirectoriesNewestFirst(string agentRunsRoot)
    {
        string[] runDirs;
        try
        {
            runDirs = Directory.Exists(agentRunsRoot)
                ? Directory.GetDirectories(agentRunsRoot, "run_*")
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            runDirs = [];
        }

        // 目录名 run_yyyyMMdd_HHmmss_* 字典序即时间序，倒序取最新。
        Array.Sort(runDirs, StringComparer.Ordinal);
        for (var i = runDirs.Length - 1; i >= 0; i--)
            yield return runDirs[i];
    }

    private static string? ReadSubSessionId(string runJsonPath)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(runJsonPath));
            return doc.RootElement.TryGetProperty("subSessionId", out var value) ? value.GetString() : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }

    private static (string RunId, string Status) ReadRunIdentity(string runJsonPath)
    {
        if (!File.Exists(runJsonPath))
            return ("(unknown)", "(unknown)");

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(runJsonPath));
            var root = doc.RootElement;
            var runId = root.TryGetProperty("runId", out var r) ? r.GetString() : null;
            var status = root.TryGetProperty("status", out var s) ? s.GetString() : null;
            return (runId ?? "(unknown)", status ?? "(unknown)");
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return ("(unreadable)", "(unreadable)");
        }
    }

    /// <summary>事件日志的单趟处理：取尾部 + 聚合延迟 + 数总行数。</summary>
    private static List<string> ReadEventTail(
        string path,
        int tail,
        SubAgentLatencyAggregator aggregator,
        out int total)
    {
        total = 0;
        var buffer = new Queue<string>(tail);
        if (!File.Exists(path))
            return [];

        try
        {
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                total++;
                aggregator.AddEventLine(line);

                var formatted = FormatEventLine(line);
                if (formatted is null)
                    continue;

                if (buffer.Count == tail)
                    buffer.Dequeue();
                buffer.Enqueue(formatted);
            }
        }
        catch (IOException)
        {
        }

        return [.. buffer];
    }

    /// <summary>流式取尾部，不把整份日志读进内存。</summary>
    private static List<string> ReadTail(string path, int tail, Func<string, string?> formatter)
    {
        var buffer = new Queue<string>(tail);
        if (!File.Exists(path))
            return [];

        try
        {
            foreach (var line in File.ReadLines(path))
            {
                var formatted = formatter(line);
                if (formatted is null)
                    continue;

                if (buffer.Count == tail)
                    buffer.Dequeue();
                buffer.Enqueue(formatted);
            }
        }
        catch (IOException)
        {
            // 日志正在写或已被清理：返回已读到的部分。
        }

        return [.. buffer];
    }

    private static int CountLines(string path)
    {
        if (!File.Exists(path))
            return 0;

        var count = 0;
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                if (!string.IsNullOrWhiteSpace(line))
                    count++;
            }
        }
        catch (IOException)
        {
        }

        return count;
    }

    private static string? FormatEventLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var type = root.TryGetProperty("eventType", out var t) ? t.GetString() : null;
            if (string.IsNullOrWhiteSpace(type))
                return null;

            var stamp = TryFormatTimestamp(root);
            return string.IsNullOrEmpty(stamp) ? type : $"{stamp} {type}";
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? FormatToolLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var name = root.TryGetProperty("toolName", out var n) ? n.GetString() : null;
            if (string.IsNullOrWhiteSpace(name))
                return null;

            var ok = !root.TryGetProperty("success", out var s) || s.ValueKind != JsonValueKind.False;
            var duration = root.TryGetProperty("durationMs", out var d) && d.TryGetInt64(out var dv) ? dv : 0L;
            var bytes = root.TryGetProperty("outputLength", out var o) && o.TryGetInt64(out var ov) ? ov : 0L;

            return string.Create(
                CultureInfo.InvariantCulture,
                $"{name} {(ok ? "ok" : "FAIL")} {duration}ms {bytes}B");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string TryFormatTimestamp(JsonElement root)
    {
        if (!root.TryGetProperty("timestamp", out var ts) || ts.GetString() is not { } raw)
            return string.Empty;

        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed.ToString("HH:mm:ss", CultureInfo.InvariantCulture)
            : string.Empty;
    }
}
