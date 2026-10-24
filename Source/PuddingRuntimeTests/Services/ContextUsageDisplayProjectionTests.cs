using PuddingCode.Runtime;
using PuddingRuntime.Services;

namespace PuddingRuntimeTests.Services;

/// <summary>
/// 上下文用量显示口径的回归：**面板与实际压缩判定必须是同一个数**。
/// </summary>
/// <remarks>
/// 用户裁定 2026-10-24：前端指示器与后端估算共用同一套参数/函数，杜绝两头各算一套。
/// 因此这些用例针对的不是「数字好不好看」，而是「显示的分母/分子能不能漂离门禁」。
/// </remarks>
[TestClass]
public sealed class ContextUsageDisplayProjectionTests
{
    /// <summary>
    /// 面板曾经用模型窗口（1,000,000）当分母，于是 300,000 显示 30.0%，
    /// 而门禁按有效窗口 616,000 判为 48.7%。现在必须取后者。
    /// </summary>
    [TestMethod]
    public void Project_UsesEffectiveWindowAsDenominator_NeverModelWindow()
    {
        var health = new ContextHealthEvaluator().Evaluate(
            sessionId: "session-1",
            usedTokens: 300_000,
            contextWindowTokens: 1_000_000,
            maxOutputTokens: 384_000);

        Assert.AreEqual(616_000, health.EffectiveWindowTokens);
        Assert.AreEqual(300_000.0 / 616_000.0 * 100d, health.Display.Percent, 1e-9);
        Assert.AreEqual(616_000, health.Display.DenominatorTokens);
        Assert.AreEqual(300_000, health.Display.NumeratorTokens);
        // 30.0% 是模型窗口口径 —— 出现它就意味着分母又退回去了。
        Assert.AreNotEqual(30d, health.Display.Percent, 0.5d);
    }

    /// <summary>
    /// 反漂移断言：显示百分比与门禁比率必须逐位相等（同一分子、同一分母）。
    /// 任何一方换分母，这条会立刻红。
    /// </summary>
    [TestMethod]
    public void Project_PercentEqualsGateRatio_ForTheSameSnapshot()
    {
        var health = new ContextHealthEvaluator().Evaluate(
            sessionId: "session-1",
            usedTokens: 300_000,
            contextWindowTokens: 1_000_000,
            maxOutputTokens: 384_000,
            safetyBufferTokens: 9_216,
            compactionThreshold: 0.80);

        Assert.AreEqual(606_784, health.EffectiveWindowTokens);
        Assert.AreEqual(300_000.0 / 606_784.0 * 100d, health.Display.Percent, 1e-9);
        Assert.AreEqual(health.GateRatio * 100d, health.Display.Percent, 1e-9);
    }

    /// <summary>
    /// 超过有效窗口时门禁比率可以是 1.0041（2026-09-22 事故实测），显示侧要饱和在 100
    /// （环不能画到 100% 以外），但**不能**把饱和后的值再当门禁比率用 —— 原始值在 GateRatio 里。
    /// </summary>
    [TestMethod]
    public void Project_SaturatesAboveOne_ButRawGateRatioStaysOnSnapshot()
    {
        var health = new ContextHealthEvaluator().Evaluate(
            sessionId: "session-1",
            usedTokens: 609_305,
            contextWindowTokens: 1_000_000,
            maxOutputTokens: 384_000,
            safetyBufferTokens: 9_216,
            compactionThreshold: 0.80);

        Assert.IsTrue(health.GateRatio > 1.0);
        Assert.AreEqual(100d, health.Display.Percent, 1e-9);
        Assert.AreEqual(0d, health.Display.RemainingPercent, 1e-9);
        // 饱和只发生在显示侧：分子保留原始估算，便于诊断。
        Assert.AreEqual(609_305, health.Display.NumeratorTokens);
    }

