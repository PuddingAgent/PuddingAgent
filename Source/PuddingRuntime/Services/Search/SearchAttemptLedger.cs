using System.Diagnostics;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntime.Services.Search;

/// <summary>
/// 一次搜索尝试的终态分类。用于失败账本判定"确定性重试"是否可被短路。
/// </summary>
public enum SearchAttemptOutcome
{
    /// <summary>已命中结果（后续仍应重新执行以获取最新结果）。</summary>
    Hit,
    /// <summary>确定无匹配（在该 scope/glob 下扫描完成且未命中）。</summary>
    NoMatch,
    /// <summary>扫描被预算/上限截断，结果不完整。</summary>
    Truncated,
    /// <summary>扫描超时。</summary>
    Timeout,
    /// <summary>执行错误。</summary>
    Error,
}

/// <summary>
/// 搜索失败账本的确定性去重键。所有字段在入账前已规范化：
/// query 已 trim，scope 已归一为绝对小写路径，glob/case 为原始归一化值，
/// workspaceVersion 为 git HEAD（失败退化为空串）。
/// </summary>
public readonly record struct SearchAttemptKey(
    string Tool,
    string Query,
    string Scope,
    string Glob,
    bool CaseSensitive,
    string WorkspaceVersion);

/// <summary>一次已完成的搜索尝试记录。</summary>
public readonly record struct SearchAttemptRecord(
    SearchAttemptOutcome Outcome,
    string Summary,
    int ResultCount,
    DateTimeOffset RecordedAtUtc);

/// <summary>搜索失败账本：记录已扫描范围，并对确定性重复做短路。</summary>
public interface ISearchAttemptLedger
{
    /// <summary>
    /// 当 key 对应的上一次尝试是确定性终态（no_match / timeout）时返回 true，并输出 prior。
    /// 命中/截断/错误不会被短路，因为那会阻断必要的继续检索或重试。
    /// </summary>
    bool TryGetSuppression(SearchAttemptKey key, out SearchAttemptRecord prior);

    /// <summary>记录一次已完成的搜索尝试。</summary>
    void Record(SearchAttemptKey key, SearchAttemptRecord record);
}

/// <summary>
/// 有界内存实现。账本跨调用共享（工具为单例），按 key 去重；超过上限时整体清空，
/// 避免无界增长。清空后仅丧失"短路"能力，不会阻断任何新调用。
/// </summary>
public sealed class SearchAttemptLedger : ISearchAttemptLedger
{
    private readonly object _gate = new();
    private readonly Dictionary<SearchAttemptKey, SearchAttemptRecord> _entries = new();
    private readonly int _maxEntries;

    public SearchAttemptLedger(int maxEntries = 256)
    {
        _maxEntries = maxEntries <= 0 ? 256 : maxEntries;
    }

    public bool TryGetSuppression(SearchAttemptKey key, out SearchAttemptRecord prior)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out prior))
            {
                return prior.Outcome is SearchAttemptOutcome.NoMatch or SearchAttemptOutcome.Timeout;
            }

            prior = default;
            return false;
        }
    }

    public void Record(SearchAttemptKey key, SearchAttemptRecord record)
    {
        lock (_gate)
        {
            _entries[key] = record;
            if (_entries.Count > _maxEntries)
            {
                _entries.Clear();
            }
        }
    }
}

/// <summary>
/// 确定性 workspace 版本源：git HEAD hash；任何失败/超时退化为固定空串，绝不抛异常。
/// 结果按规范化 workspace 根缓存，避免每次搜索都 spawn git。
/// </summary>
public static class SearchWorkspaceVersion
{
    private static readonly object s_gate = new();
    private static readonly Dictionary<string, string> s_cache = new(StringComparer.OrdinalIgnoreCase);

    public static string Resolve(string? workspaceRoot)
    {
        var root = Normalize(workspaceRoot);
        lock (s_gate)
        {
            if (s_cache.TryGetValue(root, out var cached))
                return cached;
        }

        var value = TryResolveGitHead(root);
        lock (s_gate)
        {
            s_cache[root] = value;
        }

        return value;
    }

    private static string Normalize(string? workspaceRoot)
        => string.IsNullOrWhiteSpace(workspaceRoot)
            ? string.Empty
            : Path.GetFullPath(workspaceRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .ToLowerInvariant();

    private static string TryResolveGitHead(string root)
    {
        if (string.IsNullOrEmpty(root))
            return string.Empty;

        try
        {
            var psi = new ProcessStartInfo("git", "rev-parse HEAD")
            {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process is null)
                return string.Empty;

            var stdout = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(1500))
            {
                TryKill(process);
                return string.Empty;
            }

            if (process.ExitCode != 0)
                return string.Empty;

            var head = stdout.Trim();
            return head.Length == 40 && head.All(Uri.IsHexDigit) ? head : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // best effort
        }
    }
}

/// <summary>搜索失败账本相关路径/键的规范化辅助。</summary>
public static class SearchAttemptKeyNormalizer
{
    /// <summary>规范化搜索 scope：绝对路径 + 去尾分隔符 + 小写，用于确定性比较。</summary>
    public static string NormalizeScope(string? directory)
        => string.IsNullOrWhiteSpace(directory)
            ? string.Empty
            : Path.GetFullPath(directory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .ToLowerInvariant();
}
