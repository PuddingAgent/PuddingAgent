namespace PuddingMemoryEngine.Services;

/// <summary>
/// 一次「事实记忆」的落库草案。
/// <para>
/// 刻意做成最小字段集：写入方（如 save_memory 工具）只提供事实本身与出处，
/// 编号 / 空间 / 新鲜度由 sink 内部决定。这样调用方不需要知道 MemoryFacts 的表结构。
/// </para>
/// </summary>
public sealed record FactMemoryDraft(
    string WorkspaceId,
    string AgentId,
    string Statement,
    string FactType,
    string SourceSessionId,
    string? SourceMessageId = null);

/// <summary>
/// 事实记忆的落库出口。
/// <para>
/// 存在的理由（2026-11-01 实测）：<c>FactMemoryService.CreateFactAsync</c> 生产调用点为 **0**，
/// 导致 `MemoryFacts` 恒为 0 行 —— 服务注册了、测试全绿、就是没人接线。
/// 把"谁来写"抽象成这个接口后，接线点可以被单测证明存在，而不再靠人肉审查。
/// </para>
/// </summary>
public interface IFactMemorySink
{
    Task SinkAsync(FactMemoryDraft draft, CancellationToken ct = default);
}

/// <summary>默认实现：不落表。用于开关关闭或测试环境，保证接线存在但零副作用。</summary>
public sealed class NullFactMemorySink : IFactMemorySink
{
    public static NullFactMemorySink Instance { get; } = new();

    public Task SinkAsync(FactMemoryDraft draft, CancellationToken ct = default) => Task.CompletedTask;
}
