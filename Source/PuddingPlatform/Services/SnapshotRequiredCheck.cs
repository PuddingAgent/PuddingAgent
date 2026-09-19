// ADR-057 快照必需判定。
// 控制器（SessionEventsController.EventsStream）与流服务（SessionEventStreamService）共用
// 这一处判据，避免两边各写一份而漂移。

namespace PuddingPlatform.Services;

/// <summary>
/// 判定客户端游标之后是否**确实缺失**事件，从而必须退回快照。
///
/// 这不等于「游标低于当前最小可用序号」：每个会话的序号从 1 开始
///（<c>conversation_heads.head_sequence</c> 从 0 起递增后分配），所以
/// <c>cursor=0</c> 与 <c>minAvailableSequence=1</c> 之间并无缺失——
/// 显式 0（有意全量回放，仅用于刚创建的新会话）不得被判为需要快照。
/// 反之，若日志被裁剪过（min &gt; 1），任何低于 <c>min - 1</c> 的游标都确有缺口。
///
/// 用 <c>cursor + 1 &lt; min</c> 还顺带修掉了旧判据 <c>cursor &lt; min</c> 的假阳性：
/// 游标恰为 <c>min - 1</c>（客户端已持有 min 之前的全部事件）时并无缺失，无需退回快照。
/// </summary>
public static class SnapshotRequiredCheck
{
    /// <param name="cursor">客户端声明的权威位置（已应用到的最大序号）。</param>
    /// <param name="minAvailableSequence">当前仍可读取的最小序号；无事件时为 null。</param>
    public static bool HasMissingEvents(long cursor, long? minAvailableSequence) =>
        minAvailableSequence.HasValue && cursor + 1 < minAvailableSequence.Value;
}
