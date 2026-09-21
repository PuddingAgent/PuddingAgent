namespace PuddingCode.Operators;

/// <summary>
/// 所有算子的公共输入面：场景上下文实现本接口。
/// <para>只承载「场景无关的最小面」——场景细节留在实现侧，避免契约层被场景字段污染。</para>
/// </summary>
public interface IOperatorContext
{
    /// <summary>场景键（算子与阈值策略按场景解析）。</summary>
    string SceneKey { get; }

    /// <summary>输入身份指纹（用于缓存、审计去重与复现）。</summary>
    string InputDigest { get; }
}

/// <summary>
/// 可选扩展面：场景上下文携带溯源身份四元组时实现本接口，基类据此填充信封的 <c>Identity</c>。
/// </summary>
public interface IOperatorIdentityContext
{
    /// <summary>溯源身份四元组。</summary>
    OperatorIdentity Identity { get; }
}

/// <summary>
/// 溯源身份四元组（自既有工具调用裁决上下文<b>提升</b>为公开契约；不改变其既有定义）。
/// </summary>
public sealed record OperatorIdentity
{
    /// <summary>工作区 id（判定不跨工作区）。</summary>
    public required string WorkspaceId { get; init; }

    /// <summary>会话 id。</summary>
    public required string SessionId { get; init; }

    /// <summary>Agent 实例 id。</summary>
    public required string AgentInstanceId { get; init; }

    /// <summary>用户 id。</summary>
    public required string UserId { get; init; }
}

/// <summary>
/// 一条证据（必须可回溯）：<see cref="Reference"/> 要能定位到原始事实，
/// 例如 canonical event id / 规则 id / <c>path:line</c>。
/// </summary>
public sealed record JudgementEvidence
{
    /// <summary>证据种类（例 <c>canonical_event</c> / <c>rule</c> / <c>source_file</c>）。</summary>
    public required string Kind { get; init; }

    /// <summary>证据引用（例 event_id / ruleId / path:line）。</summary>
    public required string Reference { get; init; }

    /// <summary>补充说明；无则 null。</summary>
    public string? Note { get; init; }
}
