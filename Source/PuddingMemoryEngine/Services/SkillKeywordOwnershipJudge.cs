namespace PuddingMemoryEngine.Services;

/// <summary>
/// 关键词冲突的**性质**（RSI-G4 交付物 D5，任务书 §2.4-2）。
/// <para>
/// ⛔ 本枚举**刻意只有两项**：它们描述的是"关键词是什么"，**不含任何"谁该赢"的说法**。
/// 特别是：没有 `FirstArrivalWins` / `LaterDeclarerRejected` 这类成员 —— 任务书明令
/// 「⛔ 禁止"先到先得"作为默认裁决；⛔ 也不得把"后来者一律拒绝"写成默认」，
/// 而枚举成员集合本身就是取红载体（<c>Apply_ShouldNotDefaultToRejectingLaterDeclarers</c> 逐个钉住）。
/// </para>
/// </summary>
public enum SkillKeywordDisputeReason
{
    /// <summary>
    /// 冲突关键词形如工具名（如 <c>file_read</c>）。
    /// <para>
    /// 判定**必须**来自调用方注入的**同一份**谓词来源（G7-C3 用的那份，
    /// 现场接线见 <c>SubconsciousOrchestrator</c> 的 <c>IsToolLikeKeyword = SkillEvolutionDeduplicationService.IsToolLikeKeyword</c>）——
    /// 本类**不得**自带第二份工具名判定（任务书 §2.4-3 / §6③）。
    /// </para>
    /// </summary>
    SharedByToolName = 0,

    /// <summary>冲突关键词不是工具名 ⇒ 内容类冲突（可能是真重复，也可能是真噪声，须人裁决）。</summary>
    SharedByContent = 1,
}

/// <summary>
/// 待裁决项**可以**被裁成的几种结果（任务书 §2.4-2 原文："改写关键词 / 提升优先级 / 拒绝"）。
/// <para>
/// ⚠️ 本枚举是**候选空间**，不是判据的选择结果：判据把这些选项**原样列出**，由人 / L3-b 选。
/// ⛔ 因此本类中不存在"默认选项""已选选项"字段，也不存在"建议选项"。
/// </para>
/// </summary>
public enum KeywordDispositionOption
{
    /// <summary>改写关键词：让该技能的这条声明换一个不碰撞的词。</summary>
    RewriteKeyword = 0,

    /// <summary>提升优先级：显式抬升某一方的注入优先权（而不是靠索引遍历顺序）。</summary>
    RaisePriority = 1,

    /// <summary>拒绝声明：明确拒绝某一方对该关键词的声明（必须**指名**拒谁，⛔ 不接受"一律拒绝后来者"）。</summary>
    RejectDeclaration = 2,
}

/// <summary>
/// "本判据**不裁决**"的原因（任务书 §2.6：冷启动 / 无数据 ⇒ 降级为"不裁决"并产出 reason code，
/// ⛔ 不得退化成"低价值"）。
/// </summary>
public enum SkillKeywordDisputeAbstentionReason
{
    /// <summary>没有启用技能（冷启动 / 门控给错目录）⇒ 无事实可裁决。</summary>
    NoEnabledSkills = 0,

    /// <summary>有启用技能但关键词空间为空 ⇒ 无事实可裁决。</summary>
    NoKeywordsEvaluated = 1,

    /// <summary>
    /// 关键词空间非空，但没有任何关键词被 ≥2 个启用技能声明 ⇒ **没有冲突项**。
    /// <para>
    /// ⚠️ 这不等于"归属是干净的"：它只说明**本判据没有可裁决项**（可能是真干净，也可能是关键词归一
    /// 把差异磨平了、或声明面太窄）。⛔ 不得把它读成"归属已清"这类结论。
    /// </para>
    /// </summary>
    NoSharedKeywords = 2,
}

/// <summary>
/// 一条**待裁决**的关键词归属冲突（纯事实，⛔ 不含任何执行语义）。
/// </summary>
public sealed record SkillKeywordDispute
{
    /// <summary>冲突关键词（小写归一形式，由 D4 探针给出）。</summary>
    public required string Keyword { get; init; }

    /// <summary>
    /// **全部**竞争者（声明该关键词的启用技能 id，序数升序）。
    /// <para>
    /// ⛔ 刻意**不**标出"胜者 / 实际命中者"：真实命中者只有运行时索引构造顺序知道，
    /// 把近似值写成事实字段正是本会话反复踩过的坑。也不存在"第一个 = 先到者"的说法。
    /// </para>
    /// </summary>
    public required IReadOnlyList<string> Candidates { get; init; }

