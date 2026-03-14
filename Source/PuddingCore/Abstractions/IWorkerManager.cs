using PuddingCode.Models;

namespace PuddingCode.Abstractions;

/// <summary>
/// Worker 生命周期管理。负责 Worker 的生成、销毁和状态查询。
/// </summary>
public interface IWorkerManager
{
    /// <summary>
    /// 生成新的 Worker Agent。
    /// </summary>
    /// <param name="role">Worker 角色（Leader/Builder/QA/Docs）。</param>
    /// <param name="taskPrompt">分配给 Worker 的任务描述。</param>
    /// <param name="scope">Worker 的作用域约束（允许修改的文件/符号）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>Worker 信息，包含 ID、角色、作用域等。</returns>
    Task<WorkerInfo> SpawnWorkerAsync(WorkerRole role, string taskPrompt, WorkerScope scope, CancellationToken ct = default);

    /// <summary>
    /// 销毁 Worker Agent，清理相关资源（如 Git Worktree）。
    /// </summary>
    /// <param name="workerId">Worker ID。</param>
    /// <param name="ct">取消令牌。</param>
    Task DismissWorkerAsync(string workerId, CancellationToken ct = default);

    /// <summary>
    /// 获取当前活跃的 Worker 列表。
    /// </summary>
    /// <returns>只读的 Worker 信息列表。</returns>
    IReadOnlyList<WorkerInfo> GetActiveWorkers();
}
