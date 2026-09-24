using System.Reflection;
using PuddingCodeIndex.Contracts.Retrieval;

namespace PuddingCodeIndexTests.Contracts.Retrieval;

/// <summary>
/// 契约测试 A2 / A3 / A4 / A9：把"空洞否定"变成**不可表示**（ADR-089 §2.1 第 3/7 条、§2.2、§8.6/§8.7）。
/// </summary>
[TestClass]
public sealed class RetrievalResultContractTests
{
    /// <summary>A2：任何"hits=0 但无 reason"的构造路径都不存在。</summary>
    [TestMethod]
    public void A02_No_Empty_Result_Without_Reason_Is_Representable()
    {
        var request = RetrievalTestData.Request();

        // ① 无公开构造函数：只能经工厂产出（反射证明）
        Assert.IsEmpty(
            typeof(RetrievalResult).GetConstructors(BindingFlags.Public | BindingFlags.Instance),
            "RetrievalResult 不得有公开构造函数（否则可绕过全部不变量）");

        // ② 通用工厂：totalCount = 0 却不给 reason ⇒ 拒绝
        Assert.ThrowsExactly<RetrievalContractViolationException>(
            () => RetrievalResult.Create(request, [], 0, RetrievalTestData.IndexVersion),
            "0 命中且无原因的结果不得被构造出来");

        // ③ 空页 + 有总量的说法（既非真空、又没落盘）⇒ 同样拒绝
        Assert.ThrowsExactly<RetrievalContractViolationException>(
            () => RetrievalResult.Create(request, [], 5, RetrievalTestData.IndexVersion, nextSteps: [RetrievalTestData.NarrowStep()]));

        // ④ 结构证明：名字含 Empty 的工厂必须声明 RetrievalEmptyReason 参数（reason 必填）
        var emptyFactories = ResultFactories()
            .Where(m => m.Name.StartsWith("Empty", StringComparison.Ordinal))
            .ToArray();
        Assert.IsNotEmpty(emptyFactories, "阳性对照：必须存在名为 Empty* 的空结果工厂，否则下一条断言是空的");
        foreach (var factory in emptyFactories)
        {
            Assert.IsTrue(
                factory.GetParameters().Any(p => p.ParameterType == typeof(RetrievalEmptyReason)),
                $"{factory.Name} 必须声明 RetrievalEmptyReason 参数（0 命中必须给原因）");
        }

        // ⑤ 结构证明：任何公开工厂都必须显式接收"真实总数"或"空结果原因"，二者不可回避
        var factories = ResultFactories();
        Assert.IsNotEmpty(factories, "阳性对照：必须存在公开工厂");
        foreach (var factory in factories)
        {
            var hasTotal = factory.GetParameters()
                .Any(p => string.Equals(p.Name, "totalCount", StringComparison.Ordinal) && !p.HasDefaultValue);
            var hasReason = factory.GetParameters()
                .Any(p => p.ParameterType == typeof(RetrievalEmptyReason));

            Assert.IsTrue(
                hasTotal || hasReason,
                $"{factory.Name} 必须显式给出真实总数（无默认值）或空结果原因 —— 否则\"总量/原因不可回避\"不成立");
        }
    }

    /// <summary>A3：降级必须带原因；未降级不得带原因。</summary>
    [TestMethod]
    public void A03_Degraded_Requires_Reason()
    {
        var request = RetrievalTestData.Request();
        var page = RetrievalTestData.Page(2);

        Assert.ThrowsExactly<RetrievalContractViolationException>(
            () => RetrievalResult.Create(request, page, page.Length, RetrievalTestData.IndexVersion, degraded: true),
            "degraded=true 却不给原因 ⇒ 拒绝");

        Assert.ThrowsExactly<RetrievalContractViolationException>(
            () => RetrievalResult.Create(request, page, page.Length, RetrievalTestData.IndexVersion, degraded: true, degradedReason: "   "),
            "降级原因不得为空白");

        var degraded = RetrievalResult.Create(
            request,
            page,
            page.Length,
            RetrievalTestData.IndexVersion,
            degraded: true,
            degradedReason: "语言不支持 References，本次已退化为文本面结果");

        Assert.IsTrue(degraded.Degraded);
        Assert.IsFalse(string.IsNullOrWhiteSpace(degraded.DegradedReason));

        Assert.ThrowsExactly<RetrievalContractViolationException>(
            () => RetrievalResult.Create(request, page, page.Length, RetrievalTestData.IndexVersion, degradedReason: "无中生有的降级原因"),
            "未降级却给降级原因 ⇒ 拒绝");
    }

    /// <summary>
    /// A4：至多一条下一步。语义**明确选定为"失败"**（不是静默截断第一条）——
    /// 静默丢建议等于让 Agent 少一次纠错机会，与"不打转"的目标相反。
    /// </summary>
    [TestMethod]
    public void A04_At_Most_One_Next_Step()
    {
        var request = RetrievalTestData.Request();
        var stepA = RetrievalTestData.NarrowStep();
        var stepB = RetrievalNextStep.Narrow(RetrievalSuggestionKind.FileTypeFilter, "只搜 .cs", ".cs");

        Assert.ThrowsExactly<RetrievalContractViolationException>(
            () => RetrievalResult.Create(
                request,
                [],
                0,
                RetrievalTestData.IndexVersion,
                emptyReason: RetrievalEmptyReason.GenuinelyAbsent,
                nextSteps: [stepA, stepB]),
            "两条下一步 ⇒ 失败（不截断）");

        var empty = RetrievalResult.Create(
            request,
            [],
            0,
            RetrievalTestData.IndexVersion,
            emptyReason: RetrievalEmptyReason.GenuinelyAbsent,
            nextSteps: [RetrievalNextStep.AcceptAbsence("确认确实不存在，可换文本面交叉验证")]);

        Assert.HasCount(1, empty.NextSteps);
        Assert.IsNotNull(empty.NextStep);
        Assert.AreEqual(RetrievalNextStepKind.AcceptAbsence, empty.NextStep!.Kind);

        // 空结果一条建议都不给 ⇒ 拒绝（§8.6：每次调用要么给结果，要么给一个可执行的下一步）
        Assert.ThrowsExactly<RetrievalContractViolationException>(
            () => RetrievalResult.Create(
                request,
                [],
                0,
                RetrievalTestData.IndexVersion,
                emptyReason: RetrievalEmptyReason.GenuinelyAbsent));
    }

