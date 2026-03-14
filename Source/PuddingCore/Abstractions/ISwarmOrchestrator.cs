using PuddingCode.Models;

namespace PuddingCode.Abstractions;

/// <summary>
/// 蜂群编排器。管理契约驱动的 Leader-Worker 协作。
/// </summary>
public interface ISwarmOrchestrator
{
    /// <summary>
    /// 处理蜂群模式的完整工作流，以事件流形式返回。
    /// </summary>
    /// <param name="userInput">用户输入的需求描述。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>蜂群事件流，包含 SwarmStarted、ContractDefined、WorkerSpawned、TaskCompleted、SwarmCompleted 等事件。</returns>
    IAsyncEnumerable<AgentEvent> ProcessSwarmAsync(string userInput, CancellationToken ct = default);
}
