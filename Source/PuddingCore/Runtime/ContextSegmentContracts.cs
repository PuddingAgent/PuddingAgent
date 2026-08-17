using System.Text.Json.Serialization;

namespace PuddingCode.Runtime;

/// <summary>
/// ContextSegmentLedger 的分级（设计方案 §8.1 分级策略，§6.1 数据合同）。
/// 与 §8.1 表格一致：T0 当前执行 → T4 归档。
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ContextSegmentTier>))]
public enum ContextSegmentTier
{
    /// <summary>当前执行：当前用户输入、未完成 assistant/tool group，原文不压缩。</summary>
    T0,

    /// <summary>近期：最近 2 个完整轮次，原文；大工具结果使用 envelope + artifact。</summary>
    T1,

    /// <summary>温数据：第 3–10 轮，可逆投影优先。</summary>
    T2,

    /// <summary>冷数据：第 11–50 轮或超出软预算部分，chunk summary + coverage ranges + artifact refs。</summary>
    T3,

    /// <summary>归档：更久远、已稳定结论，多级 reduce summary + durable facts + source refs。</summary>
    T4,
}

/// <summary>
/// ContextSegmentLedger 契约（设计方案 §6.1）。
/// 每一段可进入模型的内容先登记身份，再决定投影。
/// 纯数据契约，不绑定存储实现；持久化由 PuddingMemoryEngine.ContextSegmentEntity 承担。
/// </summary>
/// <param name="SegmentId">段唯一标识。</param>
/// <param name="SessionId">所属会话 ID。</param>
/// <param name="RunId">执行运行 ID（可空）。</param>
/// <param name="TurnId">轮次 ID（可空）。</param>
/// <param name="SourceKind">来源种类。§6.1 未固定枚举值，取值由来源合同约定（如 message/tool_result/recall/summary），不得臆造。</param>
/// <param name="SourceId">来源 ID（如 MessageId / artifact id）。</param>
/// <param name="SequenceStart">来源起始序号（含）。</param>
/// <param name="SequenceEnd">来源结束序号（含）。</param>
/// <param name="Role">角色（user/assistant/tool/system）。</param>
/// <param name="ContentType">内容类型（text/tool_result/...）。</param>
/// <param name="CanonicalContentHash">对原始规范化 UTF-8 字节计算的 SHA-256 十六进制摘要。</param>
/// <param name="RawUtf8Bytes">原始 UTF-8 字节数。</param>
/// <param name="EstimatedTokens">估算 Token 数（可空，未归因前可缺省）。</param>
/// <param name="ProviderTokens">服务商口径 Token 数（可空，未归因前可缺省）。</param>
/// <param name="ArtifactRef">原文 artifact 引用（可空，无 artifact 时缺省）。</param>
/// <param name="ContextGeneration">上下文代际（可空，未代际化时缺省）。</param>
/// <param name="CoveredByManifestId">覆盖清单 ID（可空，未被覆盖时缺省）。</param>
/// <param name="Tier">分级（T0–T4，见 <see cref="ContextSegmentTier"/>）。</param>
/// <param name="IsAtomicToolGroup">是否属于不可拆分的原子工具组。</param>
/// <param name="AuthorizationScope">授权范围（可空，沿原来源授权边界）。</param>
public sealed record ContextSegment(
    string SegmentId,
    string SessionId,
    string? RunId,
    string? TurnId,
    string SourceKind,
    string SourceId,
    long SequenceStart,
    long SequenceEnd,
    string Role,
    string ContentType,
    string CanonicalContentHash,
    long RawUtf8Bytes,
    int? EstimatedTokens,
    int? ProviderTokens,
    string? ArtifactRef,
    long? ContextGeneration,
    string? CoveredByManifestId,
    ContextSegmentTier Tier,
    bool IsAtomicToolGroup,
    string? AuthorizationScope);
