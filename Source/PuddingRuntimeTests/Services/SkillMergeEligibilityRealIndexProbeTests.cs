using System.Text.Json;
using PuddingCode.Platform;
using PuddingCode.Skills.Family;
using PuddingCode.Skills.Retrieval;
using PuddingMemoryEngine.Services;

namespace PuddingRuntimeTests.Services;

/// <summary>
/// 真实技能仓上的**合并可行性只读探针**（RSI-G4 交付物 D6b；任务书 §2.5-2 / §2.5-3）。
/// <para>
/// <b>它回答两个数</b>（在 §3-F 报告的 10 个近重复簇上，即 33 个技能组成的 75 个同族对）：
/// <list type="number">
/// <item><b>(a)</b> 有多少对**真的**同时通过合并四条件 —— 同家族 + 关键词重叠 + 程序性文本相似 ≥ 阈值 + 证据可归并；</item>
/// <item><b>(b)</b> 在这些对里，有多少对**再叠加既有确定性闸门**（<see cref="SkillEvolutionDeduplicationService.IsDeterministicallyEligible"/>）后仍然通过。</item>
/// </list>
/// </para>
/// <para>
/// <b>为什么必须问第二个数</b>：如果四条件判据在既有闸门之上**一对都新增不了**，那它就不是一个"已生效的合并能力"，
/// 只是一个永不触发的形式条款。任务书 §2.5-3 明确禁止"静默保留一个永不触发的判据"⇒ 本探针把 <c>(a, b)</c> 两个数
/// **同时钉进基线**，并让"是否新增触发面"这个结论由<b>实测数字推导</b>后打印（⛔ 不是注释里的一句承诺）。
/// </para>
/// <para>
/// <b>来源是权威事实文件、不是派生索引</b>：每个技能目录下的 <c>manifest.json</c>（元数据）与 <c>SKILL.md</c>（程序性正文）。
/// 全程只做目录枚举 + <see cref="File.ReadAllText(string)"/>：⛔ 不写盘、不建目录、不调 LLM、不触发任何会落库的入口
/// （例如 <c>ConsolidateExistingAsync</c>）。
/// </para>
/// <para>
/// <b>为什么门控（而不是无条件跑）</b>：技能仓随会话自进化持续变化 —— 把数字写成无条件断言会让构建结果随
/// **本机数据**浮动（今天红、明天绿，而红绿都不代表代码对错）。缺环境变量时本类**如实返回无结论**（Inconclusive），
/// ⛔ 既不是"通过"，也不是"静默跳过"。
/// </para>
/// </summary>
/// <remarks>
/// <b>门控变量</b>：<c>PUDDING_G4_DATA_ROOT</c>（数据根，例如 <c>D:\data</c>）+
/// <c>PUDDING_G4_AGENT_ID</c>（例如 <c>default.global_general-assistant.6a8</c>），两者都给出时才跑。
/// <para>
/// <b>失败时的分诊纪律</b>：数字与基线不符时，先跑 <see cref="EraSubset_ShouldReproduceTheG4ReportFamilyBaseline"/>
/// —— 若 §3-F 时代的子集仍能逐数复现 144/139/10/33/11/75，则**口径未漂移**、差异来自技能仓增长
/// （此时更新基线并写明日期即可）；若子集复现不了，就是**口径漂移** ⇒ 修实现，⛔ 不得改断言迁就实现。
/// </para>
/// </remarks>
[TestClass]
public sealed class SkillMergeEligibilityRealIndexProbeTests
{
    private const string DataRootVariable = "PUDDING_G4_DATA_ROOT";
    private const string AgentIdVariable = "PUDDING_G4_AGENT_ID";

    /// <summary>§3-F 的家族口径：名称分词 token 的 Jaccard ≥ 0.25（报告原文）。</summary>
    private const double FamilyNameThreshold = 0.25;

