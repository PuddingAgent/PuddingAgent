using System.Reflection;
using PuddingMemoryEngine.Services;

namespace PuddingMemoryEngineTests;

/// <summary>
/// RSI-G4 交付物 **D5**：关键词归属**裁决**判据契约用例。
/// <para>
/// 权威出处：任务书 §4-D5 行、§2.4-2（冲突 ⇒ 待裁决 + reason code；⛔ 默认不得是"先到先得"、
/// ⛔ 也不得是"后来者一律拒绝"）、§2.6（冷启动/无数据 ⇒ 不裁决 + reason code）、
/// §2.4-3（与 G7-C3 共享同一个工具名谓词来源）、不变式 **I5**。
/// </para>
/// <para>
/// 纪律：**只写用例不证明它会红 ⇒ 不算完成**。本文件每条断言都注明"什么变异会让它变红"，
/// 并在提交信息里给出逐条变异的前后对照。
/// </para>
/// </summary>
[TestClass]
public sealed class SkillKeywordOwnershipJudgeTests
{
    // ───────────── I5① 冲突必须保留全部竞争者（"取第一个"＝先到先得，必红载体） ─────────────

    [TestMethod]
    public void Apply_ShouldKeepEveryCompetitor_NotJustTheFirstOne()
    {
        var report = Judge().Apply(Facts(
            ("skill-a", ["shared"]),
            ("skill-b", ["shared"]),
            ("skill-c", ["shared"])));

        var dispute = report.Disputes.Single();

        Assert.AreEqual("shared", dispute.Keyword);
        Assert.HasCount(
            3,
            dispute.Candidates,
            "把冲突判成『取第一个』（先到先得）时本用例必红：竞争者被截成 1 个。");
        CollectionAssert.AreEqual(new[] { "skill-a", "skill-b", "skill-c" }, dispute.Candidates.ToList());
        Assert.AreEqual(2, dispute.DisplacedCount, "被挤掉的注入机会 = 竞争者数 − 1（决定这条待裁决的代价）。");
    }

    [TestMethod]
    public void Apply_ShouldEmitOnePendingItemPerSharedKeyword()
    {
        var report = Judge().Apply(Facts(
            ("skill-a", ["alpha-shared", "only-a"]),
            ("skill-b", ["alpha-shared", "beta-shared"]),
            ("skill-c", ["beta-shared"])));

        Assert.HasCount(2, report.Disputes, "每个共享关键词各一条待裁决。");
        Assert.AreEqual("alpha-shared", report.Disputes[0].Keyword, "待裁决按关键词序数升序（确定性）。");
        Assert.AreEqual("beta-shared", report.Disputes[1].Keyword);
        CollectionAssert.AreEqual(new[] { "skill-a", "skill-b" }, report.Disputes[0].Candidates.ToList());
        CollectionAssert.AreEqual(new[] { "skill-b", "skill-c" }, report.Disputes[1].Candidates.ToList());
    }

    [TestMethod]
    public void Apply_ShouldNotTruncateThePendingList()
    {
        var report = Judge().Apply(Facts(
            ("skill-a", ["small-shared"]),
            ("skill-b", ["small-shared", "big-shared"]),
            ("skill-c", ["big-shared"]),
            ("skill-d", ["big-shared"]),
            ("skill-e", ["big-shared"])));

        Assert.HasCount(
            2,
            report.Disputes,
            "⛔ 不得按 Top-N / .Take(...) 截断待裁决清单 —— 那会把静默丢项伪装成『冲突只有这些』。");
        Assert.AreEqual(1, report.Disputes.Single(dispute => dispute.Keyword == "small-shared").DisplacedCount);
        Assert.AreEqual(3, report.Disputes.Single(dispute => dispute.Keyword == "big-shared").DisplacedCount);
    }

    // ───────────── 两种被明令禁止的默认裁决：都没有载体 ─────────────

