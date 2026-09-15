using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingCode.Abstractions;
using PuddingCode.Core;
using PuddingCode.Models;

namespace PuddingCoreTests;

/// <summary>
/// V5 切片二 T1：请求级视觉图片预算账本（VisualInputRequestBudget）合同测试。
/// 覆盖任务书 D1–D4/D6–D8；D5（budget=null 旧行为）由 LlmVisualInputPlannerTests 既有 13 个用例全绿证明；
/// D7 的全仓 grep 证据与 D8 的网关级代码引用见 temp/v5-slice2-t1-report.md。
/// </summary>
[TestClass]
public sealed class VisualInputRequestBudgetTests
{
    private const string Workspace = "default";

    // DataUri(1000)：base64 1000 字符 → 解码 750 字节；wire = "data:image/png;base64,"(22) + 1000 = 1022 UTF-8 字节。
    private static string DataUri(int base64Chars)
        => "data:image/png;base64," + new string('A', base64Chars);

    private static string Id(int index)
        => "vision-" + index.ToString("d32");

    private sealed class SizedResolver : IVisualArtifactResolver
    {
        public Task<VisualArtifactResolveResult?> ResolveAsync(
            string workspaceId,
            string artifactId,
            CancellationToken ct = default, string detail = PuddingCode.Models.VisionContentPartDetails.Original)
            => Task.FromResult<VisualArtifactResolveResult?>(
                new VisualArtifactResolveResult(artifactId, DataUri(1_000), "image/png"));
    }

    private static Task<VisualInputPlan> PlanOneBatchAsync(
        int startIndex,
        int count,
        VisionRequestPolicy? policy = null,
        VisualInputRequestBudget? budget = null,
        string? budgetSource = null)
        => LlmVisualInputPlanner.PlanAsync(
            Workspace,
            Enumerable.Range(startIndex, count).Select(i => new LlmImagePart(Id(i))).ToList(),
            new SizedResolver(),
            policy: policy,
            budget: budget,
            budgetSource: budgetSource);

    // ── D1：跨消息合计张数拦截（3+3+3=9 > 8，每批各自 ≤ 8）──
    [TestMethod]
    public async Task RequestScope_CrossMessageImageCount_ThirdBatchRejected()
    {
        var budget = new VisualInputRequestBudget();
        Assert.AreEqual(3, (await PlanOneBatchAsync(0, 3, budget: budget, budgetSource: "user input_image @message#1")).Images.Count);
        Assert.AreEqual(3, (await PlanOneBatchAsync(10, 3, budget: budget, budgetSource: "user input_image @message#2")).Images.Count);

        var ex = await Assert.ThrowsExactlyAsync<VisionPipelineException>(
            () => PlanOneBatchAsync(20, 3, budget: budget, budgetSource: "user input_image @message#3"));

        Assert.AreEqual(VisionErrorCodes.RequestLimitExceeded, ex.Code);
        StringAssert.Contains(ex.Message, "image count");
        StringAssert.Contains(ex.Message, "cumulative 8 + incoming 1");
        StringAssert.Contains(ex.Message, "policy limit 8");
        StringAssert.Contains(ex.Message, "user input_image @message#3");
    }

    // ── D2：跨消息合计解码字节拦截（每批 2250B ≤ 3000B，两批累计 3750B > 3000B）──
    [TestMethod]
    public async Task RequestScope_CrossMessageDecodedBytes_SecondBatchRejected()
    {
        var policy = new VisionRequestPolicy { InlineMaxTotalBytes = 3_000 };
        var budget = new VisualInputRequestBudget(policy);

        Assert.AreEqual(3, (await PlanOneBatchAsync(0, 3, policy, budget, "user input_image @message#1")).Images.Count);

        var ex = await Assert.ThrowsExactlyAsync<VisionPipelineException>(
            () => PlanOneBatchAsync(10, 3, policy, budget, "user input_image @message#2"));

        Assert.AreEqual(VisionErrorCodes.RequestLimitExceeded, ex.Code);
        StringAssert.Contains(ex.Message, "inline decoded bytes");
        // 批 2 第 1 份 2250+750=3000 ≤ 3000 合法通过；第 2 份 3000+750=3750 > 3000 才拦截（逐份 fail closed）。
        StringAssert.Contains(ex.Message, "cumulative 3000 + incoming 750");
        StringAssert.Contains(ex.Message, "policy limit 3000");
    }

    // ── D3：wire 字节维度独立生效（解码字节合计不超、wire 合计超）──
    [TestMethod]
    public async Task RequestScope_WireBytesDimension_TriggeredIndependently()
    {
        // 每批 3 张：解码 750×3=2250 ≤ 3000（decoded 不触发）；wire 1022×3=3066 > 2600（wire 触发）。
        var policy = new VisionRequestPolicy
        {
            InlineMaxTotalBytes = 3_000,
            InlineMaxTotalWireBytes = 2_600,
        };
        var budget = new VisualInputRequestBudget(policy);

        var ex = await Assert.ThrowsExactlyAsync<VisionPipelineException>(
            () => PlanOneBatchAsync(0, 3, policy, budget, "user input_image @message#1"));

        Assert.AreEqual(VisionErrorCodes.RequestLimitExceeded, ex.Code);
        StringAssert.Contains(ex.Message, "inline wire bytes");
        StringAssert.Contains(ex.Message, "cumulative 2044 + incoming 1022");
        StringAssert.Contains(ex.Message, "policy limit 2600");
    }

