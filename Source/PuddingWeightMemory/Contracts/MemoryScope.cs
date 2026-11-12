namespace PuddingWeightMemory;

/// <summary>
/// 权重记忆的分区身份，由宿主从可信执行身份构造。
/// <para>
/// 所有权：这是"谁的参数"这一事实。章节权重、快照指针与训练回执都按此三元组隔离，
/// 同一章节 ID 在不同用户或不同 Agent 下是两组互不相干的参数；组件不做任何跨分区读取。
/// </para>
/// </summary>
/// <param name="WorkspaceId">工作区标识。</param>
/// <param name="UserId">宿主已核验的用户标识，不由消息文本推断。</param>
/// <param name="AgentId">Agent 实例标识。</param>
public sealed record MemoryScope(string WorkspaceId, string UserId, string AgentId);