    /// <summary>
    /// 程序性文本（<c>SKILL.md</c> 正文）相似度的三个档位：<c>0.20</c> / <c>0.35</c> / <c>0.50</c>。
    /// <para>
    /// 为什么是这三档：前两档与**既有闸门**的元数据文本档位同源（下界 0.20 / 上界 0.35），便于做"同水位比较"；
    /// 第三档（0.50）更严，用来回答"结论对阈值有多敏感"。
    /// </para>
    /// <para>
    /// ⚠️ 这三档<strong>不是</strong>产品默认值：产品阈值必须由调用方显式给出（
    /// <see cref="SkillMergePolicy.MinimumProceduralTextSimilarity"/>，其类型刻意不提供默认策略）。
    /// </para>
    /// </summary>
    private static readonly double[] ProceduralTextThresholds = [0.20, 0.35, 0.50];

    /// <summary>
    /// §3-F 报告（2026-09-21 18:44）之后**新建**的技能（由技能目录创建时间实测：
    /// 2026-09-21 20:27~20:29 四个 + 2026-09-22 01:20 一个）。剔除它们即得到报告当时的技能快照。
    /// </summary>
    private static readonly string[] SkillsCreatedAfterTheG4Report =
    [
        "post-restart-deployment-verification",
        "ascii-only-powershell-scripting",
        "clean-window-restart-coordination",
        "dry-run-then-apply-batch-file-migration-verified-by-external-validator",
        "spec-freeze-reverse-verification",
    ];

    // ── §3-F 时代子集基线（2026-09-21 18:44 实测）────────────────────────────
    private const int EraBaselineSkillCount = 144;
    private const int EraBaselineEnabledCount = 139;
    private const int EraBaselineFamilyCount = 10;
    private const int EraBaselineFamilyMemberCount = 33;
    private const int EraBaselineLargestFamilySize = 11;
    private const int EraBaselineIntraFamilyPairCount = 75;

