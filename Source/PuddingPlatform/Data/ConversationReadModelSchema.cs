namespace PuddingPlatform.Data;

/// <summary>
/// 会话读模型持久化合同的**单一定义点**（Plan《会话首屏与渐进加载架构优化方案》§3.2/§6）。
/// <para>
/// 表名、索引名与 <c>conversation_events</c> 查询索引都由这里命名；EF 模型（新库 EnsureCreated）
/// 与 <c>ConversationReadModelSchemaBootstrapper</c>（存量库就地补齐）必须引用同一常量，
/// 禁止新库/旧库各自维护一套名字。
/// </para>
/// </summary>
public static class ConversationReadModelSchema
{
    public const string TurnSummariesTable = "conversation_turn_summaries";
    public const string ReadStateTable = "conversation_read_state";

    /// <summary>会话内按 last_sequence 取最近 Turn 摘要。</summary>
    public const string TurnSummariesConversationLastSequenceIndex =
        "IX_conversation_turn_summaries_conversation_id_last_sequence";

    /// <summary>
    /// 消息过程分页/首屏消息过程摘要：按 conversation_id + message_id + sequence 收敛。
    /// 属于存量库显式维护索引，不在每次启动时创建（见 bootstrapper 的 EnsureConversationEventIndexesAsync）。
    /// </summary>
    public const string ConversationEventsConversationMessageSequenceIndex =
        "IX_conversation_events_conversation_id_message_id_sequence";

    /// <summary>
    /// Turn 过程分页/根终态收敛：按 conversation_id + turn_id + sequence 收敛。
    /// 属于存量库显式维护索引，不在每次启动时创建。
    /// </summary>
    public const string ConversationEventsConversationTurnSequenceIndex =
        "IX_conversation_events_conversation_id_turn_id_sequence";

    /// <summary>存量库需要显式补齐的两条事件查询索引（维护窗口执行；顺序用于进度/日志）。</summary>
    public static readonly IReadOnlyList<string> ConversationEventLookupIndexes =
    [
        ConversationEventsConversationMessageSequenceIndex,
        ConversationEventsConversationTurnSequenceIndex,
    ];
}