    /// <summary>该关键词上被挤掉的注入机会数（= 竞争人数 − 1；决定这条待裁决的**代价**）。</summary>
    public required int DisplacedCount { get; init; }

    /// <summary>冲突性质（工具名 / 内容）。</summary>
    public required SkillKeywordDisputeReason ReasonCode { get; init; }

    /// <summary>**候选**处置方式（与 <see cref="KeywordDispositionOption"/> 全量一致，顺序固定）。</summary>
    public required IReadOnlyList<KeywordDispositionOption> CandidateDispositions { get; init; }

    public bool Equals(SkillKeywordDispute? other)
        => other is not null
            && string.Equals(Keyword, other.Keyword, StringComparison.Ordinal)
            && DisplacedCount == other.DisplacedCount
            && ReasonCode == other.ReasonCode
            && Candidates.SequenceEqual(other.Candidates, StringComparer.Ordinal)
            && CandidateDispositions.SequenceEqual(other.CandidateDispositions);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Keyword, StringComparer.Ordinal);
        hash.Add(DisplacedCount);
        hash.Add(ReasonCode);
        foreach (var candidate in Candidates)
            hash.Add(candidate, StringComparer.Ordinal);
        foreach (var option in CandidateDispositions)
            hash.Add(option);
        return hash.ToHashCode();
    }
}

/// <summary>为什么本判据**没有**产出任何待裁决项（见 <see cref="SkillKeywordDisputeAbstentionReason"/>）。</summary>
public sealed record SkillKeywordDisputeAbstention
{
    public required SkillKeywordDisputeAbstentionReason ReasonCode { get; init; }
}

/// <summary>
/// 关键词归属**待裁决**报告（任务书 §4-D5：冲突 ⇒ 待裁决 + reason code）。
/// </summary>
/// <remarks>
/// ⛔ 不含 <c>policyId@version</c>：§2.3 的该要求只针对**家族超限记录**，而本判据**没有任何阈值**
/// （冲突条件是结构性的"声明者 ≥2"，不是可调参数）⇒ 没有可版本化的策略；也正因为无阈值，
/// I4「判定器内不得出现裸阈值字面量」对本判据天然成立（其载体在 D3 / D6）。
/// <para>
/// 总量**由明细推导**（<see cref="DisputedKeywordCount"/> / <see cref="DisputedDisplacedInjectionCount"/>
/// 都不另算一遍）⇒ 不会与 <see cref="Disputes"/> 分叉。
/// </para>
/// </remarks>
public sealed record SkillKeywordDisputeReport
{
    /// <summary>被评估的关键词数（= 归属事实里的关键词总数；分母不能省）。</summary>
    public required int EvaluatedKeywordCount { get; init; }

    /// <summary>存在冲突的关键词数（= <see cref="Disputes"/> 条数）。</summary>
    public required int DisputedKeywordCount { get; init; }

    /// <summary>这些冲突关键词上被挤掉的注入机会总数（严格来自明细求和）。</summary>
    public required int DisputedDisplacedInjectionCount { get; init; }

    /// <summary>待裁决项，按关键词序数升序（确定性）。</summary>
    public required IReadOnlyList<SkillKeywordDispute> Disputes { get; init; }

    /// <summary>
    /// 非 <c>null</c> ⇔ <see cref="Disputes"/> 为空 —— 即"没有产出任何可裁决项"时**必须**给出原因。
    /// <para>
    /// 这是任务书 §2.6 的载体：0 条待裁决有两种完全不同的含义（"确实没有冲突" vs "没数据可判"），
    /// 把二者压成同一个 0 就是制造一个无人会失败的幻影区间（同 D3 的 <c>Cap</c> 字段）。
    /// </para>
    /// </summary>
    public SkillKeywordDisputeAbstention? Abstention { get; init; }

    public bool Equals(SkillKeywordDisputeReport? other)
        => other is not null
            && EvaluatedKeywordCount == other.EvaluatedKeywordCount
            && DisputedKeywordCount == other.DisputedKeywordCount
            && DisputedDisplacedInjectionCount == other.DisputedDisplacedInjectionCount
            && Equals(Abstention, other.Abstention)
            && Disputes.SequenceEqual(other.Disputes);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(EvaluatedKeywordCount);
        hash.Add(DisputedKeywordCount);
        hash.Add(DisputedDisplacedInjectionCount);
        hash.Add(Abstention);
        foreach (var dispute in Disputes)
            hash.Add(dispute);
        return hash.ToHashCode();
    }
}