    [TestMethod]
    public void Apply_ShouldNotDefaultToRejectingLaterDeclarers()
    {
        var report = Judge().Apply(Facts(("skill-a", ["shared"]), ("skill-b", ["shared"])));
        var dispute = report.Disputes.Single();

        CollectionAssert.AreEqual(
            new[]
            {
                KeywordDispositionOption.RewriteKeyword,
                KeywordDispositionOption.RaisePriority,
                KeywordDispositionOption.RejectDeclaration,
            },
            dispute.CandidateDispositions.ToList(),
            "§2.4-2：三种可能处置必须**并列**给出，⛔ 不得有默认项 / 建议项 / 已选项。");

        // 「先到先得」与「后来者一律拒绝」若要被写成**默认**，必然需要一个枚举成员或字段来承载它。
        // 逐个钉住成员集合：多一个裁决性成员就红。
        CollectionAssert.AreEqual(
            new[] { SkillKeywordDisputeReason.SharedByToolName, SkillKeywordDisputeReason.SharedByContent },
            Enum.GetValues<SkillKeywordDisputeReason>(),
            "reason code 只描述冲突**性质**，⛔ 不得出现『先到先得 / 后来者拒绝』这类裁决性成员。");
        CollectionAssert.AreEqual(
            new[]
            {
                KeywordDispositionOption.RewriteKeyword,
                KeywordDispositionOption.RaisePriority,
                KeywordDispositionOption.RejectDeclaration,
            },
            Enum.GetValues<KeywordDispositionOption>(),
            "处置方式就是 §2.4-2 原文那三种，⛔ 不得增补『默认拒绝后来者』之类的成员。");
        CollectionAssert.AreEqual(
            new[]
            {
                SkillKeywordDisputeAbstentionReason.NoEnabledSkills,
                SkillKeywordDisputeAbstentionReason.NoKeywordsEvaluated,
                SkillKeywordDisputeAbstentionReason.NoSharedKeywords,
            },
            Enum.GetValues<SkillKeywordDisputeAbstentionReason>(),
            "『不裁决』的原因必须逐个显式，⛔ 不得退化成『低价值』之类的单一兜底原因。");
    }

    // ───────────── §2.4-3 / §6③：工具名谓词只有一份来源（注入的那份） ─────────────

    [TestMethod]
    public void Apply_ShouldClassifyViaTheInjectedPredicate_NotViaAPrivateCopy()
    {
        // 注入一个"只认 weird-marker"的谓词：判据必须**照它**分类。
        var report = Judge(MarkerOnlyPredicate).Apply(Facts(
            ("skill-a", ["weird-marker", "file_read", "shared-word"]),
            ("skill-b", ["weird-marker", "file_read", "shared-word"])));

        var reasons = report.Disputes.ToDictionary(dispute => dispute.Keyword, dispute => dispute.ReasonCode, StringComparer.Ordinal);

        Assert.AreEqual(SkillKeywordDisputeReason.SharedByToolName, reasons["weird-marker"]);
        Assert.AreEqual(
            SkillKeywordDisputeReason.SharedByContent,
            reasons["file_read"],
            "注入的谓词**没有**说 file_read 是工具名 ⇒ 只能是内容类。若判据内部自写了一份工具名判定（无视注入来源），这里会是 ToolName ⇒ 必红（§2.4-3 / §6③）。");
        Assert.AreEqual(SkillKeywordDisputeReason.SharedByContent, reasons["shared-word"]);
    }

    [TestMethod]
    public void Apply_WithTheRealToolNamePredicate_ShouldAgreeWithItForEveryDispute()
    {
        // 生产口径：G7-C3 与 G4 用的是**同一个**谓词（现场接线见 SubconsciousOrchestrator 的
        // IsToolLikeKeyword = SkillEvolutionDeduplicationService.IsToolLikeKeyword）。
        var report = Judge().Apply(Facts(
            ("skill-a", ["file_read", "git_commit", "self-evolution", "shared-semantic-word"]),
            ("skill-b", ["file_read", "git_commit", "self-evolution", "shared-semantic-word"])));

        Assert.HasCount(4, report.Disputes);

        foreach (var dispute in report.Disputes)
        {
            var expected = SkillEvolutionDeduplicationService.IsToolLikeKeyword(dispute.Keyword)
                ? SkillKeywordDisputeReason.SharedByToolName
                : SkillKeywordDisputeReason.SharedByContent;
            Assert.AreEqual(
                expected,
                dispute.ReasonCode,
                $"关键词 {dispute.Keyword} 的分类必须与唯一来源谓词逐字一致（判据不得自带第二份判定）。");
        }

        // 分类必须真的能区分（否则"分类"是个常量，等于没有分类）。
        Assert.IsGreaterThan(
            0,
            report.Disputes.Count(dispute => dispute.ReasonCode == SkillKeywordDisputeReason.SharedByToolName),
            "至少应有一条工具名类冲突，否则说明谓词没被真正用上。");
        Assert.IsGreaterThan(
            0,
            report.Disputes.Count(dispute => dispute.ReasonCode == SkillKeywordDisputeReason.SharedByContent),
            "至少应有一条内容类冲突。");
    }

