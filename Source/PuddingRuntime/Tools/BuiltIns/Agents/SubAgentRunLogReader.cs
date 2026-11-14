using System.Globalization;
using System.Text;
using System.Text.Json;
using PuddingCode.Configuration;

namespace PuddingRuntime.Services.Skills;

/// <summary>子代理运行日志的一小段快照（给主代理看的）。</summary>
public sealed record SubAgentRunLogSnapshot(
    string RunId,
    string Status,
    int EventCount,
    int ToolCallCount,
    IReadOnlyList<string> EventLines,
    IReadOnlyList<string> ToolLines)
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
/// </summary>
internal static class SubAgentRunLogReader
{
    private const string RunJsonFile = "run.json";
    private const string EventsFile = "events.jsonl";
    private const string ToolsFile = "tools.jsonl";

    /// <summary>按 subSessionId 找最新的 run 目录；找不到返回 null。</summary>
    internal static string? TryFindLatestRunDirectory(
        PuddingDataPaths paths,
        string subSessionId,
        int maxScanned = 300)
    {
        if (string.IsNullOrWhiteSpace(subSessionId) || !Directory.Exists(paths.WorkspacesRoot))
            return null;

        var scanned = 0;
        foreach (var runDir in EnumerateRunDirectoriesNewestFirst(paths))
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

        var events = ReadTail(eventsPath, safeTail, FormatEventLine);
        var tools = ReadTail(toolsPath, safeTail, FormatToolLine);

        return new SubAgentRunLogSnapshot(
            runId,
            status,
            CountLines(eventsPath),
            CountLines(toolsPath),
            events,
            tools);
    }

    /// <summary>一步到位：定位 + 读取。</summary>
    internal static SubAgentRunLogSnapshot? ReadLatest(PuddingDataPaths paths, string subSessionId, int tail)
    {
        var dir = TryFindLatestRunDirectory(paths, subSessionId);
        return dir is null ? null : Read(dir, tail);
    }

    private static IEnumerable<string> EnumerateRunDirectoriesNewestFirst(PuddingDataPaths paths)
    {
        IEnumerable<string> workspaces;
        try
        {
            workspaces = Directory.GetDirectories(paths.WorkspacesRoot);
        }
        catch (IOException)
        {
            yield break;
        }

        var all = new List<string>();
        foreach (var workspace in workspaces)
        {
            var agentsRoot = Path.Combine(workspace, "agents");
            if (!Directory.Exists(agentsRoot))
                continue;

            foreach (var agentDir in Directory.GetDirectories(agentsRoot))
            {
                var runsRoot = Path.Combine(agentDir, "runs");
                if (!Directory.Exists(runsRoot))
                    continue;

                foreach (var runDir in Directory.GetDirectories(runsRoot, "run_*"))
                    all.Add(runDir);
            }
        }

        // 目录名 run_yyyyMMdd_HHmmss_* 字典序即时间序，倒序取最新。
        all.Sort(StringComparer.Ordinal);
        for (var i = all.Count - 1; i >= 0; i--)
            yield return all[i];
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