/// <summary>
/// RSI-G4 交付物 **D5**：关键词归属**裁决**判据（任务书 §2.4-2 / §4-D5 / 不变式 I5）。
/// <para>
/// 输入 = D4 的**归属事实**（<see cref="SkillKeywordOwnershipReport"/>：每个关键词的**全部**声明者）。
/// 输出 = **待裁决记录**（冲突关键词 + 全部竞争者 + 代价 + reason code + 候选处置方式）。
/// </para>
/// <para>
/// <b>它刻意不做的事（每一条都是任务书明令）</b>：
/// <list type="bullet">
/// <item>⛔ 不产出"胜者 / 实际命中者 / 该禁谁"——冲突一律是**待裁决**；</item>
/// <item>⛔ 不使用"先到先得"作为默认裁决（记录里没有任何顺序派生字段）；</item>
/// <item>⛔ 不把"后来者一律拒绝"写成默认（候选处置方式三项并列，无默认项）；</item>
/// <item>⛔ 零写盘、零执行面：不禁用、不改关键词、不 Displace、不合并（删除 / 改名 / 归属改写属 **L3-b**）；</item>
/// <item>⛔ 不改 <c>SkillEnforcerService</c> 的 map 构建结果（本判据**没有任何生产调用点**，
/// 注入结果在结构上不可能被它影响）；</item>
/// <item>⛔ 不按"噪声 / 语义"筛掉任何一条冲突——D1/D2/D3 去噪属 **G5**（须在 G2 遥测之后）。</item>
/// </list>
/// </para>
/// <para>
/// <b>与 G7-C3 的分层</b>：C3 是**新技能准入**时的碰撞检测（产出 <c>ChangeVerdict</c>）；
/// 本判据是**存量已启用技能之间**的归属裁决（产出待裁决事实）。
/// 二者共享同一份关键词归一（D4）与**同一个工具名谓词**（由调用方注入，见构造器），
/// ⛔ 不得各写一份（任务书 §2.4-3）。
/// </para>
/// <para>
/// <b>零归一纪律</b>：本判据**不做任何关键词归一** —— 事实里的关键词已由 D4 探针归一（小写）后传入；
/// 若收到未归一的关键词，本判据**抛出**而不是就地再归一次（避免出现第二份归一逻辑，
/// 也避免"两次归一结果不同"这类静默分叉）。
/// </para>
/// <para>
/// <b>刻意没有的校验（防"假有牙"）</b>：<see cref="SkillKeywordOwnership"/> 的
/// <c>DeclaredCount</c> / <c>DisplacedCount</c> / <c>IsShared</c> 都是**派生属性**
/// （分别由 <c>DeclaringSkillIds</c> 计算），因此"检查它们与明细一致"这类校验在类型上**永远不可达** ——
/// 写了也没有任何用例能取红，只是把"看起来有防线"当成有防线。
/// 本判据只保留**可达**的 fail-closed 校验：非空校验、重复关键词、重复声明者、空白/未归一关键词、
/// 以及报告级数字与明细的一致性。
/// </para>
/// </summary>
public sealed class SkillKeywordOwnershipJudge
{
    private static readonly IReadOnlyList<KeywordDispositionOption> DispositionVocabulary =
        Array.AsReadOnly<KeywordDispositionOption>(
        [
            KeywordDispositionOption.RewriteKeyword,
            KeywordDispositionOption.RaisePriority,
            KeywordDispositionOption.RejectDeclaration,
        ]);

    private readonly Func<string, bool> _isToolLikeKeyword;

    /// <summary>
    /// 构造判据。
    /// </summary>
    /// <param name="isToolLikeKeyword">
    /// **工具名谓词来源**（任务书 §2.4-3 要求的"同一个来源"）：生产侧接线必须传
    /// <c>SkillEvolutionDeduplicationService.IsToolLikeKeyword</c>（G7-C3 用的是同一个；
    /// 现场接线见 <c>SubconsciousOrchestrator</c>）。
    /// <para>
    /// ⛔ 注入而非内建：本类**不得**自带第二份工具名判定（§6③）。同时它必须**不可为空** ——
    /// 缺失谓词时抛出，⛔ 绝不允许静默退化成"都不是工具名"（那会把一个缺失的判据伪装成结论）。
    /// </para>
    /// </param>
    public SkillKeywordOwnershipJudge(Func<string, bool> isToolLikeKeyword)
        => _isToolLikeKeyword = isToolLikeKeyword ?? throw new ArgumentNullException(nameof(isToolLikeKeyword));

