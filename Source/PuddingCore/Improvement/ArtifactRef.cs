namespace PuddingCode.Improvement;

/// <summary>
/// 资产句柄：指向「一份**既有的**、可被改进的资产」。
/// </summary>
/// <remarks>
/// <para>
/// 本类型是「更新既有资产」这件事的**前置条件**：没有统一句柄，就无法用同一份提案描述
/// 「更新这个技能」与「更新这条记忆」，于是每一次改进都只能退化成"在某条通道里新增一个东西"。
/// </para>
/// <para>
/// 本类型是**纯值对象**：不持有服务、不做 IO、不依赖任何具体存储 ——
/// L3 只描述落点，不执行落点（执行由各通道的适配器负责）。
/// </para>
/// </remarks>
public sealed record ArtifactRef
{
    /// <summary>资产种类（决定落到哪条消费通道）。</summary>
    public required ArtifactKind Kind { get; init; }

    /// <summary>该种类内稳定且唯一的 id。</summary>
    public required string Id { get; init; }

    /// <summary>
    /// 观测到的版本（如技能语义版本、章节修订号）。
/// <para>未知为 <c>null</c> —— <b>不得编造</b>；「不知道版本」必须是可见的事实，而不是一个假值。</para>
    /// </summary>
    public string? Version { get; init; }

    /// <summary>证据引用（canonical 事件 id / turn id / 指标 key 等），用于事后核实本句柄从何而来。</summary>
    public IReadOnlyList<string> EvidenceRefs { get; init; } = [];

    /// <summary>规范化句柄串（<c>kind:id@version</c>），供幂等键、日志与跨通道对账使用。</summary>
    public string Handle
        => Version is { Length: > 0 } version ? $"{Kind}:{Id}@{version}" : $"{Kind}:{Id}";

    /// <summary>
    /// 校验式工厂：非法句柄在**构造期**即被拒绝。
    /// </summary>
    /// <exception cref="ArgumentException">种类为 <see cref="ArtifactKind.Unknown"/>，或 id 为空。</exception>
    public static ArtifactRef Create(
        ArtifactKind kind,
        string id,
        string? version = null,
        IReadOnlyList<string>? evidenceRefs = null)
    {
        if (kind == ArtifactKind.Unknown)
        {
            throw new ArgumentException(
                "资产种类不得为 Unknown：无种类就无法确定消费通道，产物会变成不可达的死数据。",
                nameof(kind));
        }

        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("资产 id 不得为空：无 id 的句柄无法寻址、无法回滚。", nameof(id));
        }

        return new ArtifactRef
        {
            Kind = kind,
            Id = id.Trim(),
            // 空白版本一律规范化为 null：避免出现 `kind:id@` 这种"看起来有版本"的假句柄。
            Version = string.IsNullOrWhiteSpace(version) ? null : version.Trim(),
            EvidenceRefs = evidenceRefs ?? [],
        };
    }

    /// <summary>
    /// 结构化相等：显式按**序列内容**比较证据引用。
    /// </summary>
    /// <remarks>
    /// 为什么必须显式写：C# record 的自动相等对集合成员用的是**引用相等**，
    /// 于是"两份内容相同的句柄"会被判为不等 —— 在去重与幂等键场景里这是静默错误。
    /// </remarks>
    public bool Equals(ArtifactRef? other)
        => other is not null
           && Kind == other.Kind
           && string.Equals(Id, other.Id, StringComparison.Ordinal)
           && string.Equals(Version, other.Version, StringComparison.Ordinal)
           && EvidenceRefs.SequenceEqual(other.EvidenceRefs);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Kind, Id, Version, EvidenceRefs.Count);
}