    /// <summary>
    /// 实测只作次级参考：它既不能替换主指标分子，也不能用来反推门禁。
    /// </summary>
    [TestMethod]
    public void Project_MeasuredIsSecondary_AndNeverDrivesPrimaryPercent()
    {
        var snapshot = new ContextHealthSnapshot(
            "session-1",
            UsedTokens: 300_000,
            ContextWindowTokens: 1_000_000,
            EffectiveWindowTokens: 616_000,
            RemainingTokens: 316_000,
            UsageRatio: 0.30,
            State: ContextHealthState.Warning,
            ShouldSuggestCompact: false,
            ShouldAutoCompact: false,
            ShouldBlockSend: false,
            GateRatio: 300_000.0 / 616_000.0)
        {
            LastMeasuredPromptTokens = 131_100,
        };

        var display = snapshot.Display;

        Assert.AreEqual(300_000.0 / 616_000.0 * 100d, display.Percent, 1e-9);
        Assert.AreEqual(131_100, display.MeasuredNumeratorTokens);
        Assert.AreEqual(131_100.0 / 616_000.0 * 100d, display.MeasuredPercent!.Value, 1e-9);
    }

    [TestMethod]
    public void Project_WithoutMeasured_YieldsNullMeasuredPercent()
    {
        var display = new ContextHealthEvaluator()
            .Evaluate("session-1", 100_000, 1_000_000, maxOutputTokens: 384_000)
            .Display;

        Assert.IsNull(display.MeasuredNumeratorTokens);
        Assert.IsNull(display.MeasuredPercent);
    }

    /// <summary>
    /// 「不可用于输入」的分母是**模型窗口**（它讲的是整个窗口里有多少不可用），
    /// 而主指标分母是有效窗口 —— 这两个百分比**不能相加**，前端也不得再自行换算。
    /// </summary>
    [TestMethod]
    public void Project_InputUnavailableIsRelativeToModelWindow()
    {
        var display = new ContextHealthEvaluator()
            .Evaluate("session-1", 100_000, 1_000_000, maxOutputTokens: 384_000)
            .Display;

        Assert.AreEqual(1_000_000 - 616_000, display.InputUnavailableTokens);
        Assert.AreEqual(38.4d, display.InputUnavailablePercent, 1e-9);
    }

    /// <summary>分段之和必须等于标题百分比，否则图例又会和标题打架。</summary>
    [TestMethod]
    public void Project_SegmentsScaleToHeadlinePercent()
    {
        var snapshot = new ContextHealthSnapshot(
            "session-1",
            UsedTokens: 300_000,
            ContextWindowTokens: 1_000_000,
            EffectiveWindowTokens: 616_000,
            RemainingTokens: 316_000,
            UsageRatio: 0.30,
            State: ContextHealthState.Warning,
            ShouldSuggestCompact: false,
            ShouldAutoCompact: false,
            ShouldBlockSend: false)
        {
            SystemPromptTokens = 10_000,
            ToolDefinitionTokens = 20_000,
            CompactionSummaryTokens = 1_000,
            ConversationTokens = 40_000,
            ReasoningTokens = 4_000,
            ToolResultTokens = 25_000,
        };

        var display = snapshot.Display;

        Assert.AreEqual(6, display.Segments.Count);
        var totalTokens = display.Segments.Sum(segment => segment.Tokens);
        var totalPercent = display.Segments.Sum(segment => segment.Percent);
        // 桶合计 100,000 被等比放大到 300,000（已使用的口径）。
        Assert.AreEqual(display.NumeratorTokens, totalTokens);
        Assert.AreEqual(display.Percent, totalPercent, 0.01d);
        Assert.IsTrue(display.Segments.All(segment => segment.Percent > 0));
    }

    [TestMethod]
    public void Project_WithoutBuckets_YieldsEmptySegments()
    {
        var display = new ContextHealthEvaluator()
            .Evaluate("session-1", 100_000, 1_000_000, maxOutputTokens: 384_000)
            .Display;

        Assert.AreEqual(0, display.Segments.Count);
    }

