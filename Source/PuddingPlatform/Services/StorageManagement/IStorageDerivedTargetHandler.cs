namespace PuddingPlatform.Services.StorageManagement;

/// <summary>
/// 派生清理目标（code-index 作用域 / 冗余索引）的宿主侧处理器。
/// 物理实现依赖 PuddingCodeIntelligence/宿主组合根，因此以接口注入；
/// 目录中 RequiresDerivedHandler=true 的目标由协调器委托执行。
/// </summary>
public interface IStorageDerivedTargetHandler
{
    /// <summary>对应 StorageDataClassDefinition.HandlerId。</summary>
    string HandlerId { get; }

    /// <summary>有界候选探测（不执行删除）。</summary>
    Task<StorageDerivedEstimate> EstimateAsync(DateTimeOffset cutoffUtc, CancellationToken ct);

    /// <summary>执行一轮。派生目标为一次性小操作；Complete=false 表示仍有剩余。</summary>
    Task<StorageDerivedExecution> ExecuteRoundAsync(DateTimeOffset cutoffUtc, CancellationToken ct);
}

public sealed record StorageDerivedEstimate
{
    public long CandidateCount { get; init; }
    /// <summary>预览摘要行（如索引名/作用域名），有界，最多 20 条。</summary>
    public IReadOnlyList<string> PreviewItems { get; init; } = [];
    public string? Warning { get; init; }
}

public sealed record StorageDerivedExecution
{
    /// <summary>处理的行数（进度/预算口径）。</summary>
    public long ProcessedCount { get; init; }
    /// <summary>处理的单元数（作用域数/索引数，供旧 /databases 端点结果映射）。</summary>
    public long UnitCount { get; init; }
    public bool Complete { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
}
