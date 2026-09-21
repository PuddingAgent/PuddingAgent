using PuddingCode.Platform;

namespace PuddingRuntime.Services.Improvement.Rsi;

/// <summary>
/// RSI S3 B3（规格 §2.11 / §3.4）：会话级轨迹源实现 —— 只编排，不重写。
/// <para>
/// 组装链（冻结，全部复用）：GetRecentTurnIdsAsync → GetEventsByTurnIdsAsync → 按 TurnId 分组 →
/// 映射为 B1 的 RsiTurnSlice / RsiEventRow（盖章 scope 的三个身份字段）→ RsiTrajectoryAssembler.Assemble。
/// <list type="bullet">
/// <item>查询维度<b>只用 scope.ConversationId</b>；WorkspaceId / AgentInstanceId / SessionId 只用于盖章，不参与取数（§2.11）；</item>
/// <item>slice 顺序 = turnIds 的顺序（时间升序）；组内事件保持数据访问的确定性顺序（Sequence 升序，分组时不得打乱）；</item>
/// <item>结局判定一律复用 B1 装配器，本类不做任何结局逻辑；</item>
/// <item>eventTypes = requested / completed / failed 三类（§2.4；failed 照收但不依赖其出现）。</item>
/// </list>
/// </para>
/// </summary>
public sealed class RsiTrajectorySource : IRsiTrajectorySource
{
    private static readonly string[] ToolEventTypes =
    [
        ConversationEventTypes.ToolCallRequested,
        ConversationEventTypes.ToolCallCompleted,
        ConversationEventTypes.ToolCallFailed,
    ];

    private readonly IRsiTrajectoryDataAccess _dataAccess;

    public RsiTrajectorySource(IRsiTrajectoryDataAccess dataAccess) => _dataAccess = dataAccess;

    public async Task<IReadOnlyList<RsiTrajectory>> GetRecentAnnotatedAsync(
        RsiScope scope, int limit, CancellationToken ct)
    {
        // 边界行为（§2.12.4 冻结）：调用方错误（null scope）不得静默成空结果。
        ArgumentNullException.ThrowIfNull(scope);

        // 无意义入参 ⇒ 空结果且不访问数据库（可断言零调用）。
        if (limit <= 0 || string.IsNullOrWhiteSpace(scope.ConversationId))
        {
            return [];
        }

        var turnIds = await _dataAccess.GetRecentTurnIdsAsync(scope.ConversationId, limit, ct);
        if (turnIds.Count == 0)
        {
            return [];
        }

        var rows = await _dataAccess.GetEventsByTurnIdsAsync([.. turnIds], ToolEventTypes, ct);

        // 按 TurnId 分组：每个 bucket 按 rows 的既有顺序追加（Sequence 升序，不打乱）。
        var eventsByTurn = new Dictionary<string, List<RsiEventRow>>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (!eventsByTurn.TryGetValue(row.TurnId, out var bucket))
            {
                bucket = [];
                eventsByTurn.Add(row.TurnId, bucket);
            }

            bucket.Add(new RsiEventRow
            {
                Type = row.Type,
                Sequence = row.Sequence,
                Payload = row.Payload,
                OccurredAtUtc = row.OccurredAtUtc,
            });
        }

        var slices = new List<RsiTurnSlice>(turnIds.Count);
        foreach (var turnId in turnIds)
        {
            if (!eventsByTurn.TryGetValue(turnId, out var events))
            {
                continue;   // 该 turn 无工具事件 ⇒ 不产出轨迹（零工具调用不携带信号，§2.3）。
            }

            slices.Add(new RsiTurnSlice
            {
                // 盖章：三个身份字段一律逐字取入参 scope（§2.11 不推导），与任何数据行无关。
                WorkspaceId = scope.WorkspaceId,
                AgentInstanceId = scope.AgentInstanceId,
                SessionId = scope.SessionId,
                TurnId = turnId,
                Events = events,
            });
        }

        return RsiTrajectoryAssembler.Assemble(slices);
    }
}
