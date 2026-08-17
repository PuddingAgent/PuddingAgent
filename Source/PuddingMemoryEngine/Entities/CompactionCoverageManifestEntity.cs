using System.ComponentModel.DataAnnotations;

namespace PuddingMemoryEngine.Entities;

/// <summary>
/// 压缩覆盖清单持久化实体（方案 §6.3）。
/// 与 <see cref="MessageEntity.CompactedBy"/> 落在同一事务中写入，是「写前覆盖门禁」
/// 的持久化事实：只有当 <see cref="OmittedCount"/> == 0 时才允许写入覆盖标记。
/// 历史表只做 additive 追加，不删除旧行、不更新已提交 manifest 的源代际字段。
/// </summary>
public class CompactionCoverageManifestEntity
{
    [Key]
    [MaxLength(32)]
    public string CompactionId { get; set; } = Guid.NewGuid().ToString("N");

    [MaxLength(32)]
    public string SessionId { get; set; } = string.Empty;

    /// <summary>本次压缩开始前的会话代际（读取自 <see cref="SessionEntity.CompactionGeneration"/>）。</summary>
    public int SourceGeneration { get; set; }

    /// <summary>本次压缩提交后的会话代际（= SourceGeneration + 1）。</summary>
    public int TargetGeneration { get; set; }

    /// <summary>被本次压缩覆盖的 message id 列表（JSON 数组字符串）。</summary>
    public string? SourceMessageIds { get; set; }

    /// <summary>被覆盖消息的内容 SHA-256（小写 hex，JSON 数组字符串），与 SourceMessageIds 一一对应。</summary>
    public string? SourceHashes { get; set; }

    /// <summary>确实进入摘要输入、并被写入 CompactedBy 的消息数。</summary>
    public int CoveredCount { get; set; }

    /// <summary>应覆盖但未覆盖的消息数。写覆盖标记前强制为 0。</summary>
    public int OmittedCount { get; set; }

    /// <summary>摘要输入中重复出现的消息数（正常情况下为 0）。</summary>
    public int DuplicateCount { get; set; }

    /// <summary>压缩前活跃上下文 UTF-8 字节数（不含正文）。</summary>
    public long RawUtf8BytesBefore { get; set; }

    /// <summary>压缩后活跃上下文 UTF-8 字节数（保留原文 + 摘要）。</summary>
    public long RawUtf8BytesAfter { get; set; }

    /// <summary>压缩前活跃上下文估算 Token 数。</summary>
    public long TokensBefore { get; set; }

    /// <summary>压缩后活跃上下文估算 Token 数。</summary>
    public long TokensAfter { get; set; }

    [MaxLength(32)]
    public string? FinalSummaryId { get; set; }

    [MaxLength(64)]
    public string? FinalSummaryHash { get; set; }

    [MaxLength(128)]
    public string Generator { get; set; } = string.Empty;

    /// <summary>本次压缩是否以降级方式完成（例如摘要生成失败但未破坏覆盖）。</summary>
    public bool Degraded { get; set; }

    /// <summary>失败原因。仅当本次压缩失败（未写 CompactedBy）时有值。</summary>
    public string? FailureReason { get; set; }

    /// <summary>Unix 时间戳（毫秒）。</summary>
    public long CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
