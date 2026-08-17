using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PuddingMemoryEngine.Data;

namespace PuddingRuntime.Services;

/// <summary>
/// 压缩覆盖过滤器：读取 session 的最新压缩覆盖清单，为 JSONL 冷启动路径提供
/// 「已压缩消息」去重集合，防止已压缩消息经 JSONL 旁路复活（方案 §9 去重规则）。
/// 数据库不可用 / 无 manifest 时必须 no-op（返回 <see cref="CompactionCoverage.Empty"/>），不抛异常。
/// </summary>
public sealed class CompactionCoverageFilter
{
    private readonly IDbContextFactory<MemoryDbContext>? _memoryDbFactory;

    public CompactionCoverageFilter(IDbContextFactory<MemoryDbContext>? memoryDbFactory)
    {
        _memoryDbFactory = memoryDbFactory;
    }

    /// <summary>
    /// 加载指定 session 最新（TargetGeneration 最大）覆盖清单的覆盖集合。
    /// </summary>
    public async Task<CompactionCoverage> LoadAsync(string sessionId, CancellationToken ct = default)
    {
        if (_memoryDbFactory is null)
            return CompactionCoverage.Empty;

        await using var db = await _memoryDbFactory.CreateDbContextAsync(ct);
        var manifest = await db.CompactionCoverageManifests
            .AsNoTracking()
            .Where(m => m.SessionId == sessionId)
            .OrderByDescending(m => m.TargetGeneration)
            .FirstOrDefaultAsync(ct);
        if (manifest is null)
            return CompactionCoverage.Empty;

        var coveredMessageIds = ParseIdArray(manifest.SourceMessageIds);
        var coveredHashes = ParseIdArray(manifest.SourceHashes);
        return new CompactionCoverage(coveredMessageIds, coveredHashes, manifest.TargetGeneration);
    }

    /// <summary>解析 JSON 数组字符串；非法/空输入一律返回空集合。</summary>
    private static HashSet<string> ParseIdArray(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            var ids = JsonSerializer.Deserialize<string[]>(json);
            return ids is null or { Length: 0 } ? [] : new HashSet<string>(ids);
        }
        catch (JsonException)
        {
            return [];
        }
    }
}

/// <summary>
/// 一次压缩覆盖的只读集合：被覆盖的 message id 与内容哈希（均小写 hex）。
/// </summary>
public sealed record CompactionCoverage(
    HashSet<string> CoveredMessageIds,
    HashSet<string> CoveredHashes,
    int? LatestTargetGeneration)
{
    /// <summary>空覆盖：无任何被覆盖消息、无最新代际。过滤循环中为空集合时无影响。</summary>
    public static CompactionCoverage Empty { get; } = new([], [], null);
}
