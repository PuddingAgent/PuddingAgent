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
    /// 加载指定 session 全部已提交覆盖清单的并集（滚动摘要链下各代 manifest 链式衔接；
    /// 只看最新代会漏掉旧代覆盖的消息，使其经 JSONL 旁路复活）。
    /// </summary>
    public async Task<CompactionCoverage> LoadAsync(string sessionId, CancellationToken ct = default)
    {
        if (_memoryDbFactory is null)
            return CompactionCoverage.Empty;

        await using var db = await _memoryDbFactory.CreateDbContextAsync(ct);
        var manifests = await db.CompactionCoverageManifests
            .AsNoTracking()
            .Where(m => m.SessionId == sessionId)
            .ToListAsync(ct);
        if (manifests.Count == 0)
            return CompactionCoverage.Empty;

        var coveredMessageIds = new HashSet<string>();
        var coveredHashes = new HashSet<string>();
        foreach (var manifest in manifests)
        {
            coveredMessageIds.UnionWith(ParseIdArray(manifest.SourceMessageIds));
            coveredHashes.UnionWith(ParseIdArray(manifest.SourceHashes));
        }

        var latestGeneration = manifests.Max(m => m.TargetGeneration);
        return new CompactionCoverage(coveredMessageIds, coveredHashes, latestGeneration);
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
