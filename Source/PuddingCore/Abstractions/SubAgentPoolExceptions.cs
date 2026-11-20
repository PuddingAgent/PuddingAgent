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

/// <summary>
/// 池的就绪状态（持久化池 S4 引入）。
/// <para>
/// 池从「纯内存字典」变成「从库重建的视图」之后，多了一个此前不存在的状态：<b>视图还没加载完</b>。
/// 这个状态必须显式建模，因为「未加载完」与「池里确实没有成员」在类型上看起来是同一个空集合，
/// 而调用方（尤其拿池集合当删除白名单的 GC）对这两者的处置完全相反。
/// </para>
/// </summary>
public enum PoolReadiness
{
    /// <summary>尚未尝试加载（池的初始状态）。不可当作「空池」使用。</summary>
    NotStarted,

    /// <summary>正在加载（另一个调用方已进入加载临界区）。不可当作「空池」使用。</summary>
    Loading,

    /// <summary>已就绪：内存视图与库一致，可以正常读写。</summary>
    Ready,

    /// <summary>恢复失败（加载抛异常）。<b>终态</b>，不自动重试、绝不降级为空池。</summary>
    Failed,
}

/// <summary>
/// 池未就绪：底层视图尚未可用，调用方不得把它当成空池继续。
/// <para>
/// <b>为什么必须抛而不是返回空集合：</b>池集合有一处「删除白名单」用法
/// （<c>SubAgentTransientDirectoryGcService</c>）：返回空集合等于告诉 GC「没有任何池化目录需要保护」，
/// 那些目录会被当成孤儿隔离。把「不知道」伪装成「没有」，代价是删数据；
/// 显式抛错则只会让本轮扫描跳过——错误可见且可重试。
/// </para>
/// </summary>
public sealed class PoolNotReadyException : InvalidOperationException
{
    public PoolNotReadyException(string message, PoolReadiness readiness) : base(message)
    {
        Readiness = readiness;
    }

    /// <summary>
    /// 带原始异常的构造：加载失败的原因（SQLite 打不开、schema 缺失等）必须留在异常链里，
    /// 否则上层只能看到「池不可用」而丢失「为什么」，排障要重新跑一遍才能知道。
    /// </summary>
    public PoolNotReadyException(string message, PoolReadiness readiness, Exception? innerException)
        : base(message, innerException)
    {
        Readiness = readiness;
    }

    /// <summary>抛出时的就绪状态：NotStarted / Loading / Failed。</summary>
    public PoolReadiness Readiness { get; }
}
