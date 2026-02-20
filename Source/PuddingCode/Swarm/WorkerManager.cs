using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using CliWrap;
using CliWrap.Buffered;
using PuddingCode.Abstractions;
using PuddingCode.Models;

namespace PuddingCode.Swarm;

/// <summary>
/// Worker 生命周期管理器。实现 <see cref="IWorkerManager"/> 接口，
/// 负责 Worker 的生成、销毁和状态查询。
/// </summary>
public sealed class WorkerManager : IWorkerManager
{
    private readonly ConcurrentDictionary<string, WorkerInfo> _workers = new();
    private readonly string _workDir;

    /// <summary>
    /// 初始化 WorkerManager 的新实例。
    /// </summary>
    /// <param name="workDir">项目根目录（用于 Git Worktree 操作）。</param>
    public WorkerManager(string workDir)
    {
        ArgumentNullException.ThrowIfNull(workDir);
        _workDir = Path.GetFullPath(workDir);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Phase 2 (V0.9) 实现：
    /// - 创建唯一 Worker ID
    /// - 创建 Git Worktree 作为工作目录
    /// - 创建 AgentOrchestrator 实例并分配角色
    /// - 注入 WorkerScope 限制访问范围
    /// - 注册到 _workers 字典中
    /// </remarks>
    public async Task<WorkerInfo> SpawnWorkerAsync(
        WorkerRole role,
        string taskPrompt,
        WorkerScope scope,
        CancellationToken ct = default)
    {
        // 生成唯一 Worker ID
        var workerId = $"worker-{Guid.NewGuid().ToString("N")[..8]}";
        var branchName = $"swarm/{workerId}";
        var worktreePath = Path.Combine(_workDir, ".pudding", "worktrees", workerId);

        // 确保 .pudding/worktrees 目录存在
        Directory.CreateDirectory(Path.GetDirectoryName(worktreePath)!);

        // 创建 Git Worktree (如果分支已存在则先删除)
        try
        {
            await CreateWorktreeAsync(branchName, worktreePath, ct);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("already used"))
        {
            // 清理旧 worktree 和分支
            await CleanupExistingWorktreeAsync(branchName, worktreePath, ct);
            // 重试创建
            await CreateWorktreeAsync(branchName, worktreePath, ct);
        }

        // 创建 WorkerInfo
        var workerName = $"{role}-{workerId.Split('-')[1][..4]}";
        var workerInfo = new WorkerInfo(workerId, role, workerName, worktreePath, scope);

        // 注册到字典中
        _workers[workerId] = workerInfo;

        return workerInfo;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Phase 2 (V0.9) 实现：
    /// - 从 _workers 字典中移除
    /// - 清理 Git Worktree
    /// - 释放相关资源
    /// </remarks>
    public async Task DismissWorkerAsync(string workerId, CancellationToken ct = default)
    {
        if (!_workers.TryRemove(workerId, out var workerInfo))
        {
            throw new InvalidOperationException($"Worker '{workerId}' not found");
        }

        // 清理 Git Worktree
        await RemoveWorktreeAsync(workerInfo.WorktreePath, ct);
    }

    /// <inheritdoc />
    public IReadOnlyList<WorkerInfo> GetActiveWorkers()
    {
        return _workers.Values.ToList().AsReadOnly();
    }

    /// <summary>
    /// 清理已存在的 worktree 和分支。
    /// </summary>
    private async Task CleanupExistingWorktreeAsync(string branchName, string worktreePath, CancellationToken ct)
    {
        // 移除 worktree
        if (Directory.Exists(worktreePath))
        {
            try
            {
                await RunGitAsync(["worktree", "remove", worktreePath, "--force"], ct);
            }
            catch
            {
                // 忽略删除失败
            }
        }

        // 删除分支
        try
        {
            await RunGitAsync(["branch", "-D", branchName], ct);
        }
        catch
        {
            // 忽略删除失败
        }
    }

    /// <summary>
    /// 创建 Git Worktree。
    /// </summary>
    /// <param name="branchName">新分支名称。</param>
    /// <param name="worktreePath">Worktree 路径。</param>
    /// <param name="ct">取消令牌。</param>
    private async Task CreateWorktreeAsync(string branchName, string worktreePath, CancellationToken ct)
    {
        // 检查分支是否已存在，如果存在则先清理
        var existingBranches = await RunGitAsync(["worktree", "list"], ct);
        if (existingBranches.Contains(branchName, StringComparison.OrdinalIgnoreCase))
        {
            // 分支已存在，先清理
            await CleanupExistingWorktreeAsync(branchName, worktreePath, ct);
        }

        // 创建并切换到新分支（基于当前 HEAD）
        await RunGitAsync(["checkout", "-b", branchName], ct);

        // 创建 worktree
        await RunGitAsync(["worktree", "add", worktreePath, branchName], ct);
    }

    /// <summary>
    /// 移除 Git Worktree。
    /// </summary>
    /// <param name="worktreePath">Worktree 路径。</param>
    /// <param name="ct">取消令牌。</param>
    private async Task RemoveWorktreeAsync(string worktreePath, CancellationToken ct)
    {
        // 先删除分支
        var branchName = Path.GetFileName(worktreePath);
        await RunGitAsync(["branch", "-D", branchName], ct);

        // 移除 worktree
        await RunGitAsync(["worktree", "remove", worktreePath], ct);

        // 清理本地目录（如果还存在）
        if (Directory.Exists(worktreePath))
        {
            try
            {
                Directory.Delete(worktreePath, recursive: true);
            }
            catch (IOException)
            {
                // 忽略删除失败（可能文件被占用）
            }
        }
    }

    /// <summary>
    /// 运行 Git 命令。
    /// </summary>
    /// <param name="args">Git 命令参数。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>命令输出。</returns>
    private async Task<string> RunGitAsync(string[] args, CancellationToken ct)
    {
        var result = await Cli.Wrap("git")
            .WithArguments(args)
            .WithWorkingDirectory(_workDir)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);

        if (result.ExitCode != 0 && !string.IsNullOrWhiteSpace(result.StandardError))
        {
            var stderr = result.StandardError.Trim();
            // 忽略一些常见的非错误信息
            if (!stderr.Contains("already exists", StringComparison.OrdinalIgnoreCase) &&
                !stderr.Contains("nothing to commit", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"git {string.Join(' ', args)} failed: {stderr}");
            }
        }

        return result.StandardOutput;
    }
}
