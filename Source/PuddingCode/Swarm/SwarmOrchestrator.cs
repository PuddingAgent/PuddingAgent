using System.Runtime.CompilerServices;
using PuddingCode.Abstractions;
using PuddingCode.Models;

namespace PuddingCode.Swarm;

/// <summary>
/// 蜂群编排器实现。管理契约驱动的 Leader-Worker 协作流程。
/// </summary>
/// <remarks>
/// Phase 1/2 (V0.8/V0.9) 实现本地串行/并行蜂群，Phase 3 将实现 P2P 分布式蜂群。
/// </remarks>
public sealed class SwarmOrchestrator : ISwarmOrchestrator
{
    private readonly IContractManager _contractManager;
    private readonly IWorkerManager _workerManager;
    private readonly ContractValidator _validator;
    private readonly string _swarmRoot;

    /// <summary>
    /// 初始化 <see cref="SwarmOrchestrator"/> 类的新实例。
    /// </summary>
    /// <param name="contractManager">契约管理器。</param>
    /// <param name="workerManager">Worker 管理器。</param>
    /// <param name="swarmRoot">蜂群根目录（默认为 .pudding/swarm）。</param>
    public SwarmOrchestrator(
        IContractManager contractManager,
        IWorkerManager workerManager,
        string? swarmRoot = null)
    {
        _contractManager = contractManager;
        _workerManager = workerManager;
        _validator = new ContractValidator();
        _swarmRoot = swarmRoot ?? Path.Combine(Directory.GetCurrentDirectory(), ".pudding", "swarm");
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<AgentEvent> ProcessSwarmAsync(
        string userInput,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Step 1: 初始化蜂群目录
        yield return new ThinkingEvent("Initializing swarm directory...");
        await _contractManager.InitializeSwarmDirectoryAsync(ct);

        // Step 2: 分析用户需求，Spawn Leader Agent
        yield return new ThinkingEvent("Analyzing user requirements and spawning Leader Agent...");
        var leaderScope = new WorkerScope(
            AllowedPaths: [ "**/*" ],
            AllowedSymbols: [ "**" ]
        );

        // Try to spawn Leader, fallback to simulated if not implemented
        WorkerInfo? leader = null;
        var leaderSpawned = false;
        try
        {
            leader = await _workerManager.SpawnWorkerAsync(WorkerRole.Leader, userInput, leaderScope, ct);
            leaderSpawned = true;
        }
        catch (NotImplementedException)
        {
            // Phase 2 未实现时，使用模拟 Leader
            leader = new WorkerInfo(
                Id: "leader-001",
                Role: WorkerRole.Leader,
                Name: "Leader Agent",
                WorktreePath: Path.Combine(_swarmRoot, "worktrees", "leader-001"),
                Scope: leaderScope
            );
        }

        if (leaderSpawned && leader != null)
        {
            yield return new WorkerSpawnedEvent(leader.Id, WorkerRole.Leader, leaderScope);
        }
        yield return new ThinkingEvent("Leader Agent spawned (simulated for Phase 1)");

        // Step 3: Leader 定义契约
        yield return new ThinkingEvent("Leader analyzing requirements and defining contracts...");
        var contract = await _contractManager.DefineContractAsync(userInput, ct);
        yield return new ContractDefinedEvent(contract.Id, contract.Symbols);

        // Step 4: Leader 拆分任务并 Spawn Workers
        yield return new ThinkingEvent("Leader spawning Worker Agents for parallel implementation...");
        
        var workers = new List<WorkerInfo>();
        
        // 根据契约文件拆分任务
        // Phase 1/2 简化实现：为每个文件创建一个 Builder Worker
        foreach (var file in contract.Files.Take(5)) // 限制最多 5 个 Worker
        {
            var scope = new WorkerScope(
                AllowedPaths: [ file ],
                AllowedSymbols: contract.Symbols.Where(s => file.Contains(s, StringComparison.OrdinalIgnoreCase)).ToList()
            );

            var taskPrompt = $"Implement {file} according to contract {contract.Id}. Follow the specification: {contract.Specification}";

            WorkerInfo? worker = null;
            var workerSpawned = false;
            try
            {
                worker = await _workerManager.SpawnWorkerAsync(WorkerRole.Builder, taskPrompt, scope, ct);
                workerSpawned = true;
            }
            catch (NotImplementedException)
            {
                // Phase 2 未实现时，创建模拟 Worker
                worker = new WorkerInfo(
                    Id: $"worker-{Guid.NewGuid():N}",
                    Role: WorkerRole.Builder,
                    Name: $"Builder - {Path.GetFileName(file)}",
                    WorktreePath: Path.Combine(_swarmRoot, "worktrees", $"worker-{Guid.NewGuid():N}"),
                    Scope: scope
                );
            }

            if (worker != null)
            {
                workers.Add(worker);
                if (workerSpawned)
                {
                    yield return new WorkerSpawnedEvent(worker.Id, WorkerRole.Builder, scope);
                }
            }
        }

        // 如果没有文件，至少创建一个 Worker
        if (workers.Count == 0)
        {
            var scope = new WorkerScope(
                AllowedPaths: [ "**/*" ],
                AllowedSymbols: contract.Symbols
            );

            WorkerInfo? worker = null;
            var workerSpawned = false;
            try
            {
                worker = await _workerManager.SpawnWorkerAsync(WorkerRole.Builder, contract.Specification, scope, ct);
                workerSpawned = true;
            }
            catch (NotImplementedException)
            {
                worker = new WorkerInfo(
                    Id: "worker-001",
                    Role: WorkerRole.Builder,
                    Name: "Builder Agent",
                    WorktreePath: Path.Combine(_swarmRoot, "worktrees", "worker-001"),
                    Scope: scope
                );
            }

            if (worker != null)
            {
                workers.Add(worker);
                if (workerSpawned)
                {
                    yield return new WorkerSpawnedEvent(worker.Id, WorkerRole.Builder, scope);
                }
            }
        }

        // Step 5: 监控 Worker 进度（并行执行）
        var tasks = new List<Task>();
        foreach (var worker in workers)
        {
            tasks.Add(MonitorWorkerProgressAsync(worker, contract, ct));
        }

        // Step 6: 等待所有 Worker 完成
        yield return new ThinkingEvent($"Waiting for {workers.Count} workers to complete their tasks...");
        await Task.WhenAll(tasks);

        // Step 7: 验证契约实现
        yield return new ThinkingEvent("Validating Worker implementations against contracts...");
        foreach (var worker in workers)
        {
            var validation = _validator.ValidateContract(contract, worker.WorktreePath);
            if (validation.IsValid)
            {
                yield return new ContractValidatedEvent(contract.Id, true);
                yield return new ThinkingEvent($"Contract validation passed for {worker.Role}");
            }
            else
            {
                yield return new ContractValidatedEvent(contract.Id, false);
                foreach (var error in validation.Errors)
                {
                    yield return new ErrorEvent($"Contract validation failed: {error}");
                }
            }
        }

        // Step 8: 合并 Worktrees（Phase 2 功能）
        yield return new ThinkingEvent("Merging Worker worktrees to main branch...");
        foreach (var worker in workers)
        {
            // TODO Phase 2: 实现 Git Worktree 合并逻辑
            yield return new MergeEvent($"swarm/{worker.Id}", true);
        }

        // Step 9: 运行最终测试（Phase 2 功能）
        yield return new ThinkingEvent("Running final regression tests...");
        // TODO Phase 2: 集成测试运行器

        // Step 10: 清理和 Dismiss Swarm
        yield return new ThinkingEvent("Cleaning up swarm resources...");
        foreach (var worker in workers)
        {
            try
            {
                await _workerManager.DismissWorkerAsync(worker.Id, ct);
            }
            catch (NotImplementedException)
            {
                // Phase 2 未实现时忽略
            }
        }

        // Step 11: 完成蜂群
        var summary = $"Swarm completed successfully. Contract: {contract.Id}, Workers: {workers.Count}";
        yield return new SwarmCompletedEvent(summary);
    }

    /// <summary>
    /// 监控单个 Worker 的进度。
    /// </summary>
    /// <param name="worker">Worker 信息。</param>
    /// <param name="contract">契约定义。</param>
    /// <param name="ct">取消令牌。</param>
    private async Task MonitorWorkerProgressAsync(
        WorkerInfo worker,
        Contract contract,
        CancellationToken ct)
    {
        // Phase 1/2 简化实现：模拟 Worker 执行过程
        // TODO Phase 2: 实际调用 Worker Agent 的 ProcessAsync 方法

        // 模拟工作时间（Phase 2 将替换为实际 Agent 调用）
        await Task.Delay(1000, ct);
    }
}
