using PuddingCode.Configuration;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingRuntime.Services;

namespace PuddingRuntimeTests.Services;

/// <summary>
/// 2026-11-25：有效输入上限的推导分量。真实数据里 <c>sub_agent_runs</c> id=1030 报
/// <c>estimated=18154, limit=1</c> —— 18k 输入本身不大，是 limit 被夹到 1 了
/// （模型窗口小于自己的预留输出 + 安全缓冲）。旧文案把这种情形说成「请求太大」，
/// 会把排查引向裁历史这条错路。本组用例锁住两件事：
/// ① 失败路径必须带出推导分量；② 只有结构矛盾确实成立时才下「该改配置」的结论。
/// </summary>
[TestClass]
public sealed class LlmRequestBudgetGuardLimitDerivationTests
{
    private static string BigPayload() => string.Concat(Enumerable.Repeat("payload-token ", 20_000));

    private static IReadOnlyList<ChatMessage> SmallConversation() => new List<ChatMessage>
    {
        new(ChatRole.System, "system prompt"),
        new(ChatRole.User, "current turn user payload"),
    };

    [TestMethod]
    public void Prepare_DegenerateWindow_ReportsDerivationAndPointsAtConfig()
    {
        // 窗口 2_000 < 预留输出 1_000 + 缓冲 1_024 ⇒ 推导值被夹到 1。
        // 这正是 id=1030 那条失败的形状：不是历史太大，是配置自相矛盾。
        var config = new LlmConfig
        {
            ModelId = "degenerate-window-model",
            MaxContextTokens = 2_000,
            MaxOutputTokens = 1_000,
        };

        var ex = Assert.ThrowsExactly<LlmInputBudgetExceededException>(() => LlmRequestBudgetGuard.Prepare(
            new ContextUsageSnapshotStore(),
            "degenerate-window-session",
            SmallConversation(),
            tools: null,
            config));

        Assert.AreEqual(1, ex.EffectiveInputLimit);
        Assert.IsNotNull(ex.LimitDerivation);
        Assert.AreEqual(2_000, ex.LimitDerivation!.ContextWindow);
        Assert.AreEqual(1, ex.LimitDerivation.DerivedContextLimit);
        Assert.IsTrue(
            ex.LimitDerivation.WindowCannotCoverReservedOutput,
            "窗口 2000 < 预留输出 1000 + 缓冲 1024，结构矛盾判据必须成立");

        StringAssert.Contains(ex.Message, "limitDerivation:");
        StringAssert.Contains(ex.Message, "contextWindow=2000");
        StringAssert.Contains(ex.Message, "derivedContextLimit=1");
        StringAssert.Contains(ex.Message, "cannot accommodate its own reserved output");
        StringAssert.Contains(ex.Message, "fix the model configuration");
    }

