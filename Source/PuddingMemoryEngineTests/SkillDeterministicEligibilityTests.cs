using PuddingCode.Platform;
using PuddingMemoryEngine.Services;

namespace PuddingMemoryEngineTests;

/// <summary>
/// G4 交付物 D8：**既有确定性闸门** <c>SkillEvolutionDeduplicationService.IsDeterministicallyEligible</c> 的取红用例。
/// <para>
/// <b>为什么必须有这个文件</b>：D8 的变异复核实测发现 —— 把该闸门的文本相似度门槛由 <c>0.20</c>
/// 改成 <c>0.60</c> 时，**既有用例全部保持绿**（变异 M-I8 的 NO-RED）。而 G4-D6 的可行性裁决
/// （"叠加既有闸门后仍通过的对数 = 0/0/0"）正建立在这条闸门的行为之上 ⇒ 闸门若无声，
/// 该裁决就是悬空的（这正是本会话反复出现的失败形态：结论跑在证据前面）。
/// </para>
/// <para>
/// <b>纪律</b>：每条用例都必须在闸门行为被改坏时**变红**；凡涉及相似度区间的用例，
/// 先在断言里**自证前提**（算出实际相似度并断言它落在预期区间），
/// 这样"用例没红"就不会被误读成"防线有效"。
/// </para>
/// <para>
/// 说明：本文件只覆盖闸门**自身**的判定语义（纯静态函数，无需仓储）。
/// "默认配置下注入结果逐字段不变"（I5②）与"家族标签不参与生死判定"（I9）的载体在别处。
/// </para>
/// </summary>
[TestClass]
public sealed class SkillDeterministicEligibilityTests
{
    [TestMethod]
    public void Eligibility_ShouldRequireNonEmptyAndIdenticalToolSets()
    {
        var withTools = Skill("a", "alpha beta", "gamma delta epsilon", "turn-1", ["http_get", "parse_json"]);

        Assert.IsFalse(
            SkillEvolutionDeduplicationService.IsDeterministicallyEligible(
                withTools,
                Skill("b", "alpha beta", "gamma delta epsilon", "turn-1", [])),
            "候选侧工具集合为空 ⇒ 不得判为可合并（空集与任何集合都 SetEquals，是经典静默坑）。");

        Assert.IsFalse(
            SkillEvolutionDeduplicationService.IsDeterministicallyEligible(
                withTools,
                Skill("b", "alpha beta", "gamma delta epsilon", "turn-1", ["http_get"])),
            "工具集合必须是**完全相等**，不是子集关系。");
    }

    [TestMethod]
    public void Eligibility_ShouldRejectLooseText_BelowTheFloor()
    {
        var canonical = Skill("a", "alpha beta", "gamma delta epsilon", "turn-1", ["http_get", "parse_json"]);
        var duplicate = Skill("b", "theta iota", "kappa lambda sigma", "turn-1", ["http_get", "parse_json"]);

        var similarity = Similarity(canonical, duplicate);
        Assert.IsTrue(similarity < 0.20, $"前提不成立：相似度应为 < 0.20，实际 {similarity}（用例断言的不是门槛就不算门槛用例）。");

        Assert.IsFalse(
            SkillEvolutionDeduplicationService.IsDeterministicallyEligible(canonical, duplicate),
            "文本相似度低于 0.20 ⇒ 即使共享来源 turn 也不得判为可合并。");
    }

    [TestMethod]
    public void Eligibility_ShouldAcceptBySharedSourceTurn_AtLooseSimilarity()
    {
        // ⭐ 核心用例（D8 变异 M-I8 的取红载体）。相似度落在 [0.20, 0.35) ⇒
        // 仅靠文本不达标，但**共享来源 turn** 兜底使之可合并。
        // 若把门槛 0.20 改成 0.60（即"文本必须自身达标"），本条必红。
        var canonical = Skill("a", "alpha beta", "gamma delta epsilon", "turn-shared", ["http_get", "parse_json"]);
        var duplicate = Skill("b", "alpha beta", "theta iota kappa", "turn-shared", ["http_get", "parse_json"]);

        var similarity = Similarity(canonical, duplicate);
        Assert.IsTrue(similarity >= 0.20 && similarity < 0.35, $"前提不成立：相似度应为 [0.20, 0.35)，实际 {similarity}。");

        Assert.IsTrue(
            SkillEvolutionDeduplicationService.IsDeterministicallyEligible(canonical, duplicate),
            "相似度 ≥ 0.20 且共享来源 turn ⇒ 可合并（低门槛由 turn 共享兜底，不是靠文本）。");
    }

    [TestMethod]
    public void Eligibility_ShouldRequireHighSimilarity_WhenNoSharedTurn()
    {
        var canonical = Skill("a", "alpha beta", "gamma delta epsilon", "turn-1", ["http_get", "parse_json"]);
        var loose = Skill("b", "alpha beta", "theta iota kappa", "turn-2", ["http_get", "parse_json"]);
        var identical = Skill("c", "alpha beta", "gamma delta epsilon", "turn-3", ["http_get", "parse_json"]);

        var looseSimilarity = Similarity(canonical, loose);
        Assert.IsTrue(looseSimilarity >= 0.20 && looseSimilarity < 0.35, $"前提不成立：相似度应为 [0.20, 0.35)，实际 {looseSimilarity}。");

        Assert.IsFalse(
            SkillEvolutionDeduplicationService.IsDeterministicallyEligible(canonical, loose),
            "无共享来源 turn 且文本相似度 < 0.35 ⇒ 不可合并。");

        Assert.IsTrue(
            SkillEvolutionDeduplicationService.IsDeterministicallyEligible(canonical, identical),
            "文本几乎相同 ⇒ 无共享 turn 也可合并。");
    }

    /// <summary>
    /// 闸门实际使用的相似度口径（元数据文本 = 名称 + 描述）。
    /// 复用被闸门调用的**同一个**公有实现，⛔ 不另算一份。
    /// </summary>
    private static double Similarity(AgentSkillEvolutionDocument left, AgentSkillEvolutionDocument right)
        => SkillEvolutionDeduplicationService.CalculateTextSimilarity(
            $"{left.Name} {left.Description}",
            $"{right.Name} {right.Description}");

    private static AgentSkillEvolutionDocument Skill(
        string id,
        string name,
        string description,
        string turnId,
        IReadOnlyList<string> tools) => new()
    {
        SkillId = id,
        Name = name,
        Version = "1.0.0",
        Description = description,
        Tags = ["auto-generated", $"source-turn:{turnId}"],
        Keywords = tools,
        Enabled = true,
        Markdown = $"---\nversion: 1.0.0\n---\n\n# {name}\n\n- Turn: {turnId}\n",
    };
}
