using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Configuration;
using PuddingRuntime.Services.Skills;

namespace PuddingRuntimeTests.Services;

/// <summary>
/// RSI-G4 任务书 §2.4-3 / I5② 的**零回归闸门**：关键词归一的唯一一份逻辑被搬到共享类型
/// （<c>PuddingCode.Skills.Retrieval.SkillKeywordNormalization</c>）之后，
/// <c>SkillEnforcerService</c> 的**关键词映射构建结果与注入结果必须逐字段不变**。
/// <para>
/// 为什么这组用例必须存在：抽出共享归一这件事，若"顺手"改变了注入结果，就等于**改了生产行为**，
/// 而那属于需要灰度与遥测的改动（本片明令不做）。没有这组用例，"我没有改变行为"只是一句声明。
/// </para>
/// <para>
/// 这组用例同时是 **D5（关键词归属裁决）的前置闸门**：D5 若改动了归属裁决，
/// 必须先解释为什么本组用例仍然全绿，或显式更新它们。
/// </para>
/// </summary>
[TestClass]
public sealed class SkillEnforcerKeywordMapTests
{
    [TestMethod]
    public async Task ExplicitKeyword_ShouldInjectItsOwnSkill()
    {
        using var temp = new TempDataRoot();
        var files = await CreateSkillsAsync(temp, ("skill-a", "Alpha Helper", ["alpha"]));
        var enforcer = new SkillEnforcerService(files, NullLogger<SkillEnforcerService>.Instance);

        var results = await enforcer.EnforceAsync("agent-1", "please use alpha now");

        Assert.IsNotNull(results);
        Assert.HasCount(1, results);
        Assert.AreEqual("skill-a", results[0].SkillId);
        Assert.IsFalse(string.IsNullOrWhiteSpace(results[0].MarkdownContent), "命中即注入正文，不得只返回 id。");
    }

    [TestMethod]
    public async Task NameFallback_ShouldInject_WhenNoExplicitKeywordIsDeclared()
    {
        using var temp = new TempDataRoot();
        var files = await CreateSkillsAsync(temp, ("skill-b", "Weekly Report", []));
        var enforcer = new SkillEnforcerService(files, NullLogger<SkillEnforcerService>.Instance);

        // 无显式 keywords：该技能仍能靠 Name（及其分词）被命中 —— 这是归一的兜底路径，必须原样保留。
        var byName = await enforcer.EnforceAsync("agent-1", "please build weekly report");
        Assert.IsNotNull(byName);
        Assert.AreEqual("skill-b", byName[0].SkillId);

        var byToken = await enforcer.EnforceAsync("agent-1", "weekly");
        Assert.IsNotNull(byToken, "Name 的分词片段也是关键词（长度 ≥ 2 的片段）。");
        Assert.AreEqual("skill-b", byToken[0].SkillId);
    }

    /// <remarks>
    /// ⚠️ <b>实测边界（2026-09-22）</b>：把 map 构建处的可用性过滤撤掉（改成 <c>kw is not null</c>）后
    /// 本用例**不会变红** —— manifest/index 层已经把空白关键词丢弃，空白键到不了 map。
    /// 故它是**安全网**而非该过滤的取红点；该过滤的牙在 <c>SkillKeywordNormalizationTests</c> 的
    /// <c>IsUsableKeyword_ShouldRejectNullEmptyAndWhitespace</c>。此处如实标注，防止后来者误以为这里在守它。
    /// </remarks>
    [TestMethod]
    public async Task WhitespaceOnlyKeyword_ShouldNotEnterTheMap()
    {
        using var temp = new TempDataRoot();
        var files = await CreateSkillsAsync(temp, ("skill-noise", "Noise", ["   "]));
        var enforcer = new SkillEnforcerService(files, NullLogger<SkillEnforcerService>.Instance);

        // 空白关键词若进入 map，会与**任何**消息匹配（空串是任意字符串的子串）⇒ 全量误注入。
        // 可用性过滤只在 map 构建处做一次（现由共享归一的 IsUsableKeyword 承担）。
        // ⚠️ 消息必须**包含**那段空白，否则本用例在可用性过滤被撤掉时也照样通过（假有牙）。
        Assert.IsNull(await enforcer.EnforceAsync("agent-1", "x   y"));
        Assert.IsNull(await enforcer.EnforceAsync("agent-1", "unrelated text"));

        // 消息里出现空白时同样不得命中（否则"空白即万能键"会以另一种形式复活）。
        Assert.IsNull(await enforcer.EnforceAsync("agent-1", "   "));
    }

    [TestMethod]
    public async Task SharedKeyword_ShouldResolveToOneSkillOnly_AndStayStable()
    {
        using var temp = new TempDataRoot();
        var files = await CreateSkillsAsync(
            temp,
            ("skill-a", "Alpha Helper", ["alpha", "shared"]),
            ("skill-b", "Beta Helper", ["beta", "shared"]));
        var enforcer = new SkillEnforcerService(files, NullLogger<SkillEnforcerService>.Instance);

        // ① 一个关键词只能有一个持有者（现状 = 索引顺序先到先得；被挤掉的技能静默失去这次注入机会）。
        var shared = await enforcer.EnforceAsync("agent-1", "please use shared now");
        Assert.IsNotNull(shared);
        Assert.HasCount(1, shared, "同一关键词被两个技能声明时，map 只可能有一个持有者。");

        var repeated = await enforcer.EnforceAsync("agent-1", "please use shared now");
        Assert.IsNotNull(repeated);
        Assert.AreEqual(shared[0].SkillId, repeated[0].SkillId, "持有者必须稳定，不得随缓存状态漂移。");

        // ② 被挤掉的只是**那一个关键词**，不是那个技能：它自己的独有关键词照样注入。
        var unique = await enforcer.EnforceAsync("agent-1", "please use beta now");
        Assert.IsNotNull(unique);
        Assert.AreEqual("skill-b", unique[0].SkillId);
    }

    // ───────────────────────── helpers（与 Services 下既有测试同形） ─────────────────────────

    private static async Task<AgentSkillFileService> CreateSkillsAsync(
        TempDataRoot temp,
        params (string SkillId, string Name, string[] Keywords)[] skills)
    {
        var files = new AgentSkillFileService(temp.Paths);
        foreach (var (skillId, name, keywords) in skills)
        {
            await files.CreateAsync("agent-1", new AgentSkillCreateRequest
            {
                SkillId = skillId,
                Name = name,
                Keywords = keywords,
                SkillMarkdown = "# Test Skill\n\ncontent-body",
            });
        }

        return files;
    }

    private sealed class TempDataRoot : IDisposable
    {
        public TempDataRoot()
        {
            Root = Path.Combine(Path.GetTempPath(), "pudding-skill-keyword-map-tests", Guid.NewGuid().ToString("N"));
            Paths = PuddingDataPaths.FromRoot(Root);
        }

        public string Root { get; }

        public PuddingDataPaths Paths { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }
}
