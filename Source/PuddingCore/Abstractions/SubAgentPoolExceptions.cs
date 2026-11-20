namespace PuddingCode.Abstractions;

/// <summary>
/// 池条目归属不匹配：请求方父会话/工作区不是该条目的创建者。
/// <para>
/// 持久化之后条目会比「创建它的那次执行」活得久，而「谁有权驱动它」在数据里必须先存在。
/// 跨父会话复用不是「借用一下上下文」那么轻：会话投影的 UPSERT 带
/// <c>WHERE parent_session_id = excluded.parent_session_id</c>，跨父时会抛
/// 「已被另一个父会话拥有」——而那时 run 已经建好，只能再终结成 failed，
/// 结果是「一条失败 run + 被污染的池条目计数」。
/// </para>
/// <para>
/// 因此归属校验前移到池层、且在<b>创建 run 之前</b>拒绝：本异常一旦抛出，
/// 数据库与池状态都不应有任何变化。
/// </para>
/// </summary>
public sealed class PoolOwnershipMismatchException : InvalidOperationException
{
    /// <summary>归属不匹配的具体维度。</summary>
    public const string OwnerSessionReason = "owner_session_mismatch";

    /// <summary>工作区不匹配。</summary>
    public const string OwnerWorkspaceReason = "owner_workspace_mismatch";

    /// <summary>条目没有归属（历史遗留或从未执行），不允许被任何会话接管。</summary>
    public const string OrphanReason = "orphan_entry";

    public PoolOwnershipMismatchException(
        string message,
        string poolEntryName,
        string? ownerSessionId,
        string? requestedSessionId,
        string reasonCode)
        : base(message)
    {
        PoolEntryName = poolEntryName;
        OwnerSessionId = ownerSessionId;
        RequestedSessionId = requestedSessionId;
        ReasonCode = reasonCode;
    }

    /// <summary>池条目名（仅用于展示，不是身份）。</summary>
    public string PoolEntryName { get; }

    /// <summary>条目的创建者父会话；为 null 表示孤儿条目。</summary>
    public string? OwnerSessionId { get; }

    /// <summary>本次请求的父会话。</summary>
    public string? RequestedSessionId { get; }

    /// <summary>机器可读的原因码，供工具层映射文案（S5）。</summary>
    public string ReasonCode { get; }
}
