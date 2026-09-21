using PuddingCode.Configuration;
using PuddingMemoryEngine.Services;
using PuddingRuntime.Services.Skills;

namespace PuddingRuntimeTests.Services;

/// <summary>
/// 真实索引上的关键词归属**只读**可行性探针（RSI-G4 交付物 D4c）。
/// <para>
/// 它存在的理由：D4b 的 <c>SkillKeywordOwnershipProbe</c> 只在合成 fixture 上被证明过。
/// 「探针算出来的 165 / 1735 与 G1 报告逐数一致」这句话，如果只写在提交信息里，就只是一个**声明**；
/// 这里的用例是它的**可复跑载体**。
/// </para>
/// <para>
/// <b>为什么门控（而不是无条件跑）</b>：真实技能仓随会话自进化持续变化 —— 例如本仓库 2026-09-22 实测
/// 已从 G1 的 144/139 技能长到 149/144。把它的数字写成无条件断言，会让构建结果随**本机数据**浮动：
/// 今天红、明天绿，而红绿都不代表代码对错。缺环境变量时本类**如实返回 Inconclusive（无结论）**，
/// ⛔ 既不是"通过"，也不是"静默跳过"（MSTest 会把它单独计为"无结论/跳过"）。
/// </para>
/// <remarks>
/// <b>门控变量</b>：<c>PUDDING_G4_DATA_ROOT</c>（数据根，例如 <c>D:\data</c>）+
/// <c>PUDDING_G4_AGENT_ID</c>（例如 <c>default.global_general-assistant.6a8</c>）。
/// 两者都给出时才跑；索引读取走 <see cref="AgentSkillFileService.GetIndexAsync"/>（**纯读**：不落盘、不建目录），
/// ⛔ 绝不调用 <c>RebuildIndexAsync</c>（那会写盘）——本片职权是只读事实，不是修索引。
/// <para>
/// <b>失败时的分诊纪律</b>：数字不同时，先判「**口径漂移**」还是「**技能已变化**」——
/// 判据是 <see cref="RealIndex_G1Baseline_ShouldBeReproducibleOnTheEraSubset"/>：
/// 若在"G1 时代子集"上仍能逐数复现 2490/755/165/1735，则口径未漂移、差异来自数据；
/// 反之就是口径漂移（必须修实现，⛔ 不得改断言迁就实现）。
/// </para>
/// </remarks>
[TestClass]
public sealed class SkillKeywordOwnershipRealIndexProbeTests
{
    private const string DataRootVariable = "PUDDING_G4_DATA_ROOT";
    private const string AgentIdVariable = "PUDDING_G4_AGENT_ID";

    /// <summary>
    /// G1 报告（<c>Docs/Reports/skill-portfolio-G1-2026-09-21.md</c>）之后**新建**的技能
    /// （由技能目录创建时间实测：2026-09-21 20:27~20:29 四个 + 2026-09-22 01:20 一个）。
    /// 把它们从当前索引里剔除，就得到 G1 当时的 144 技能快照 —— 用于「口径漂移探测器」。
    /// </summary>
    private static readonly string[] SkillsCreatedAfterTheG1Report =
    [
        "post-restart-deployment-verification",
        "ascii-only-powershell-scripting",
        "clean-window-restart-coordination",
        "dry-run-then-apply-batch-file-migration-verified-by-external-validator",
        "spec-freeze-reverse-verification",
    ];