    // ───────────── 确定性：与输入顺序无关，且不依赖上游排序 ─────────────

    [TestMethod]
    public void Apply_ShouldBeOrderInsensitiveAndRepeatable()
    {
        var forward = Judge().Apply(Facts(("skill-a", ["shared"]), ("skill-b", ["shared"])));
        var backward = Judge().Apply(Facts(("skill-b", ["shared"]), ("skill-a", ["shared"])));

        Assert.AreEqual<SkillKeywordDisputeReport>(
            forward,
            backward,
            "同一事实换输入顺序必须得到同一结果（比较的是 record 内容相等——若相等性退化成引用相等，本断言会**静默失效**，所以这里断言整体相等）。");

        // 手工构造"声明者清单未排序"的事实：判据必须自己排序。
        // ⚠️ 变异取红点：删掉判据内的 OrderBy ⇒ 本断言必红；且不与上游 D4 的排序互相掩盖
        //（上游排序对**手工构造**的事实不存在）。
        var unsorted = Report([("shared", ["skill-c", "skill-a", "skill-b"])]);
        var sorted = Report([("shared", ["skill-a", "skill-b", "skill-c"])]);

        Assert.AreEqual<SkillKeywordDisputeReport>(Judge().Apply(sorted), Judge().Apply(unsorted));
        Assert.AreEqual<SkillKeywordDisputeReport>(Judge().Apply(sorted), Judge().Apply(sorted), "两次同输入必须逐字段相同。");
    }

    // ───────────── 总量由明细推导（不与明细分叉） ─────────────

    [TestMethod]
    public void Report_ShouldDeriveTotalsFromTheDetails()
    {
        var facts = Facts(
            ("skill-a", ["shared-one"]),
            ("skill-b", ["shared-one", "shared-two"]),
            ("skill-c", ["shared-two"]),
            ("skill-d", ["shared-two"]));

        var report = Judge().Apply(facts);

        Assert.AreEqual(facts.Ownerships.Count, report.EvaluatedKeywordCount, "分母 = 归属事实条数。");
        Assert.AreEqual(report.Disputes.Count, report.DisputedKeywordCount);
        Assert.AreEqual(report.Disputes.Sum(dispute => dispute.DisplacedCount), report.DisputedDisplacedInjectionCount);
        Assert.AreEqual(facts.SharedKeywordCount, report.DisputedKeywordCount, "共享关键词一条不漏、也不得多。");
        Assert.IsNull(report.Abstention, "有产出待裁决项 ⇒ 不存在『没裁决』这件事要解释。");
    }

    // ───────────── §2.6：0 条待裁决必须给出原因（不得压成一个裸 0） ─────────────

    [TestMethod]
    public void Apply_ShouldExplainWhyNothingIsPending_InsteadOfReturningABareZero()
    {
        var noSkills = Judge().Apply(SkillKeywordOwnershipProbe.Analyze([]));
        var noShared = Judge().Apply(Facts(("skill-a", ["only-a"]), ("skill-b", ["only-b"])));
        var disputed = Judge().Apply(Facts(("skill-a", ["shared"]), ("skill-b", ["shared"])));

        Assert.IsEmpty(noSkills.Disputes);
        Assert.IsNotNull(noSkills.Abstention, "0 条待裁决必须给出原因，否则『没数据可判』与『没有冲突』被压成同一个 0。");
        Assert.AreEqual(SkillKeywordDisputeAbstentionReason.NoEnabledSkills, noSkills.Abstention!.ReasonCode);

        Assert.IsEmpty(noShared.Disputes);
        Assert.IsNotNull(noShared.Abstention);
        Assert.AreEqual(
            SkillKeywordDisputeAbstentionReason.NoSharedKeywords,
            noShared.Abstention!.ReasonCode,
            "⛔ 不得把『无共享关键词』读成『归属已清』这类结论。");
        Assert.IsGreaterThan(
            0,
            noShared.EvaluatedKeywordCount,
            "关键词空间非空 ⇒ 走的是 NoSharedKeywords 而不是 NoKeywordsEvaluated。");

        Assert.IsNull(disputed.Abstention);
        Assert.IsGreaterThan(0, disputed.DisputedKeywordCount);
    }

