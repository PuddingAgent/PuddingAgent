namespace PuddingCode.Platform;

/// <summary>
/// 会话事件投影游标存储——追踪 ChatMessages 物化视图的进度。
/// <para>
/// ADR-056 分层架构：
///   SessionEventLog ──── 唯一权威事实源（事件、审计、恢复、诊断）
///        │
///        │ 投影（project）
///        ▼
///   ChatMessages ─────── 物化投影（浏览器 UI、历史分页、全文检索）
/// <para>
/// projectedThroughSequence 记录 "已投影到 ChatMessages 的最新 Sequence 号"。
/// 浏览器重连时：
/// 1. 从 ChatMessages 加载已完成的用户/Agent 消息（历史分页）
/// 2. 获取 projectedThroughSequence
/// 3. 从 SessionEventLog 读取该 sequence 之后的尾部队列事件
/// 4. 尚未完成的 Turn 使用尾部 delta 重建
/// </para>
/// </summary>
public interface ISessionProjectionStore
{
    /// <summary>
    /// 获取会话已投影到 ChatMessages 的最新 Sequence（0 表示未投影任何事件）。
    /// </summary>
    Task<long> GetProjectedCursorAsync(string sessionId, CancellationToken ct = default);

    /// <summary>
    /// 更新会话的投影游标（= 已经投影完成的最新 Sequence）。
    /// 幂等：只接受更大的 sequence。
    /// </summary>
    Task SetProjectedCursorAsync(string sessionId, long sequence, CancellationToken ct = default);
}