    /// <summary>
    /// 不变量：无论真实技能仓怎么变，这些关系必须成立（口径无关）⇒ 这条用例**不会随数据增长而变砖**。
    /// </summary>
    [TestMethod]
    public async Task RealIndex_ShouldSatisfyOwnershipInvariants()
    {
        if (!TryOpenGate(out var dataRoot, out var agentId))
        {
            SkipBecauseGateIsClosed();
            return;
        }

        var report = await AnalyzeRealIndexAsync(dataRoot, agentId);
        PrintReport(report);

        Assert.IsGreaterThan(0, report.EnabledSkillCount, "真实索引里应当有启用技能（0 ⇒ 门控给错了目录/agent id）。");

        // ① 总量必须由明细推导（D4b 的同一不变量，在真实数据上再钉一次）。
        Assert.AreEqual(report.Ownerships.Count, report.DistinctKeywordCount);
        Assert.AreEqual(report.Ownerships.Sum(o => o.DeclaredCount), report.SlotCount);
        Assert.AreEqual(report.Ownerships.Sum(o => o.DisplacedCount), report.DisplacedInjectionCount);
        Assert.AreEqual(report.Ownerships.Count(o => o.IsShared), report.SharedKeywordCount);

        // ② 事实必须完整：不得出现空白关键词项，共享关键词必须列出**全部**竞争者且不重复。
        Assert.IsTrue(
            report.Ownerships.All(o => !string.IsNullOrWhiteSpace(o.Keyword)),
            "空白关键词不产生注入机会，不得成为归属项。");
        foreach (var ownership in report.Ownerships.Where(o => o.IsShared))
        {
            Assert.AreEqual(
                ownership.DeclaringSkillIds.Count,
                ownership.DeclaringSkillIds.Distinct(StringComparer.Ordinal).Count(),
                $"关键词 {ownership.Keyword} 的声明者清单出现重复。");
            Assert.IsTrue(
                ownership.DeclaredCount >= 2,
                $"关键词 {ownership.Keyword} 被标为共享却只有 {ownership.DeclaredCount} 个声明者。");
        }

        // ③ 声明者必须都落在启用技能集合内（禁用技能不得进入事实集）。
        var enabledSkillIds = await ReadEnabledSkillIdsAsync(dataRoot, agentId);
        var unknown = report.Ownerships
            .SelectMany(o => o.DeclaringSkillIds)
            .Where(id => !enabledSkillIds.Contains(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Assert.IsEmpty(unknown, "归属事实里出现了非启用技能的声明者：" + string.Join(", ", unknown));
    }

    /// <summary>
    /// 当日实测基线（**带日期**的快照断言）。
    /// <para>
    /// 2026-09-21 G1 报告：144 技能 / 139 启用 / 2490 槽位 / 755 去重 / 165 共享 / 1735 被挤掉。
    /// 2026-09-22 本探针实测：149 技能 / 144 启用 / 2572 槽位 / 779 去重 / 166 共享 / 1793 被挤掉。
    /// 差异已归因（见 <see cref="RealIndex_G1Baseline_ShouldBeReproducibleOnTheEraSubset"/>）：
    /// 期间新增 5 个启用技能，槽位增量 82 = 18+11+18+17+18 精确吻合 ⇒ **口径未漂移**。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task RealIndex_ShouldReproduceTheMeasuredBaseline()
    {
        if (!TryOpenGate(out var dataRoot, out var agentId))
        {
            SkipBecauseGateIsClosed();
            return;
        }

        var report = await AnalyzeRealIndexAsync(dataRoot, agentId);
        PrintReport(report);

        Assert.AreEqual(144, report.EnabledSkillCount, BaselineMessage("启用技能数"));
        Assert.AreEqual(2572, report.SlotCount, BaselineMessage("关键词槽位"));
        Assert.AreEqual(779, report.DistinctKeywordCount, BaselineMessage("去重关键词"));
        Assert.AreEqual(166, report.SharedKeywordCount, BaselineMessage("被 ≥2 启用技能共享的关键词"));
        Assert.AreEqual(1793, report.DisplacedInjectionCount, BaselineMessage("被挤掉的注入机会总数"));
    }

    /// <summary>
    /// **口径漂移探测器**：把 G1 之后新建的技能剔除，剩下的 144 技能快照上必须**逐数复现 G1 报告**。
    /// <para>
    /// 这是"数字变了"这件事的归因装置：复现成功 ⇒ 口径一致、差异只来自数据；
    /// 复现失败 ⇒ 口径漂移（⛔ 修实现，不是改这里的期望值）。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task RealIndex_G1Baseline_ShouldBeReproducibleOnTheEraSubset()
    {
        if (!TryOpenGate(out var dataRoot, out var agentId))
        {
            SkipBecauseGateIsClosed();
            return;
        }

        var index = await ReadIndexAsync(dataRoot, agentId);
        var present = index.Skills
            .Select(e => e.SkillId)
            .Where(id => SkillsCreatedAfterTheG1Report.Contains(id, StringComparer.Ordinal))
            .ToArray();
        if (present.Length == 0)
        {
            Assert.Inconclusive(
                "当前索引里找不到 G1 之后新建的那 5 个技能 ⇒ 无法重建 G1 时代快照，本用例无结论（不是通过）。");
            return;
        }

        var excluded = present.ToHashSet(StringComparer.Ordinal);
        var subjects = index.Skills
            .Where(e => !excluded.Contains(e.SkillId))
            .Select(ToSubject)
            .ToList();
        var report = SkillKeywordOwnershipProbe.Analyze(subjects);
        PrintReport(report);
        Console.WriteLine("  (剔除技能: " + string.Join(", ", present) + ")");

        Assert.AreEqual(139, report.EnabledSkillCount, EraMessage("启用技能数"));
        Assert.AreEqual(2490, report.SlotCount, EraMessage("关键词槽位"));
        Assert.AreEqual(755, report.DistinctKeywordCount, EraMessage("去重关键词"));
        Assert.AreEqual(165, report.SharedKeywordCount, EraMessage("共享关键词"));
        Assert.AreEqual(1735, report.DisplacedInjectionCount, EraMessage("被挤掉的注入机会"));
    }

    // ───────────────────────── helpers ─────────────────────────

    private static bool TryOpenGate(out string dataRoot, out string agentId)
    {
        dataRoot = Environment.GetEnvironmentVariable(DataRootVariable) ?? string.Empty;
        agentId = Environment.GetEnvironmentVariable(AgentIdVariable) ?? string.Empty;
        return !string.IsNullOrWhiteSpace(dataRoot) && !string.IsNullOrWhiteSpace(agentId);
    }

    private static void SkipBecauseGateIsClosed()
        => Assert.Inconclusive(
            $"未提供 {DataRootVariable} / {AgentIdVariable}（真实技能仓数据根 + agent id）⇒ " +
            "本用例对真实数据**无结论**（Inconclusive ≠ 通过）。门控是刻意的：真实技能仓随自进化持续变化，" +
            "把它的数字写成无条件断言会让构建随本机数据浮动（今天红明天绿，与代码对错无关）。");

    private static async Task<AgentSkillIndex> ReadIndexAsync(string dataRoot, string agentId)
    {
        // GetIndexAsync 是纯读：不落盘、不建目录（RebuildIndexAsync 才会写盘，本片明令不用）。
        var files = new AgentSkillFileService(PuddingDataPaths.FromRoot(dataRoot));
        return await files.GetIndexAsync(agentId);
    }

    private static async Task<HashSet<string>> ReadEnabledSkillIdsAsync(string dataRoot, string agentId)
    {
        var index = await ReadIndexAsync(dataRoot, agentId);
        return index.Skills.Where(e => e.Enabled).Select(e => e.SkillId).ToHashSet(StringComparer.Ordinal);
    }

    private static async Task<SkillKeywordOwnershipReport> AnalyzeRealIndexAsync(string dataRoot, string agentId)
    {
        var index = await ReadIndexAsync(dataRoot, agentId);
        return SkillKeywordOwnershipProbe.Analyze(index.Skills.Select(ToSubject).ToList());
    }

    private static SkillKeywordSubject ToSubject(AgentSkillIndexEntry entry) => new()
    {
        SkillId = entry.SkillId,
        Keywords = entry.Keywords,
        Tags = entry.Tags,
        Name = entry.Name,
        Enabled = entry.Enabled,
    };

    private static void PrintReport(SkillKeywordOwnershipReport report)
    {
        // 数字进日志 = 可复核的证据链（否则"实测过"只是一句话）。
        Console.WriteLine(
            $"  enabled={report.EnabledSkillCount} slots={report.SlotCount} distinct={report.DistinctKeywordCount} " +
            $"shared={report.SharedKeywordCount} displaced={report.DisplacedInjectionCount}");
        foreach (var ownership in report.Ownerships
            .Where(o => o.IsShared)
            .OrderByDescending(o => o.DeclaredCount)
            .ThenBy(o => o.Keyword, StringComparer.Ordinal)
            .Take(5))
        {
            Console.WriteLine($"  {ownership.DeclaredCount,4}  {ownership.Keyword}");
        }
    }

    private static string BaselineMessage(string field)
        => $"{field} 与 2026-09-22 实测基线不符。分诊顺序：① 先跑 " +
           "RealIndex_G1Baseline_ShouldBeReproducibleOnTheEraSubset —— 若 G1 时代子集仍能逐数复现 " +
           "144/139/2490/755/165/1735，则口径未漂移、差异来自技能仓变化（此时更新此处基线并写明日期即可）；" +
           "② 若子集复现不了，就是口径漂移 ⇒ 修 SkillKeywordOwnershipProbe，⛔ 不得改断言迁就实现。";

    private static string EraMessage(string field)
        => $"{field} 未复现 G1 报告（144 技能 / 139 启用 / 2490 槽位 / 755 去重 / 165 共享 / 1735 被挤掉）。" +
           "子集是『剔除 G1 之后新建技能』重建的 144 技能快照 ⇒ 复现失败意味着**口径漂移**（分词/来源/大小写/去重口径之一改动），" +
           "必须修实现，⛔ 不得改断言。";
}
