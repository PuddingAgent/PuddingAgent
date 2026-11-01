namespace PuddingMemoryEngine.Services;

/// <summary>
/// 把 <see cref="FactMemoryDraft"/> 落进 `MemoryFacts` 的实现（走既有的 <see cref="FactMemoryService"/>）。
/// <para>
/// 这是"接线"的另一半：<see cref="FactMemoryService"/> 一直存在但生产调用点为 0，
/// 由本类把它挂在 <see cref="IFactMemorySink"/> 这个可测试的出口上。
/// </para>
/// <para>
/// 三个刻意的行为：
/// ① 记忆空间按 (workspace, agent) 自动保证存在 —— 调用方不必先建空间；
/// ② 证据必填（引擎要求 ≥1 条），这里用「来源会话」作为最小可信证据；
/// ③ **不吞异常**：写入失败要让上层看见（由调用方决定是记日志还是中断），
///    因为"静默写失败"正是这次要根治的问题。
/// </para>
/// </summary>
public sealed class FactMemorySink(FactMemoryService facts) : IFactMemorySink
{
    private readonly FactMemoryService _facts = facts;

    public async Task SinkAsync(FactMemoryDraft draft, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(draft);

        if (string.IsNullOrWhiteSpace(draft.Statement))
            throw new ArgumentException("Statement is required.", nameof(draft));

        var space = await _facts
            .EnsureDefaultMemorySpaceAsync(draft.WorkspaceId, draft.AgentId, ct)
            .ConfigureAwait(false);

        var request = new CreateFactRequest
        {
            Statement = draft.Statement,
            FactType = string.IsNullOrWhiteSpace(draft.FactType) ? "fact" : draft.FactType,
            Evidence =
            [
                new CreateFactEvidenceRequest
                {
                    SourceType = "session",
                    SourceId = draft.SourceMessageId ?? draft.SourceSessionId,
                    QuoteSummary = draft.Statement.Length <= 200
                        ? draft.Statement
                        : draft.Statement[..200],
                    Confidence = 1.0,
                },
            ],
        };

        await _facts
            .CreateFactAsync(draft.WorkspaceId, draft.AgentId, space.MemorySpaceId, request, ct)
            .ConfigureAwait(false);
    }
}
