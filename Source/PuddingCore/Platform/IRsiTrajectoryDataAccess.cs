namespace PuddingCode.Platform;

/// <summary>
/// RSI S3 B2（规格 §2.6.1）：EF 边界件返回的事件行 —— <b>带 TurnId</b>（跨程序集的「查询结果」形状）。
/// 与 B1 的 <c>RsiEventRow</c>（PuddingRuntime，<c>RsiTurnSlice</c> 内的「turn 内切片」、不带 TurnId）层次不同，不得合并。
/// 不复用 <c>ConversationEventRow</c>：后者没有 TurnId 与 OccurredAtUtc，类型层面装不下（§2.6.1 已裁决）。
/// </summary>
public sealed record RsiEventRowWithTurn
{
    public required string TurnId { get; init; }

    public required string Type { get; init; }

    /// <summary>稳定排序键（不得依赖 DB 返回顺序）。</summary>
    public required long Sequence { get; init; }

    /// <summary>payload JSON（原样透传，由上层结局推导器解析）。</summary>
    public string? Payload { get; init; }

    /// <summary>事件发生时间（UTC）。</summary>
    public required DateTimeOffset OccurredAtUtc { get; init; }
}

/// <summary>
/// RSI S3 B2 数据访问接缝（规格 §2.2 / §2.6）。
/// 定义在 PuddingCore 的原因（§2.8 硬约束）：PuddingRuntime.csproj 不引用 PuddingPlatform / EF，
/// 接口与行形状必须在 PuddingCore，EF 实现在 PuddingPlatform。
/// <para>
/// 只暴露「按 TurnId 批量取事件」——即已索引键 (TurnId, Type) 的形状（§1.4）；
/// ⛔ 不得暴露按 CommandId 的查询：CommandId 无索引，任何该形状都是全表扫描。
/// </para>
/// </summary>
public interface IRsiTrajectoryDataAccess
{
    /// <summary>
    /// 按 turnIds 批量取事件行：SQL 侧做事件类型过滤；确定性排序 TurnId → Sequence；
    /// occurred_at 为 string 列，经 <see cref="RsiEventTimestamp.ParseUtc"/> 按 §2.7 契约解析
    /// （不可解析 fail-closed 抛出，不返回 null / MinValue / 丢行）。
    /// </summary>
    Task<IReadOnlyList<RsiEventRowWithTurn>> GetEventsByTurnIdsAsync(
        string[] turnIds, string[] eventTypes, CancellationToken ct);
}
