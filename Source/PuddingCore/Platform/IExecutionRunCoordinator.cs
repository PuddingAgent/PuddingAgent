namespace PuddingCode.Platform;

/// <summary>
/// 执行运行协调器 — Harness Execution Kernel 入口。
/// 接收 Worker 透传的原始 ExecutionLease，完成整个执行生命周期：
///   1. 从 Journal 恢复/验证 Turn 状态
///   2. 读取 Control Inbox 中待处理的 Cancel/Steering
///   3. 创建 AgentExecutionSnapshot
///   4. 启动 ITurnExecutor
///   5. 通过 Output Chunker 处理事件流
///   6. 通过 IExecutionJournal 提交普通事件和终态
/// 替换旧版 IChatCommandProcessor。
/// Scoped 服务，每个 Run 独立 Scope。
/// </summary>
public interface IExecutionRunCoordinator
{
    /// <summary>
    /// 执行一次完整的 Run。
    /// Lease 由 Worker 提供，Coordinator 透传给 Journal/Inbox，不复制，不伪造。</summary>
    /// <param name="lease">Worker 从 IExecutionLeaseStore 获取的原始租约。</param>
    /// <param name="hostStoppingToken">宿主关停令牌。</param>
    /// <returns>包含终态信息、事件序列范围和统计的结果。</returns>
    Task<ExecutionRunOutcome> ExecuteAsync(
        ExecutionLease lease,
        CancellationToken hostStoppingToken);
}