    /// <summary>
    /// 手工构造的退化快照（有效窗口 0）不得让投影抛异常或产出 NaN：
    /// 这些快照来自历史数据/测试替身，除零会一路流到 UI 变成 "NaN%"。
    /// </summary>
    [TestMethod]
    public void Project_DegenerateSnapshot_DoesNotDivideByZero()
    {
        var snapshot = new ContextHealthSnapshot(
            "session-1",
            UsedTokens: 10,
            ContextWindowTokens: 0,
            EffectiveWindowTokens: 0,
            RemainingTokens: 0,
            UsageRatio: 0,
            State: ContextHealthState.Healthy,
            ShouldSuggestCompact: false,
            ShouldAutoCompact: false,
            ShouldBlockSend: false);

        var display = snapshot.Display;

        Assert.AreEqual(1, display.DenominatorTokens);
        // 10 tokens / 有效窗口 1 ⇒ 原始比率 1000%，显示侧必须饱和在 100。
        Assert.AreEqual(100d, display.Percent, 1e-9);
        Assert.IsFalse(double.IsNaN(display.Percent));
        Assert.IsFalse(double.IsNaN(display.InputUnavailablePercent));
    }

    /// <summary>
    /// 压缩判定必须按**有效输入窗口**算比率，与面板同一分母。
    /// 判别点：模型窗口 1,000,000 / 有效窗口 935,000 / 比率 0.65。
    /// 620,000 在模型窗口口径下未达标（&lt; 650,000），在有效窗口口径下达标（≥ 607,750）。
    /// </summary>
    [TestMethod]
    public void ExceedsAutoCompactRatio_UsesEffectiveWindow_NotModelWindow()
    {
        var snapshot = new ContextHealthSnapshot(
            "session-1",
            UsedTokens: 620_000,
            ContextWindowTokens: 1_000_000,
            EffectiveWindowTokens: 935_000,
            RemainingTokens: 315_000,
            UsageRatio: 0.62,
            State: ContextHealthState.Warning,
            ShouldSuggestCompact: true,
            ShouldAutoCompact: false,
            ShouldBlockSend: false);

        Assert.IsTrue(snapshot.ExceedsAutoCompactRatio(0.65));
    }

    [TestMethod]
    public void ExceedsAutoCompactRatio_BoundaryIsInclusive()
    {
        var snapshot = new ContextHealthSnapshot(
            "session-1",
            UsedTokens: 607_750,
            ContextWindowTokens: 1_000_000,
            EffectiveWindowTokens: 935_000,
            RemainingTokens: 327_250,
            UsageRatio: 0.60775,
            State: ContextHealthState.Warning,
            ShouldSuggestCompact: true,
            ShouldAutoCompact: false,
            ShouldBlockSend: false);

        Assert.IsTrue(snapshot.ExceedsAutoCompactRatio(0.65));
        Assert.IsFalse((snapshot with { UsedTokens = 607_749 }).ExceedsAutoCompactRatio(0.65));
    }

    /// <summary>
    /// 接口契约层的最后一道关：`Display` 是**只有 getter 的派生属性**，
    /// 若序列化器不输出它，前面所有断言都只在内存里成立，前端仍然拿不到。
    /// 这里用 ASP.NET Core 的默认 Web 选项（camelCase）序列化一次快照，
    /// 断言 `display.percent` / `display.denominatorTokens` 真的在 JSON 里。
    /// </summary>
    [TestMethod]
    public void Display_IsSerializedInTheApiShape()
    {
        var health = new ContextHealthEvaluator().Evaluate(
            "session-1", 300_000, 1_000_000, maxOutputTokens: 384_000);

        var json = System.Text.Json.JsonSerializer.Serialize(
            health,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        StringAssert.Contains(json, "\"display\":");
        StringAssert.Contains(json, "\"percent\":");
        StringAssert.Contains(json, "\"denominatorTokens\":616000");
        StringAssert.Contains(json, "\"remainingPercent\":");
    }
}