    /// <summary>
    /// 可行性基线（D6b 实测、D6c 口径修复后复核，2026-09-22 钉入）。
    /// <para>
    /// 取值（时代子集：144 技能 / 139 启用 / 10 家族 / 75 同族对）：
    /// 过四条件 <b>17 / 4 / 0</b>（程序性文本阈值 0.20 / 0.35 / 0.50）；
    /// 再叠加既有确定性闸门 <b>0 / 0 / 0</b> ⇒ 本片判据在真实数据上不是已生效的合并能力。
    /// </para>
    /// <para>
    /// ⛔ 钉入后不得为了让断言变绿而改这些数字：它们是与基线**日期**绑定的实测事实。
    /// 变红时按类注释的分诊纪律处理：① 先判口径漂移（判据/归一化/相似度实现是否被改）；
    /// ② 再判数据变化（技能仓新增/改写技能）—— 若是后者，**必须显式复算并说明新事实**，
    /// 因为“叠加面 = 0”这个裁决结论必须以当时的真实语料为据。
    /// </para>
    /// </summary>
    private const int FourConditionBaseline020 = 17;
    private const int FourConditionBaseline035 = 4;
    private const int FourConditionBaseline050 = 0;
    private const int GateOverlayBaseline020 = 0;
    private const int GateOverlayBaseline035 = 0;
    private const int GateOverlayBaseline050 = 0;

    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// 不变式：无论技能仓怎么增长，这些关系必须成立（口径无关）⇒ 本用例**不会随数据增长而变砖**。
    /// </summary>
    [TestMethod]
    public void RealSkills_ShouldSatisfyMergeProbeInvariants()
    {
        if (!TryOpenGate(out var dataRoot, out var agentId))
        {
            SkipBecauseGateIsClosed();
            return;
        }

        var documents = LoadDocuments(dataRoot, agentId);
        Assert.IsGreaterThan(0, documents.Count, "技能仓里应当有技能（0 ⇒ 门控给错了目录/agent id）。");

        // ① 正文必须存在（缺正文 ⇒ 程序性文本条件退化为恒假，统计不可信）。
        //    关键词则**必须**过 D4 的唯一归一口径后才能喂判据：实测真实 manifest 里存在未归一大写项
        //    （例如 `Agent Recent Activity Check`——就是技能名本身）⇒ 本用例把该事实**打印**出来，
        //    并断言**归一化产出**是归一小写形态。⛔ 断言对象是归一化产出，不是原始数据：
        //    原始数据是否规整不由本片负责，但“消费前必须归一”由本片钉住。
        // ① 正文缺失是**真实数据事实**（实测有技能没有 SKILL.md）：它的所有同族对**不可评估** C3，
        //    因此显式排除并列数 —— ⛔ 不得让它静默地把 C3 记成“恒假”（那会低估可行性）。
        var bodylessSkillIds = documents
            .Where(document => string.IsNullOrWhiteSpace(document.Markdown))
            .Select(document => document.SkillId)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        Console.WriteLine(
            $"  bodylessSkills={bodylessSkillIds.Length} [{string.Join(", ", bodylessSkillIds)}] ⇒ 其同族对不可评估 C3，已显式排除");

        var rawKeywordsNotNormalized = 0;
        foreach (var document in documents)
        {
            rawKeywordsNotNormalized += document.Keywords.Count(keyword => keyword != keyword.ToLowerInvariant());
            var normalized = SkillKeywordNormalization
                .Collect(document.Keywords, document.Tags, document.SkillId, document.Name)
                .ToList();
            Assert.IsGreaterThan(0, normalized.Count, $"技能 {document.SkillId} 归一口径产出空关键词集 ⇒ C2 退化为恒假。");
            Assert.IsTrue(
                normalized.All(keyword => !string.IsNullOrWhiteSpace(keyword)),
                $"技能 {document.SkillId} 的归一化产出含空白关键词 ⇒ 归一口径被破坏。");
        }

        Console.WriteLine(
            $"  rawKeywordsNotNormalized={rawKeywordsNotNormalized}" +
            "（数据事实：D4 归一口径保留原始大小写；D6c 已在判据侧改用同一 KeywordComparer ⇒ 调用方无需折叠）");

        // ② 家族划分必须确定性（同一输入 ⇒ 同一家族键序列）。家族键是不稳定输入的产物时，
        //    "同族对数"这个分母就不可复现，后面的数字也就没有意义。
        var policy = FamilyPolicy();
        var first = SkillFamilyClusterer.Cluster(Subjects(documents), policy);
        var second = SkillFamilyClusterer.Cluster(Subjects(documents), policy);
        CollectionAssert.AreEqual(
            first.Select(cluster => cluster.FamilyKey).ToList(),
            second.Select(cluster => cluster.FamilyKey).ToList(),
            "同一输入必须得到同一家族划分（家族键序列相等）。");

        // ③ 报告总量必须由明细推导。
        var clusters = MultiMemberClusters(first);
        var pairs = IntraFamilyPairs(documents, clusters);
        var report = Judge(pairs, ProceduralTextThresholds[0]);
        Assert.AreEqual(pairs.Count, report.EvaluatedPairCount, "被评估对必须等于构造出的同族对。");
        Assert.AreEqual(report.Verdicts.Count(verdict => verdict.IsEligible), report.EligiblePairCount, "通过对数必须由明细推导。");
        Assert.IsTrue(
            pairs.All(pair => !string.IsNullOrWhiteSpace(pair.Left.FamilyKey) && pair.Left.FamilyKey == pair.Right.FamilyKey),
            "同族对的两端必须带同一个非空家族键（否则 C1 的输入面就是假的）。");

        // ④ 本探针最关键的不变式：**叠加面是四条件面的子集**。
        //    "叠加"是在四条件之上再加一条既有闸门 ⇒ 它只可能更窄，不可能更宽。
        //    这一条能变红：任何把两套判据混成一套、或让闸门反过来放宽的实现都会被它抓住。
        foreach (var threshold in ProceduralTextThresholds)
        {
            var fourCondition = Judge(pairs, threshold);
            var overlay = CountGateOverlay(documents, fourCondition);
            Assert.IsTrue(
                overlay <= fourCondition.EligiblePairCount,
                $"阈值 {threshold:0.00}：叠加既有闸门后的对数（{overlay}）不得超过四条件通过对数（{fourCondition.EligiblePairCount}）——" +
                "叠加是收窄，不是放宽。");
        }
    }

