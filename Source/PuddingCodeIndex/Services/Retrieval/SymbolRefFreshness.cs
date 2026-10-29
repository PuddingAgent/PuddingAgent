using PuddingCodeIndex.Contracts.Retrieval;

namespace PuddingCodeIndex.Services.Retrieval;

/// <summary>ref 相对当前索引的两种状态（提案 §5 三态协议收敛后的两态；"不可重定位"由 <see cref="SymbolRefFreshnessResult.CanRelocate"/> 表达）。</summary>
public enum SymbolRefState
{
    /// <summary>内容 hash 与索引版本一致 —— <b>唯一</b>允许按 ref 行号直接打开的状态。</summary>
    Fresh,

    /// <summary>源版本已变化或无法确认 —— 不得沿旧行号打开。</summary>
    Stale,
}

/// <summary>重定位候选：当前版本中同一 <c>symbol_id</c> 的位置。</summary>
/// <param name="FilePath">候选所在文件（符号可能被移动到别的文件，如 partial 类拆分）。</param>
/// <param name="StartLine">1 基起始行。</param>
/// <param name="EndLine">1 基结束行。</param>
public sealed record SymbolRefRelocation(string FilePath, int StartLine, int EndLine);

/// <summary>
/// 判定结果。不变量（构造时强制，违反即抛）：
/// <list type="bullet">
/// <item><see cref="State"/> 为 <see cref="SymbolRefState.Fresh"/> ⇒ <see cref="Relocations"/> 必空；</item>
/// <item><see cref="CanRelocate"/> 为 true ⇔ <see cref="Relocations"/> 恰好 1 条；</item>
/// <item><see cref="Reason"/> 非空（只说"陈旧"不给理由，调用方无法决定下一步）。</item>
/// </list>
/// </summary>
public sealed record SymbolRefFreshnessResult
{
    private SymbolRefFreshnessResult(
        SymbolRefState state,
        string reason,
        IReadOnlyList<SymbolRefRelocation> relocations)
    {
        State = state;
        Reason = reason;
        Relocations = relocations;
        CanRelocate = state == SymbolRefState.Stale && relocations.Count == 1;
    }

    /// <summary>当前状态。</summary>
    public SymbolRefState State { get; }

    /// <summary>是否可重定位到唯一位置。**不表示旧 ref 仍有效**，只表示"有一个候选"。</summary>
    public bool CanRelocate { get; }

    /// <summary>可读理由；Fresh 时说明行号为何权威，Stale 时说明下一步该怎么做。</summary>
    public string Reason { get; }

    /// <summary>重定位候选（0..N）；<see cref="CanRelocate"/> 为 true 时恰好 1 条。</summary>
    public IReadOnlyList<SymbolRefRelocation> Relocations { get; }

    /// <summary>内容 hash 一致 ⇒ 行号权威。</summary>
    public static SymbolRefFreshnessResult Fresh() =>
        new(SymbolRefState.Fresh, "content_hash 与索引版本一致，行号权威。", Array.Empty<SymbolRefRelocation>());

    /// <summary>陈旧（或无法确认）。候选数量决定 <see cref="CanRelocate"/>，不由调用方指定。</summary>
    public static SymbolRefFreshnessResult Stale(string reason, IReadOnlyList<SymbolRefRelocation>? relocations)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("陈旧结论必须给出理由（只说'陈旧'调用方无法决定下一步）。", nameof(reason));

        var candidates = relocations ?? Array.Empty<SymbolRefRelocation>();
        foreach (var candidate in candidates)
        {
            if (candidate is null)
                throw new ArgumentException("重定位候选不得为 null。", nameof(relocations));
        }

        return new SymbolRefFreshnessResult(SymbolRefState.Stale, reason, candidates);
    }
}