    // ───────────── 失败即抛：不用跳过 / 默认值掩盖损坏的输入 ─────────────

    [TestMethod]
    public void Apply_ShouldRejectNullInputsAndMissingPredicate()
    {
        var judge = Judge();

        Assert.ThrowsExactly<ArgumentNullException>(
            () => new SkillKeywordOwnershipJudge(null!),
            "缺失工具名谓词必须抛出，⛔ 不得静默退化成『都不是工具名』（那会把缺失的判据伪装成结论）。");
        Assert.ThrowsExactly<ArgumentNullException>(() => judge.Apply(null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => judge.Apply(new SkillKeywordOwnershipReport
        {
            EnabledSkillCount = 1,
            SlotCount = 0,
            DistinctKeywordCount = 0,
            SharedKeywordCount = 0,
            DisplacedInjectionCount = 0,
            Ownerships = null!,
        }));
    }

    [TestMethod]
    public void Apply_ShouldRejectContradictoryFacts_InsteadOfSilentlySkipping()
    {
        var judge = Judge();

        // ① 报告级数字与明细不一致（总量与明细分叉）。
        Assert.ThrowsExactly<InvalidOperationException>(
            () => judge.Apply(Report([("shared", ["skill-a", "skill-b"])], distinctKeywordCount: 5)));
        Assert.ThrowsExactly<InvalidOperationException>(
            () => judge.Apply(Report([("shared", ["skill-a", "skill-b"])], sharedKeywordCount: 0)));

        // ② 同一关键词出现两条事实。
        Assert.ThrowsExactly<InvalidOperationException>(
            () => judge.Apply(Report([("shared", ["skill-a", "skill-b"]), ("shared", ["skill-c"])])));

        // ③ 空白关键词 / 未归一关键词（本判据不做第二次归一，也不静默接受）。
        Assert.ThrowsExactly<InvalidOperationException>(
            () => judge.Apply(Report([("   ", ["skill-a", "skill-b"])])));
        Assert.ThrowsExactly<InvalidOperationException>(
            () => judge.Apply(Report([("Shared", ["skill-a", "skill-b"])])));

        // ④ 同一声明者清单里同一个技能出现两次。
        Assert.ThrowsExactly<InvalidOperationException>(
            () => judge.Apply(Report([("shared", ["skill-a", "skill-a", "skill-b"])])));
    }

    // ───────────── 结构闸门：零执行面（I3/I7 同源纪律在 D5 的对应物） ─────────────

    [TestMethod]
    public void Judge_ShouldExposeNoWriteOrExecutableSurface()
    {
        // ① 唯一公开入口。
        var publicMethods = typeof(SkillKeywordOwnershipJudge)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => !method.IsSpecialName)
            .Select(method => method.Name)
            .ToArray();

        CollectionAssert.AreEqual(
            new[] { "Apply" },
            publicMethods,
            "本判据只能有一个公开入口：⛔ 不得有 TryApply / Preview / Execute 之类的第二入口。");

        // ② 判据**不持有任何可写句柄**：字段只有"注入的工具名谓词"与"只读候选处置词表"两项。
        // 这是比"名字里没有 write"强得多的证据 —— 没有仓储 / 文件 / 缓存句柄，就不可能有写盘路径。
        var fieldTypes = typeof(SkillKeywordOwnershipJudge)
            .GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .Select(field => field.FieldType.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        CollectionAssert.AreEqual(
            new[] { "Func`2", "IReadOnlyList`1" },
            fieldTypes,
            "判据只能是纯函数：除注入的工具名谓词与只读候选处置词表外，⛔ 不得持有文件 / 仓储 / 缓存等可写句柄。");

        // ③ 记录字段逐个钉住：待裁决事实不得携带执行语义 / 胜者语义。
        CollectionAssert.AreEqual(
            new[] { "CandidateDispositions", "Candidates", "DisplacedCount", "Keyword", "ReasonCode" },
            typeof(SkillKeywordDispute).GetProperties()
                .Select(property => property.Name)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList(),
            "待裁决记录必须只是事实：⛔ 无 winner / selected / preferred，⛔ 无 disable / delete / merge 语义。");

        CollectionAssert.AreEqual(
            new[] { "Abstention", "DisputedDisplacedInjectionCount", "DisputedKeywordCount", "Disputes", "EvaluatedKeywordCount" },
            typeof(SkillKeywordDisputeReport).GetProperties()
                .Select(property => property.Name)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList());

        CollectionAssert.AreEqual(
            new[] { "ReasonCode" },
            typeof(SkillKeywordDisputeAbstention).GetProperties()
                .Select(property => property.Name)
                .ToList());

        // ④ 成员名不得出现顺序 / 胜者类词汇（那正是两种被禁默认裁决的载体），也不得出现写侧动词。
        // ⚠️ "write" **刻意不在**禁用词里：§2.4-2 明令的候选处置 RewriteKeyword 本身含 "write"，
        //    放进去只会得到一个假阳性（本条已实测踩过一次）。写盘风险由 ② 的字段类型钉住。
        string[] forbidden =
        [
            "disable", "delete", "merge", "persist", "save", "enforce",
            "winner", "selected", "preferred", "arrival", "first", "latest", "newest",
        ];
        Type[] guardedTypes =
        [
            typeof(SkillKeywordOwnershipJudge),
            typeof(SkillKeywordDispute),
            typeof(SkillKeywordDisputeReport),
            typeof(SkillKeywordDisputeAbstention),
            typeof(SkillKeywordDisputeReason),
            typeof(KeywordDispositionOption),
            typeof(SkillKeywordDisputeAbstentionReason),
        ];

        foreach (var type in guardedTypes)
        {
            var memberNames = type
                .GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(IsNotAccessor)
                .Select(member => member.Name)
                .ToArray();

            foreach (var forbiddenToken in forbidden)
            {
                Assert.IsEmpty(
                    memberNames.Where(name => name.Contains(forbiddenToken, StringComparison.OrdinalIgnoreCase)).ToArray(),
                    $"{type.Name} 的成员名里出现『{forbiddenToken}』：待裁决事实不得携带执行 / 胜者 / 顺序语义。");
            }
        }

        // ⑤ 候选处置方式是**只读**共享实例：⛔ 不得让消费者改到其他记录（可变共享状态）。
        var dispute = Judge().Apply(Facts(("skill-a", ["shared"]), ("skill-b", ["shared"]))).Disputes.Single();
        Assert.ThrowsExactly<NotSupportedException>(
            () => ((IList<KeywordDispositionOption>)dispute.CandidateDispositions).Add(KeywordDispositionOption.RewriteKeyword));
    }

    // ───────────────────────── helpers ─────────────────────────

    private static bool IsNotAccessor(MemberInfo member) => member switch
    {
        MethodBase method => !method.IsSpecialName,
        PropertyInfo property => !property.IsSpecialName,
        FieldInfo field => !field.IsSpecialName,
        _ => true,
    };

    private static SkillKeywordOwnershipJudge Judge(Func<string, bool>? isToolLikeKeyword = null)
        => new(isToolLikeKeyword ?? SkillEvolutionDeduplicationService.IsToolLikeKeyword);

    /// <summary>只认一个标记词的谓词：用于证明判据**照注入来源**分类，而不是自带一份工具名判定。</summary>
    private static bool MarkerOnlyPredicate(string keyword)
        => string.Equals(keyword, "weird-marker", StringComparison.Ordinal);

    private static SkillKeywordOwnershipReport Facts(params (string SkillId, string[] Keywords)[] skills)
        => SkillKeywordOwnershipProbe.Analyze(skills.Select(static skill => new SkillKeywordSubject
        {
            SkillId = skill.SkillId,
            Keywords = skill.Keywords,
            Enabled = true,
        }).ToList());

    private static SkillKeywordOwnershipReport Report(
        (string Keyword, string[] DeclaringSkillIds)[] ownerships,
        int? distinctKeywordCount = null,
        int? sharedKeywordCount = null)
    {
        var details = ownerships
            .Select(ownership => new SkillKeywordOwnership
            {
                Keyword = ownership.Keyword,
                DeclaringSkillIds = ownership.DeclaringSkillIds,
            })
            .ToArray();

        return new SkillKeywordOwnershipReport
        {
            EnabledSkillCount = details.SelectMany(detail => detail.DeclaringSkillIds).Distinct(StringComparer.Ordinal).Count(),
            SlotCount = details.Sum(detail => detail.DeclaredCount),
            DistinctKeywordCount = distinctKeywordCount ?? details.Length,
            SharedKeywordCount = sharedKeywordCount ?? details.Count(detail => detail.IsShared),
            DisplacedInjectionCount = details.Sum(detail => detail.DisplacedCount),
            Ownerships = details,
        };
    }
}
