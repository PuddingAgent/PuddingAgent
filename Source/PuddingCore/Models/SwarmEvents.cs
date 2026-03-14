namespace PuddingCode.Models;

/// <summary>Swarm started — Leader Agent 初始化蜂群</summary>
public sealed record SwarmStartedEvent(int WorkerCount) : AgentEvent;

/// <summary>Contract defined — Leader 定义了契约（接口/方法签名）</summary>
public sealed record ContractDefinedEvent(string ContractId, IReadOnlyList<string> Symbols) : AgentEvent;

/// <summary>Worker spawned — 创建了一个 Worker Agent</summary>
public sealed record WorkerSpawnedEvent(string WorkerId, WorkerRole Role, WorkerScope Scope) : AgentEvent;

/// <summary>Task assigned — Leader 将任务分配给 Worker</summary>
public sealed record TaskAssignedEvent(string TaskId, string WorkerId, string ContractId) : AgentEvent;

/// <summary>Task completed — Worker 完成了任务</summary>
public sealed record TaskCompletedEvent(string TaskId, string WorkerId, string Summary) : AgentEvent;

/// <summary>Task failed — Worker 任务失败</summary>
public sealed record TaskFailedEvent(string TaskId, string WorkerId, string Reason) : AgentEvent;

/// <summary>Contract validated — Leader 验证 Worker 实现是否匹配契约</summary>
public sealed record ContractValidatedEvent(string ContractId, bool Passed) : AgentEvent;

/// <summary>Worktree merged — Worker 分支已合并到主分支</summary>
public sealed record MergeEvent(string Branch, bool Success) : AgentEvent;

/// <summary>Leader elected — P2P 模式下选举出新 Leader（Phase 3）</summary>
public sealed record LeaderElectedEvent(string NodeId) : AgentEvent;

/// <summary>Swarm completed — 蜂群完成所有任务</summary>
public sealed record SwarmCompletedEvent(string Summary) : AgentEvent;