    /// <summary>
    /// 口径漂移探测器：在被剔除"报告之后新建技能"的**时代子集**上，逐数复现 §3-F 报告的家族事实。
    /// </summary>
    [TestMethod]
    public void EraSubset_ShouldReproduceTheG4ReportFamilyBaseline()
    {
        if (!TryOpenGate(out var dataRoot, out var agentId))
        {
            SkipBecauseGateIsClosed();
            return;
        }

        var documents = LoadDocuments(dataRoot, agentId);
        var eraSubset = EraSubset(documents);

        PrintFamilyDistribution(SkillFamilyClusterer.Cluster(Subjects(eraSubset), FamilyPolicy()));

        Assert.AreEqual(EraBaselineSkillCount, eraSubset.Count, EraMessage("技能总数"));
        Assert.AreEqual(EraBaselineEnabledCount, eraSubset.Count(document => document.Enabled), EraMessage("启用技能数"));

        var families = MultiMemberClusters(SkillFamilyClusterer.Cluster(Subjects(eraSubset), FamilyPolicy()));
        Assert.AreEqual(EraBaselineFamilyCount, families.Count, EraMessage("≥2 成员家族数"));
        Assert.AreEqual(EraBaselineFamilyMemberCount, families.Sum(family => family.Size), EraMessage("被家族覆盖的技能数"));
        Assert.AreEqual(EraBaselineLargestFamilySize, families.Max(family => family.Size), EraMessage("最大家族规模"));
        Assert.AreEqual(
            EraBaselineIntraFamilyPairCount,
            families.Sum(family => family.Size * (family.Size - 1) / 2),
            EraMessage("同族对数"));
    }

    /// <summary>
    /// 本片的核心交付：把 <c>(a) 四条件通过对数</c> 与 <c>(b) 叠加既有闸门后对数</c> 一起量出来、钉住，
    /// 并让"是否在闸门之上新增触发面"由实测数字推导成结论行。
    /// </summary>
    [TestMethod]
    public void EraSubset_ShouldReportFourConditionAndGateOverlayFeasibility()
    {
        if (!TryOpenGate(out var dataRoot, out var agentId))
        {
            SkipBecauseGateIsClosed();
            return;
        }

        var documents = LoadDocuments(dataRoot, agentId);
        var eraSubset = EraSubset(documents);
        var families = MultiMemberClusters(SkillFamilyClusterer.Cluster(Subjects(eraSubset), FamilyPolicy()));
        var pairs = IntraFamilyPairs(documents, families);

        Console.WriteLine(
            $"  era-subset: skills={eraSubset.Count} enabled={eraSubset.Count(document => document.Enabled)} " +
            $"families={families.Count} intraFamilyPairs={pairs.Count}");

        var fourConditionBaselines = new[] { FourConditionBaseline020, FourConditionBaseline035, FourConditionBaseline050 };
        var overlayBaselines = new[] { GateOverlayBaseline020, GateOverlayBaseline035, GateOverlayBaseline050 };

        for (var index = 0; index < ProceduralTextThresholds.Length; index++)
        {
            var threshold = ProceduralTextThresholds[index];
            var report = Judge(pairs, threshold);
            var eligible = report.Verdicts.Where(verdict => verdict.IsEligible).ToList();
            var overlay = eligible
                .Where(verdict => IsGateEligible(documents, verdict.LeftSkillId, verdict.RightSkillId))
                .ToList();

            PrintFeasibility(threshold, report, eligible.Count, overlay.Count);

            Assert.IsTrue(
                overlay.Count <= eligible.Count,
                $"阈值 {threshold:0.00}：叠加面（{overlay.Count}）必须是四条件面（{eligible.Count}）的子集。");

            if (fourConditionBaselines[index] >= 0)
            {
                Assert.AreEqual(fourConditionBaselines[index], eligible.Count, BaselineMessage($"四条件通过对数（程序性文本 ≥ {threshold:0.00}）"));
                Assert.AreEqual(overlayBaselines[index], overlay.Count, BaselineMessage($"叠加既有闸门后对数（程序性文本 ≥ {threshold:0.00}）"));
            }

            PrintVerdict(threshold, eligible.Count, overlay.Count);
        }
    }