    /// <summary>
    /// 把归属事实裁成"待裁决"清单。
    /// </summary>
    public SkillKeywordDisputeReport Apply(SkillKeywordOwnershipReport facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(facts.Ownerships);

        if (facts.DistinctKeywordCount != facts.Ownerships.Count)
        {
            throw new InvalidOperationException(
                "归属事实自相矛盾：DistinctKeywordCount " + facts.DistinctKeywordCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " ≠ 明细条数 " + facts.Ownerships.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "。⛔ 本判据拒绝在自相矛盾的事实上继续（不得用跳过掩盖）。");
        }

        var sharedFromDetails = 0;
        var disputes = new List<SkillKeywordDispute>();
        var seenKeywords = new HashSet<string>(StringComparer.Ordinal);

        foreach (var ownership in facts.Ownerships)
        {
            ArgumentNullException.ThrowIfNull(ownership);

            if (!seenKeywords.Add(ownership.Keyword))
            {
                throw new InvalidOperationException(
                    "归属事实里出现重复关键词：" + ownership.Keyword + "（同一关键词只能有一条归属事实）。");
            }

            if (string.IsNullOrWhiteSpace(ownership.Keyword))
            {
                throw new InvalidOperationException(
                    "归属事实里出现空白关键词：空白关键词不产生注入机会，D4 探针不会产出它 ⇒ 事实已损坏。");
            }

            if (ownership.Keyword != ownership.Keyword.ToLowerInvariant())
            {
                throw new InvalidOperationException(
                    "关键词未归一（应为小写）：" + ownership.Keyword
                    + "。事实必须由 D4 探针归一后传入——本判据不做第二次归一（§2.4-3 只有一份归一来源）。");
            }

            ArgumentNullException.ThrowIfNull(ownership.DeclaringSkillIds);

            if (!ownership.IsShared)
            {
                continue;
            }

            sharedFromDetails++;

            // 竞争者完整 + 稳定排序：与 D4 的排序无关（本判据不假设输入的声明者已排序，
            // 否则"删掉本判据的排序"这类变异会被上游排序完全掩盖）。
            var candidates = ownership.DeclaringSkillIds
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray();

            if (candidates.Distinct(StringComparer.Ordinal).Count() != candidates.Length)
            {
                throw new InvalidOperationException(
                    "归属事实自相矛盾：关键词 " + ownership.Keyword + " 的声明者清单出现重复项。");
            }

            disputes.Add(new SkillKeywordDispute
            {
                Keyword = ownership.Keyword,
                Candidates = candidates,
                DisplacedCount = ownership.DisplacedCount,
                ReasonCode = _isToolLikeKeyword(ownership.Keyword)
                    ? SkillKeywordDisputeReason.SharedByToolName
                    : SkillKeywordDisputeReason.SharedByContent,
                CandidateDispositions = DispositionVocabulary,
            });
        }

        if (facts.SharedKeywordCount != sharedFromDetails)
        {
            throw new InvalidOperationException(
                "归属事实自相矛盾：SharedKeywordCount "
                + facts.SharedKeywordCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " ≠ 明细中共享关键词条数 "
                + sharedFromDetails.ToString(System.Globalization.CultureInfo.InvariantCulture) + "。");
        }

        disputes.Sort(static (left, right) => string.CompareOrdinal(left.Keyword, right.Keyword));

        var disputesArray = disputes.ToArray();

        return new SkillKeywordDisputeReport
        {
            EvaluatedKeywordCount = facts.Ownerships.Count,
            DisputedKeywordCount = disputesArray.Length,
            DisputedDisplacedInjectionCount = disputesArray.Sum(static dispute => dispute.DisplacedCount),
            Disputes = disputesArray,
            Abstention = disputesArray.Length > 0 ? null : ExplainAbstention(facts),
        };
    }

    /// <summary>
    /// 没有产出待裁决项时**必须**给出原因（§2.6）；有产出时返回 <c>null</c>（产物本身就是"待裁决"，
    /// 不存在"没裁决"这件事要解释）。
    /// </summary>
    private static SkillKeywordDisputeAbstention ExplainAbstention(SkillKeywordOwnershipReport facts)
    {
        var reason = facts.EnabledSkillCount <= 0
            ? SkillKeywordDisputeAbstentionReason.NoEnabledSkills
            : facts.Ownerships.Count == 0
                ? SkillKeywordDisputeAbstentionReason.NoKeywordsEvaluated
                : SkillKeywordDisputeAbstentionReason.NoSharedKeywords;

        return new SkillKeywordDisputeAbstention { ReasonCode = reason };
    }
}
