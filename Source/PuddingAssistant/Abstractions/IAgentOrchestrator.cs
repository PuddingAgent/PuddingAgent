using PuddingAssistant.Models;

namespace PuddingAssistant.Abstractions;

/// <summary>
/// Agent 主循环编排器。处理 "对话 → Tool Call → 执行 → 回传" 的闭环。
/// </summary>
public interface IAgentOrchestrator
{
    /// <summary>处理用户的一次输入，以事件流形式返回</summary>
    IAsyncEnumerable<AgentEvent> ProcessAsync(string userInput, CancellationToken ct = default);
}