    /// <summary>A9：不得静默截断 —— 超预算必须给真实总数 + 游标 + 落盘 + 分布。</summary>
    [TestMethod]
    public void A09_Truncation_Must_Spill_With_Total_Cursor_And_Distribution()
    {
        var request = RetrievalTestData.Request(pageSize: 10);
        var page = RetrievalTestData.Page(10);
        const int totalCount = 200;

        var result = RetrievalTestData.Truncated(request, page, totalCount);

        Assert.AreEqual(totalCount, result.TotalCount, "TotalCount 必须是**真实总数**（不是返回条数）");
        Assert.AreNotEqual(result.Hits.Count, result.TotalCount, "对照：本例中真实总数必须严格大于返回条数");
        Assert.IsTrue(result.HasMore, "截断必须 HasMore=true");
        Assert.IsTrue(result.IsTruncated);
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.NextCursor), "截断必须给分页游标");

        Assert.IsNotNull(result.Overflow, "超预算必须给出落盘信息（不得静默截断）");
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.Overflow!.Path), "落盘信息必须带路径");
        Assert.IsTrue(
            RetrievalSpillPolicy.IsAllowedSpillPath(result.Overflow.Path),
            $"落盘路径必须位于 {RetrievalSpillPolicy.DescribeAllowedRoots()}");
        Assert.IsTrue(result.Overflow.SpilledCount > 0, "落盘信息必须给已写入条数");
        Assert.AreEqual(RetrievalBudgetKind.ItemCount, result.Overflow.TriggeredBy, "落盘信息必须给预算口径");

        Assert.IsFalse(result.Distribution.IsEmpty, "截断必须附带非空分布摘要");
        Assert.AreEqual(totalCount, result.TotalCount);

        // 落盘路径守卫本身必须能取红（负面对照：仓库源码路径必须被拒）
        Assert.IsFalse(
            RetrievalSpillPolicy.IsAllowedSpillPath("Source/PuddingCodeIndex/Contracts/Retrieval/spill.jsonl"),
            "仓库源码路径不得被当作合法落盘位置");
        Assert.ThrowsExactly<ArgumentException>(
            () => RetrievalOverflow.Create("Source/PuddingCodeIndex/spill.jsonl", 10, RetrievalBudgetKind.ItemCount),
            "往仓库里写结果文件必须被拒（污染仓库）");
        Assert.ThrowsExactly<ArgumentException>(
            () => RetrievalOverflow.Create(".pudding/retrieval/x.jsonl", 10, RetrievalBudgetKind.None),
            "落盘就必须说明是哪个预算先触顶");

        // "只截断、不给总量/分布/落盘" ⇒ **不可表示**
        Assert.ThrowsExactly<RetrievalContractViolationException>(() => RetrievalResult.Create(
            request,
            page,
            totalCount,
            RetrievalTestData.IndexVersion,
            nextSteps: [RetrievalTestData.NarrowStep()],
            nextCursor: "cursor-1",
            distribution: RetrievalDistribution.FromHits(page)));

        Assert.ThrowsExactly<RetrievalContractViolationException>(() => RetrievalResult.Create(
            request,
            page,
            totalCount,
            RetrievalTestData.IndexVersion,
            nextSteps: [RetrievalTestData.NarrowStep()],
            overflow: RetrievalTestData.Spill(totalCount),
            nextCursor: "cursor-1"));

        Assert.ThrowsExactly<RetrievalContractViolationException>(() => RetrievalResult.Create(
            request,
            page,
            totalCount,
            RetrievalTestData.IndexVersion,
            nextSteps: [RetrievalTestData.NarrowStep()],
            overflow: RetrievalTestData.Spill(totalCount),
            distribution: RetrievalDistribution.FromHits(page)));

        // 真实总数不可省略（无默认值）——否则"不给总量"就会变成合法路径
        var totalCountParameter = typeof(RetrievalResult)
            .GetMethod(nameof(RetrievalResult.Create))!
            .GetParameters()
            .Single(p => string.Equals(p.Name, "totalCount", StringComparison.Ordinal));
        Assert.IsFalse(totalCountParameter.HasDefaultValue, "totalCount 不得有默认值");

        // 完整返回：不得挂落盘信息 / 游标
        var complete = RetrievalResult.Create(request, page, page.Length, RetrievalTestData.IndexVersion);
        Assert.IsFalse(complete.HasMore);
        Assert.IsFalse(complete.IsTruncated);
        Assert.IsNull(complete.Overflow);
        Assert.IsNull(complete.NextCursor);

        Assert.ThrowsExactly<RetrievalContractViolationException>(() => RetrievalResult.Create(
            request,
            page,
            page.Length,
            RetrievalTestData.IndexVersion,
            overflow: RetrievalTestData.Spill(page.Length),
            distribution: RetrievalDistribution.FromHits(page)));
    }

    private static IReadOnlyList<MethodInfo> ResultFactories() =>
    [
        .. typeof(RetrievalResult)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.ReturnType == typeof(RetrievalResult))
    ];
}
