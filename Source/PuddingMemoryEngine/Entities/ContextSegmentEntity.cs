using System.ComponentModel.DataAnnotations;

namespace PuddingMemoryEngine.Entities;

/// <summary>
/// ContextSegmentLedger 持久化实体（设计方案 §6.1 数据合同）。
/// 登记每一段可进入模型内容的身份与投影元数据，供分级压缩与同源去重使用。
/// 纯 additive 底座：不接入任何现有压缩流程。
/// </summary>
public class ContextSegmentEntity
{
    [Key]
    [MaxLength(64)]
    public string SegmentId { get; set; } = Guid.NewGuid().ToString("N");

    [MaxLength(32)]
    public string SessionId { get; set; } = string.Empty;

    [MaxLength(64)]
    public string? RunId { get; set; }

    [MaxLength(64)]
    public string? TurnId { get; set; }

    /// <summary>
    /// 来源种类。§6.1 未固定枚举值，取值由来源合同约定
    /// （如 message / tool_result / recall / summary），不臆造枚举。
    /// </summary>
    [MaxLength(32)]
    public string SourceKind { get; set; } = string.Empty;

    /// <summary>来源 ID（如 MessageId / artifact id）。</summary>
    [MaxLength(128)]
    public string SourceId { get; set; } = string.Empty;

    /// <summary>来源起始序号（含）。</summary>
    public long SequenceStart { get; set; }

    /// <summary>来源结束序号（含）。</summary>
    public long SequenceEnd { get; set; }

    /// <summary>角色：user / assistant / tool / system。</summary>
    [MaxLength(32)]
    public string Role { get; set; } = "user";

    /// <summary>内容类型：text / tool_result / ...</summary>
    [MaxLength(32)]
    public string ContentType { get; set; } = "text";

    /// <summary>对原始规范化 UTF-8 字节计算的 SHA-256 十六进制摘要（§6.1）。</summary>
    [MaxLength(64)]
    public string CanonicalContentHash { get; set; } = string.Empty;

    /// <summary>原始 UTF-8 字节数。</summary>
    public long RawUtf8Bytes { get; set; }

    /// <summary>估算 Token 数（服务商归因前可缺省）。</summary>
    public int? EstimatedTokens { get; set; }

    /// <summary>服务商口径 Token 数（未归因前可缺省）。</summary>
    public int? ProviderTokens { get; set; }

    /// <summary>原文 artifact 引用路径（无 artifact 时缺省）。</summary>
    [MaxLength(512)]
    public string? ArtifactRef { get; set; }

    /// <summary>上下文代际（未代际化时缺省）。</summary>
    public long? ContextGeneration { get; set; }

    /// <summary>覆盖清单 ID（未被覆盖时缺省）。</summary>
    [MaxLength(64)]
    public string? CoveredByManifestId { get; set; }

    /// <summary>分级：T0 / T1 / T2 / T3 / T4（设计方案 §8.1）。</summary>
    [MaxLength(8)]
    public string Tier { get; set; } = "T0";

    /// <summary>是否属于不可拆分的原子工具组。</summary>
    public bool IsAtomicToolGroup { get; set; }

    /// <summary>授权范围（沿原来源授权边界，可空）。</summary>
    [MaxLength(256)]
    public string? AuthorizationScope { get; set; }

    /// <summary>Unix 毫秒时间戳。</summary>
    public long CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>JSON 扩展字段。</summary>
    public string? Metadata { get; set; }
}
