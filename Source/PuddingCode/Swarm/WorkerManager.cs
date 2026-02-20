using System.Collections.Concurrent;
using PuddingCode.Abstractions;
using PuddingCode.Models;

namespace PuddingCode.Swarm;

/// <summary>
/// Worker 生命周期管理器。实现 <see cref="IWorkerManager"/> 接口，
/// 负责 Worker 的生成、销毁和状态查询。
/// </summary>
/// <remarks>
/// Phase 1 (V0.8) 仅实现骨架，Phase 2 (V0.9) 将实现完整的 Worker 生成逻辑。
/// </remarks>
public sealed class WorkerManager : IWorkerManager
{
    private readonly ConcurrentDictionary<string, WorkerInfo> _workers = new();

    /// <inheritdoc />
    /// <remarks>
    /// TODO Phase 2 (V0.9): 实现完整的 Worker 生成逻辑，包括：
    /// - 创建 AgentOrchestrator 实例
    /// - 分配角色和 System Prompt
    /// - 创建 Git Worktree
    /// - 注入 WorkerScope
    /// </remarks>
    public Task<WorkerInfo> SpawnWorkerAsync(WorkerRole role, string taskPrompt, WorkerScope scope, CancellationToken ct = default)
    {
        // TODO Phase 2 (V0.9): 实现完整的 Worker 生成逻辑
        // - 创建 AgentOrchestrator 实例并分配角色
        // - 创建 Git Worktree 作为工作目录
        // - 注入 WorkerScope 限制访问范围
        // - 注册到 _workers 字典中
        throw new NotImplementedException("// Phase 2 implementation - see Task 14");
    }

    /// <inheritdoc />
    /// <remarks>
    /// TODO Phase 2 (V0.9): 实现 Worker 销毁逻辑，包括清理 Git Worktree 和释放资源。
    /// </remarks>
    public Task DismissWorkerAsync(string workerId, CancellationToken ct = default)
    {
        // TODO Phase 2 (V0.9): 实现 Worker 销毁逻辑
        // - 从 _workers 字典中移除
        // - 清理 Git Worktree
        // - 释放相关资源
        throw new NotImplementedException("// Phase 2 implementation");
    }

    /// <inheritdoc />
    public IReadOnlyList<WorkerInfo> GetActiveWorkers()
    {
        return _workers.Values.ToList().AsReadOnly();
    }
}