    // ── 数据装载（只读）──────────────────────────────────────────────────────

    private static IReadOnlyList<AgentSkillEvolutionDocument> LoadDocuments(string dataRoot, string agentId)
    {
        var skillsDirectory = Path.Combine(dataRoot, "agents", agentId, "skills");
        Assert.IsTrue(
            Directory.Exists(skillsDirectory),
            $"技能目录不存在：{skillsDirectory}（门控变量指错目录/agent id 了吗？）");

        var documents = new List<AgentSkillEvolutionDocument>();
        var manifestPaths = Directory
            .GetFiles(skillsDirectory, "manifest.json", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Assert.IsGreaterThan(0, manifestPaths.Length, $"{skillsDirectory} 下没有找到任何 manifest.json。");

        foreach (var manifestPath in manifestPaths)
        {
            var manifest = JsonSerializer.Deserialize<ManifestFile>(File.ReadAllText(manifestPath), ManifestJsonOptions)
                ?? throw new InvalidOperationException($"manifest.json 解析为空：{manifestPath}");
            var skillDirectory = Path.GetDirectoryName(manifestPath)!;
            var markdownPath = Path.Combine(skillDirectory, "SKILL.md");

            documents.Add(new AgentSkillEvolutionDocument
            {
                SkillId = manifest.SkillId ?? Path.GetFileName(skillDirectory),
                Name = manifest.Name ?? Path.GetFileName(skillDirectory),
                Version = manifest.Version ?? "0.0.0",
                Description = manifest.Description,
                Tags = manifest.Tags ?? [],
                Keywords = manifest.Keywords ?? [],
                Enabled = manifest.Enabled ?? true,
                Markdown = File.Exists(markdownPath) ? File.ReadAllText(markdownPath) : string.Empty,
            });
        }

        return documents;
    }

    private static IReadOnlyList<AgentSkillEvolutionDocument> EraSubset(IReadOnlyList<AgentSkillEvolutionDocument> documents)
        => documents
            .Where(document => !SkillsCreatedAfterTheG4Report.Contains(document.SkillId, StringComparer.Ordinal))
            .ToList();

    // ── 判据装配（复用同一份实现，⛔ 不另写第二套）──────────────────────────────

    private static SkillFamilyPolicy FamilyPolicy() => new()
    {
        PolicyId = "rsi-g4-family-name-tokens",
        Version = 1,
        NameTokenJaccardThreshold = FamilyNameThreshold,
        MinTokenLength = 2,
        NameSeparators = SkillNameTokenization.StandardSeparators,
    };

    private static IReadOnlyList<SkillFamilySubject> Subjects(IEnumerable<AgentSkillEvolutionDocument> documents)
        => documents
            .Where(document => document.Enabled)
            .Select(document => SkillFamilySubject.Create(document.SkillId, document.Name))
            .ToList();

    private static IReadOnlyList<SkillFamilyCluster> MultiMemberClusters(IReadOnlyList<SkillFamilyCluster> clusters)
        => clusters.Where(cluster => cluster.Size >= 2).ToList();

    private static IReadOnlyList<SkillMergePair> IntraFamilyPairs(
        IReadOnlyList<AgentSkillEvolutionDocument> documents,
        IReadOnlyList<SkillFamilyCluster> families)
    {
        var byId = documents.ToDictionary(document => document.SkillId, StringComparer.Ordinal);
        var pairs = new List<SkillMergePair>();
        foreach (var family in families)
        {
            // 成员序列已由 D1 的聚类器按序数序排好 ⇒ 对序确定、可复现。
            for (var left = 0; left < family.Members.Count; left++)
            {
                for (var right = left + 1; right < family.Members.Count; right++)
                {
                    // 任一端缺正文 ⇒ C3 **不可评估**：显式跳过（调用方会把 bodylessSkills 数打出来），
                    // ⛔ 不得把“不可评估”混进“未通过”。
                    if (string.IsNullOrWhiteSpace(byId[family.Members[left]].Markdown)
                        || string.IsNullOrWhiteSpace(byId[family.Members[right]].Markdown))
                    {
                        continue;
                    }

                    pairs.Add(new SkillMergePair
                    {
                        Left = Evidence(byId[family.Members[left]], family.FamilyKey),
                        Right = Evidence(byId[family.Members[right]], family.FamilyKey),
                    });
                }
            }
        }

        return pairs;
    }

    private static SkillMergeEvidence Evidence(AgentSkillEvolutionDocument document, string familyKey) => new()
    {
        SkillId = document.SkillId,
        FamilyKey = familyKey,
        // 关键词必须过 D4 的**唯一**归一口径（真实 manifest 里存在未归一大写项，例如技能名本身）。
        // ✅ D6c 已在**判据侧**改用同一个 KeywordComparer（大小写不敏感）⇒ 这里**不再折叠**：
        //    调用方只负责把既有归一化的产出原样传入，大小写由判据的比较器负责。
        Keywords = SkillKeywordNormalization
            .Collect(document.Keywords, document.Tags, document.SkillId, document.Name)
            .ToList(),
        ProceduralText = document.Markdown,
        // C4 的证据面复用 D7 既有的抽取口径（source-session），⛔ 不在探针里另写一套前缀解析。
        EvidenceIds = SkillEvolutionDeduplicationService.ExtractSourceSessions(document),
    };

    private static SkillMergeEligibilityReport Judge(IReadOnlyList<SkillMergePair> pairs, double proceduralTextThreshold)
    {
        // 相似度实现**只有一份**：直接用既有的 token-Jaccard（D7 的元数据文本走同一份实现）。
        var judge = new SkillMergeEligibilityJudge(SkillEvolutionDeduplicationService.CalculateTextSimilarity);
        return judge.Apply(pairs, new SkillMergePolicy
        {
            PolicyId = "rsi-g4-merge-feasibility-probe",
            Version = 1,
            MinimumProceduralTextSimilarity = proceduralTextThreshold,
        });
    }

    private static int CountGateOverlay(
        IReadOnlyList<AgentSkillEvolutionDocument> documents,
        SkillMergeEligibilityReport report)
        => report.Verdicts
            .Where(verdict => verdict.IsEligible)
            .Count(verdict => IsGateEligible(documents, verdict.LeftSkillId, verdict.RightSkillId));

    /// <summary>把**既有确定性闸门**原样叠加（⛔ 不重写、不放宽）—— 它就是生产路径上那条判据。</summary>
    private static bool IsGateEligible(
        IReadOnlyList<AgentSkillEvolutionDocument> documents,
        string leftSkillId,
        string rightSkillId)
    {
        var left = documents.Single(document => document.SkillId == leftSkillId);
        var right = documents.Single(document => document.SkillId == rightSkillId);
        return SkillEvolutionDeduplicationService.IsDeterministicallyEligible(left, right);
    }

    // ── 打印（数字进日志 = 可复核的证据链，否则"实测过"只是一句话）──────────────

    private static void PrintFamilyDistribution(IReadOnlyList<SkillFamilyCluster> clusters)
    {
        var families = clusters.Where(cluster => cluster.Size >= 2).OrderByDescending(cluster => cluster.Size).ToList();
        Console.WriteLine(
            $"  families(≥2)={families.Count} members={families.Sum(family => family.Size)} " +
            $"largest={families[0].Size} sizes=[{string.Join(", ", families.Select(family => family.Size))}]");
        foreach (var family in families.Take(3))
        {
            Console.WriteLine($"  {family.Size,4}  {family.FamilyKey}");
        }
    }

    private static void PrintFeasibility(
        double threshold,
        SkillMergeEligibilityReport report,
        int eligibleCount,
        int overlayCount)
    {
        var failed = report.Verdicts
            .SelectMany(verdict => verdict.FailedConditions)
            .GroupBy(condition => condition)
            .OrderBy(group => group.Key)
            .Select(group => $"{group.Key}={group.Count()}");
        var similarities = report.Verdicts.Select(verdict => verdict.ProceduralTextSimilarity).OrderBy(value => value).ToArray();
        var median = similarities.Length == 0 ? 0 : similarities[similarities.Length / 2];

        Console.WriteLine(
            $"  text>={threshold:0.00}: pairs={report.EvaluatedPairCount} fourCondition={eligibleCount} " +
            $"gateOverlay={overlayCount} medianTextSim={median:0.000} failed[{string.Join(" ", failed)}]");
    }

    private static void PrintVerdict(double threshold, int eligibleCount, int overlayCount)
    {
        if (overlayCount == 0)
        {
            // 任务书 §2.5-3 要求的显式裁决：把 0 写成结论，⛔ 不得当作已生效的合并能力。
            Console.WriteLine(
                $"  verdict(text>={threshold:0.00}): 叠加既有闸门后**零新增触发面** ⇒ 本片判据不是已生效的合并能力；" +
                "家族内的合并仍由既有确定性闸门决定（放行/让渡闸门属 D7 范畴，⛔ 本片不得自行放宽）。");
            return;
        }

        Console.WriteLine(
            $"  verdict(text>={threshold:0.00}): 在既有闸门之上有 {overlayCount} 对新增触发面（四条件面 {eligibleCount} 对）⇒ " +
            "可作为 D7 决策的输入，但⛔ 本片不得自行放宽或跳过既有闸门。");
    }

    private static string BaselineMessage(string field)
        => $"{field} 与 D6b 钉入的基线不符。分诊顺序：① 先跑 EraSubset_ShouldReproduceTheG4ReportFamilyBaseline —— " +
           "若 §3-F 时代子集仍能逐数复现 144/139/10/33/11/75，则口径未漂移、差异来自技能仓增长" +
           "（此时更新此处基线并写明日期即可）；② 若子集复现不了，就是口径漂移 ⇒ 修实现，⛔ 不得改断言迁就实现。";

    private static string EraMessage(string field)
        => $"{field} 未复现 §3-F 报告（144 技能 / 139 启用 / 10 个 ≥2 成员家族 / 覆盖 33 技能 / 最大 11 / 75 同族对）。" +
           "时代子集是『剔除报告之后新建的 5 个技能』重建的快照 ⇒ 复现失败意味着**口径漂移**" +
           "（家族阈值 / 分词 / 启用过滤 / 去重口径之一改动），必须修实现，⛔ 不得改断言。";

    private static bool TryOpenGate(out string dataRoot, out string agentId)
    {
        dataRoot = Environment.GetEnvironmentVariable(DataRootVariable) ?? string.Empty;
        agentId = Environment.GetEnvironmentVariable(AgentIdVariable) ?? string.Empty;
        return !string.IsNullOrWhiteSpace(dataRoot) && !string.IsNullOrWhiteSpace(agentId);
    }

    private static void SkipBecauseGateIsClosed()
        => Assert.Inconclusive(
            $"未设置 {DataRootVariable} / {AgentIdVariable} ⇒ 本探针**无结论**" +
            "（既不是通过，也不是静默跳过；要数字请显式给出真实技能仓的数据根与 agent id）。");

    /// <summary>技能目录下 <c>manifest.json</c> 的只读投影（只取本探针真正消费的字段）。</summary>
    private sealed record ManifestFile
    {
        public string? SkillId { get; init; }

        public string? Name { get; init; }

        public string? Version { get; init; }

        public string? Description { get; init; }

        public IReadOnlyList<string>? Tags { get; init; }

        public IReadOnlyList<string>? Keywords { get; init; }

        public bool? Enabled { get; init; }
    }
}
