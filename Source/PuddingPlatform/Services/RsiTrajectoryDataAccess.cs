using Microsoft.EntityFrameworkCore;
using PuddingCode.Platform;
using PuddingPlatform.Data;

namespace PuddingPlatform.Services;

/// <summary>
/// RSI S3 B2（规格 §2.6.3）：EF 实现，照抄 SkillEvolutionDataAccess 的成熟模式（逐条照抄，不另发明）。
/// <para>
/// 索引硬约束（§1.4）：conversation_events 的全部索引为 (ConversationId, Sequence) UNIQUE、EventId UNIQUE、
/// <b>(TurnId, Type)</b>、CommittedAt ⇒ 本类全部查询只落在 (TurnId, Type) 上；
/// ⛔ 禁止 CommandId 查询形状（无索引 ⇒ 全表扫描）；⛔ 禁止取回后内存过滤类型（过滤必须在 SQL 侧）。
/// </para>
/// </summary>
public sealed class RsiTrajectoryDataAccess : IRsiTrajectoryDataAccess
{
    private readonly IDbContextFactory<PlatformDbContext> _dbFactory;

    public RsiTrajectoryDataAccess(IDbContextFactory<PlatformDbContext> dbFactory) =>
        _dbFactory = dbFactory;

    public async Task<IReadOnlyList<RsiEventRowWithTurn>> GetEventsByTurnIdsAsync(
        string[] turnIds, string[] eventTypes, CancellationToken ct)
    {
        // 空入参早退（两个数组都要判，照抄既有模式）：避免 Contains 空数组生成退化 SQL。
        if (turnIds is null || turnIds.Length == 0)
        {
            return [];
        }

        if (eventTypes is null || eventTypes.Length == 0)
        {
            return [];
        }

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var entities = await db.ConversationEvents
            .AsNoTracking()
            .Where(evt => turnIds.Contains(evt.TurnId)
                          && eventTypes.Contains(evt.Type))
            // ⭐ 确定性排序：先 TurnId 后 Sequence；不得改成「只按 Sequence」跨 turn 混排。
            .OrderBy(evt => evt.TurnId)
            .ThenBy(evt => evt.Sequence)
            .ToListAsync(ct);

        // 投影成 row record（不向外泄 EF 实体）。
        // occurred_at 是 string 列：经 §2.7 契约解析（InvariantCulture + AssumeUniversal + ToUniversalTime）；
        // 不可解析 ⇒ ParseUtc fail-closed 抛出，不降级不丢行。
        return entities.Select(e => new RsiEventRowWithTurn
        {
            TurnId = e.TurnId,
            Type = e.Type,
            Sequence = e.Sequence,
            Payload = e.Payload,
            OccurredAtUtc = RsiEventTimestamp.ParseUtc(e.OccurredAt, e.TurnId),
        }).ToList();
    }
}
