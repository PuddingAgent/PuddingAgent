using PuddingCode.Abstractions;

namespace PuddingCode.Platform;

/// <summary>
/// Agent 执行快照工厂 — Run 启动时一次性读取 Agent/Template/LLM/Skill 配置
/// 并组装为不可变的 AgentExecutionSnapshot。
/// 同一 Turn 的重试复用第一次生成的快照（按 Run.SnapshotId 读取）。
/// 快照不保存 API Key；LlmConfig 内密钥在快照化前剥离。
/// </summary>
public interface IAgentExecutionSnapshotFactory
{
    /// <summary>
    /// 从已由应用服务统一解析的运行时 Profile 创建执行快照。
    /// 如果 previousSnapshot 非空（重试场景），应尽力复用原快照中的配置。
    /// </summary>
    /// <param name="profile">不含明文密钥的快照来源；调用方不得自行再次读取配置文件。</param>
    /// <param name="previousSnapshot">前次运行的快照（重试场景），首次运行为 null。</param>
    /// <param name="ct">取消令牌。</param>
    Task<AgentExecutionSnapshot> CreateAsync(
        AgentRuntimeProfile profile,
        AgentExecutionSnapshot? previousSnapshot,
        CancellationToken ct);

    /// <summary>
    /// 根据 SnapshotId 查找已存在的快照。
    /// 用于重试时复用首次生成的快照。
    /// </summary>
    Task<AgentExecutionSnapshot?> FindByIdAsync(
        string snapshotId,
        CancellationToken ct);
}