    [TestMethod]
    public void Prepare_HealthyWindow_ReportsDerivationWithoutConfigVerdict()
    {
        // 同一份输入在正常窗口下：limit 是 27_976，超限是因为历史真的太大。
        // 此时**不许**出现「该改配置」的结论 —— 那会把运维引到错方向。
        var config = new LlmConfig
        {
            ModelId = "healthy-window-model",
            MaxContextTokens = 30_000,
            MaxOutputTokens = 1_000,
        };
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, BigPayload()),
            new(ChatRole.User, "current turn user payload"),
        };

        var ex = Assert.ThrowsExactly<LlmInputBudgetExceededException>(() => LlmRequestBudgetGuard.Prepare(
            new ContextUsageSnapshotStore(),
            "healthy-window-session",
            messages,
            tools: null,
            config));

        Assert.AreEqual(27_976, ex.EffectiveInputLimit);
        Assert.IsNotNull(ex.LimitDerivation);
        Assert.IsFalse(
            ex.LimitDerivation!.WindowCannotCoverReservedOutput,
            "窗口 30000 远大于预留输出 + 缓冲，不得报结构矛盾");

        StringAssert.Contains(ex.Message, "limitDerivation:");
        StringAssert.Contains(ex.Message, "contextWindow=30000");
        StringAssert.Contains(ex.Message, "derivedContextLimit=27976");
        Assert.IsFalse(
            ex.Message.Contains("cannot accommodate its own reserved output", StringComparison.Ordinal),
            "正常窗口下不得出现「改配置」结论");
    }

    [TestMethod]
    public void Prepare_NoContextWindow_ReportsUnsetContextWindow()
    {
        // 未登记窗口 ⇒ 不做窗口推导，上限只由 provider 输入上限决定。
        // 文案必须说明「未登记」，而不是编一个推导值出来。
        var config = new LlmConfig
        {
            ModelId = "no-window-model",
            MaxInputTokens = 100,
        };

        var ex = Assert.ThrowsExactly<LlmInputBudgetExceededException>(() => LlmRequestBudgetGuard.Prepare(
            new ContextUsageSnapshotStore(),
            "no-window-session",
            // 必须真的超限：provider 上限只有 100 tokens，而这段 User 载荷远大于它。
            // （先前用两行短对话配 100 上限其实不超限，用例压根进不到抛错分支。）
            new List<ChatMessage>
            {
                new(ChatRole.System, "system prompt"),
                new(ChatRole.User, BigPayload()),
            },
            tools: null,
            config));

        Assert.AreEqual(100, ex.EffectiveInputLimit);
        Assert.IsNotNull(ex.LimitDerivation);
        Assert.IsNull(ex.LimitDerivation!.ContextWindow);
        Assert.IsNull(ex.LimitDerivation.DerivedContextLimit);
        Assert.IsFalse(ex.LimitDerivation.WindowCannotCoverReservedOutput);

        StringAssert.Contains(ex.Message, "contextWindow=<unset>");
        StringAssert.Contains(ex.Message, "derivedContextLimit=<unset>");
        StringAssert.Contains(ex.Message, "providerLimit=100");
    }

    [TestMethod]
    public void WindowCannotCoverReservedOutput_BoundaryIsExactlyOneTokenOfRoom()
    {
        // 边界：窗口 = 预留输出 + 缓冲 + 1 ⇒ 输入恰好剩 1 token，不算结构矛盾；
        // 再少 1 token（剩 0）才算。这条锁的是判据方向，防止把「恰好够」误判成矛盾。
        // 判据真源与配置加载期共用 LlmTokenBudgetDefaults.LeavesNoInputRoom，
        // 所以这里也兼作「两边共用同一个边界」的回归位。
        var buffer = LlmTokenBudgetDefaults.SafetyBufferTokens;
        const int Reserved = 1_000;

        var exactlyOne = new LlmInputLimitDerivation(
            ContextWindow: Reserved + buffer + 1,
            ReservedOutput: Reserved,
            SafetyBuffer: buffer,
            ProviderLimit: int.MaxValue,
            DerivedContextLimit: 1);
        Assert.IsFalse(
            exactlyOne.WindowCannotCoverReservedOutput,
            "输入恰好剩 1 token 时不得判为结构矛盾");

        var oneShort = new LlmInputLimitDerivation(
            ContextWindow: Reserved + buffer,
            ReservedOutput: Reserved,
            SafetyBuffer: buffer,
            ProviderLimit: int.MaxValue,
            DerivedContextLimit: 1);
        Assert.IsTrue(
            oneShort.WindowCannotCoverReservedOutput,
            "输入剩 0 token 时必须判为结构矛盾");
    }

    [TestMethod]
    public void ResolveEffectiveInputLimit_AgreesWithDerivationEntryPoint()
    {
        // 两个入口必须是**同一算法**：失败路径若自己重算一遍，
        // 报出来的数字就可能与真正生效的那个不一致 —— 而那正是要诊断的东西。
        var configs = new LlmConfig?[]
        {
            null,
            new() { ModelId = "m1", MaxContextTokens = 30_000, MaxOutputTokens = 1_000 },
            new() { ModelId = "m2", MaxContextTokens = 2_000, MaxOutputTokens = 1_000 },
            new() { ModelId = "m3", MaxInputTokens = 4_096 },
            new() { ModelId = "m4", MaxContextTokens = 128_000, MaxOutputTokens = 8_192, MaxInputTokens = 60_000 },
        };

        foreach (var config in configs)
        {
            foreach (var buffer in new[] { 0, 1_024 })
            {
                var viaEntry = LlmRequestBudgetGuard.ResolveEffectiveInputLimit(config, buffer);
                var viaDerivation = LlmRequestBudgetGuard.ResolveEffectiveInputLimitWithDerivation(config, buffer);
                Assert.AreEqual(
                    viaEntry,
                    viaDerivation.Limit,
                    $"model={config?.ModelId ?? "<null>"} buffer={buffer}");
            }
        }
    }
}