    // ── D4：token 维度（注入小阈值触发；默认 null 只累计不校验）──
    [TestMethod]
    public async Task RequestScope_TokenDimension_TriggeredWhenConfigured()
    {
        var policy = new VisionRequestPolicy
        {
            EstimatedTokensPerImageUpperBound = 3,
            EstimatedTokensPerRequestUpperBound = 4,
        };
        var budget = new VisualInputRequestBudget(policy);

        var ex = await Assert.ThrowsExactlyAsync<VisionPipelineException>(
            () => PlanOneBatchAsync(0, 2, policy, budget, "user input_image @message#1"));

        Assert.AreEqual(VisionErrorCodes.RequestLimitExceeded, ex.Code);
        StringAssert.Contains(ex.Message, "estimated tokens");
        StringAssert.Contains(ex.Message, "cumulative 3 + incoming 3");
        StringAssert.Contains(ex.Message, "policy limit 4");
    }

    [TestMethod]
    public async Task RequestScope_TokenAccumulation_ExposedOnPlanWhenUnderLimit()
    {
        var policy = new VisionRequestPolicy
        {
            EstimatedTokensPerImageUpperBound = 3,
            EstimatedTokensPerRequestUpperBound = 9,
        };
        var budget = new VisualInputRequestBudget(policy);

        var plan1 = await PlanOneBatchAsync(0, 2, policy, budget, "user input_image @message#1");
        var plan2 = await PlanOneBatchAsync(10, 1, policy, budget, "tool function_call_output @message#2");

        Assert.AreEqual(6, plan1.RequestScopedEstimatedTokens);
        Assert.AreEqual(9, plan2.RequestScopedEstimatedTokens);
        Assert.AreEqual(9, budget.EstimatedTokens);
        Assert.AreEqual("deepseek-2026-09-12-1024", plan2.ImageTokenEstimatorVersion);
    }

    // ── D6：384 不再是全模型上界（1024 + 版本标识）──
    [TestMethod]
    public void Default_EstimatedTokensPerImageUpperBound_Is1024WithParsableVersion()
    {
        Assert.AreEqual(1024, VisionRequestPolicy.Default.EstimatedTokensPerImageUpperBound);
        var version = VisionRequestPolicy.Default.ImageTokenEstimatorVersion;
        Assert.IsFalse(string.IsNullOrWhiteSpace(version));
        // 版本标识须含来源模型 + 日期 + 上界（当前 "deepseek-2026-09-12-1024"）。
        StringAssert.Contains(version, "1024");
        StringAssert.Contains(version, "2026-09-12");
    }

    // ── D7：超限 fail closed，绝不返回删减后的 plan；成功批次返回完整份数 ──
    [TestMethod]
    public async Task RequestScope_OverLimit_FailsClosedWithoutTrimmedPlan()
    {
        var budget = new VisualInputRequestBudget();
        var plan1 = await PlanOneBatchAsync(0, 3, budget: budget);
        var plan2 = await PlanOneBatchAsync(10, 3, budget: budget);
        Assert.AreEqual(3, plan1.Images.Count, "成功批次必须返回完整份数（不删减）");
        Assert.AreEqual(3, plan2.Images.Count);

        await Assert.ThrowsExactlyAsync<VisionPipelineException>(
            () => PlanOneBatchAsync(20, 3, budget: budget, budgetSource: "user input_image @message#3"));
    }

    // ── D8：用户图片与工具图片共用同一账本（planner 级证明；网关级代码引用见报告）──
    [TestMethod]
    public async Task RequestScope_UserAndToolImages_ShareSameBudgetLedger()
    {
        var budget = new VisualInputRequestBudget();
        Assert.AreEqual(3, (await PlanOneBatchAsync(0, 3, budget: budget, budgetSource: "user input_image @message#1")).Images.Count);
        Assert.AreEqual(3, (await PlanOneBatchAsync(10, 3, budget: budget, budgetSource: "tool function_call_output @message#2")).Images.Count);

        // 第 3 条消息的工具图片与前面的用户图片在同一账本聚合后越界。
        var ex = await Assert.ThrowsExactlyAsync<VisionPipelineException>(
            () => PlanOneBatchAsync(20, 3, budget: budget, budgetSource: "tool function_call_output @message#3"));

        StringAssert.Contains(ex.Message, "tool function_call_output @message#3");
        StringAssert.Contains(ex.Message, "image count");
    }
}