/// <summary>
/// `symbol_ref` 的 FRESH / STALE 判定（symbol_ref 提案 §5/§6）。
/// <para>
/// 规格依据：设计文档 §8.3 —— 源码变化后<b>不得</b>沿旧行号打开当前代码；不能安全打开时必须明说不能。
/// </para>
/// <para>
/// 这是**纯判定函数**：不读盘、不查库，输入是"当前文件的 content_hash"与"当前版本里同 id 的位置"，
/// 由调用方提供。这样三态协议可以在组件测试里穷举，而不必先起索引。
/// </para>
/// <para>
/// 三条刻意设计：
/// ① 只有 hash 相等才 <see cref="SymbolRefState.Fresh"/> —— 把 §8.3 的禁令收成一个唯一入口，
/// 而不是散落在每个 view 的 if 里；
/// ② 重定位成功也**仍是 Stale** —— "找到候选"与"你手里的 ref 仍有效"是两件事，混起来就是又一次
/// hollow negation；
/// ③ 零候选的措辞不得暗示符号不存在 —— 范围缺口/未索引不能用来否定代码存在。
/// </para>
/// </summary>
public static class SymbolRefFreshness
{
    /// <summary>
    /// 判定 ref 能否按自身行号打开。
    /// </summary>
    /// <param name="reference">待判定的 ref。</param>
    /// <param name="currentContentHash">
    /// 当前索引中该文件的 content_hash；文件未索引/未知时传 <c>null</c> 或空白
    /// （此时**不能**当作 Fresh —— 无法证明一致就不算一致）。
    /// </param>
    /// <param name="relocationCandidates">当前版本中同一 <c>symbol_id</c> 的位置；未知时传 <c>null</c>。</param>
    public static SymbolRefFreshnessResult Evaluate(
        SymbolRefV1 reference,
        string? currentContentHash,
        IReadOnlyList<SymbolRefRelocation>? relocationCandidates)
    {
        ArgumentNullException.ThrowIfNull(reference);

        var candidates = relocationCandidates ?? Array.Empty<SymbolRefRelocation>();

        var hashKnown = !string.IsNullOrWhiteSpace(currentContentHash);
        if (hashKnown && string.Equals(currentContentHash!.Trim(), reference.ContentHash, StringComparison.Ordinal))
            return SymbolRefFreshnessResult.Fresh();

        if (!hashKnown)
        {
            return SymbolRefFreshnessResult.Stale(
                "当前索引缺少该文件的版本信息，无法证明 ref 与当前内容一致 ⇒ 不得沿旧行号打开；" +
                "请用 workspace_search 按名称重新定位，或先确认该项目已索引。",
                candidates);
        }

        // 版本已变化：能否重定位取决于 symbol_id 的形态（提案 §6）。
        var stability = SymbolRefV1.ClassifySymbolId(reference.SymbolId);
        if (stability == SymbolIdStability.PositionDependent)
        {
            return SymbolRefFreshnessResult.Stale(
                "源版本已变化（content_hash 不同），且该符号标识含位置信息（@span），" +
                "不可重定位；请用 workspace_search 按名称重新检索。",
                Array.Empty<SymbolRefRelocation>());
        }

        if (stability == SymbolIdStability.Unknown)
        {
            return SymbolRefFreshnessResult.Stale(
                "源版本已变化（content_hash 不同），且该符号标识形态无法判定可重定位性，" +
                "按不可重定位处理；请用 workspace_search 按名称重新检索。",
                Array.Empty<SymbolRefRelocation>());
        }

        return candidates.Count switch
        {
            0 => SymbolRefFreshnessResult.Stale(
                "源版本已变化（content_hash 不同），当前版本中无法定位该 ref；" +
                "这不代表符号已删除，请用 workspace_search 按名称与签名检索。",
                candidates),
            1 => SymbolRefFreshnessResult.Stale(
                "源版本已变化（content_hash 不同）；唯一候选见 relocations，请用新行号重新发起请求" +
                "（本结果不表示旧行号仍然有效）。",
                candidates),
            _ => SymbolRefFreshnessResult.Stale(
                $"源版本已变化且有 {candidates.Count} 个候选，无法判定是哪一个；" +
                "请用 workspace_search 按名称与签名消歧后再打开。",
                candidates),
        };
    }
}
