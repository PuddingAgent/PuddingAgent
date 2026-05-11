using PuddingCode.Platform;

namespace PuddingCode.Abstractions;

/// <summary>
/// 潜意识编排器抽象：在主对话链路外异步执行记忆整合、摘要和增强召回。
/// 阶段 1 先定义稳定契约，阶段 2 再补齐 LLM 抽取与合并逻辑。
/// </summary>
public interface ISubconsciousOrchestrator
{
    /// <summary>
    /// 异步记忆整合：从已完成会话中抽取事实/偏好并执行去重合并。
    /// </summary>
    Task ConsolidateAsync(
        ConsolidationJob job,
        string memorySearchMode,
        MemoryLlmConfig? memoryLlmConfig = null,
        CancellationToken ct = default);

    /// <summary>
    /// 生成会话结构化摘要。
    /// </summary>
    Task<SessionSummary> SummarizeSessionAsync(
        string sessionId,
        string workspaceId,
        string agentId,
        CancellationToken ct = default);

    /// <summary>
    /// 增强召回（deep 模式入口）：在基础召回上叠加潜意识补充结果。
    /// </summary>
    Task<string?> RecallAugmentedAsync(
        string userMessage,
        string workspaceId,
        string agentId,
        string? sessionId = null,
        int maxTokens = 2000,
        CancellationToken ct = default);

    /// <summary>
    /// 获取记忆仪表盘摘要数据。
    /// </summary>
    Task<MemoryDashboard> GetMemoryDashboardAsync(
        string workspaceId,
        CancellationToken ct = default);

    /// <summary>
    /// 分页搜索记忆条目（供管理界面使用）。
    /// </summary>
    Task<MemorySearchResult> SearchMemoriesAsync(
        MemorySearchRequest request,
        CancellationToken ct = default);
}
