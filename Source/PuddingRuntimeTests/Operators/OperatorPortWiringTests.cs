using Microsoft.Extensions.DependencyInjection;
using PuddingCode.Classification;
using PuddingCode.Operators;
using PuddingRuntime.Operators.Adapters;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Operators;

/// <summary>
/// S1b 接线测试：证明新算子端口在<b>生产组合</b>里真的被消费（不是只存在于源码里的惰性抽象）。
/// <para>
/// 断言的是一条「零行为变更」链路：DI 里的注册表 → 既有工具审批分类器（<b>未改一行</b>）
/// → 适配器 → 新端口。任何一环改动了既有裁决，本类立刻变红。
/// </para>
/// </summary>
[TestClass]
public sealed class OperatorPortWiringTests
{
    [TestMethod]
    public void Wiring_ProductionRegistration_ResolvesApprovalSceneThroughNewPort()
    {
        var services = new ServiceCollection();
        services.AddPuddingToolRegistry();

        using var provider = services.BuildServiceProvider();

        var registry = provider.GetRequiredService<IOperatorRegistry>();
        CollectionAssert.AreEqual(
            new[] { ToolApprovalOperatorAdapter.SceneKeyValue },
            registry.SceneKeys.ToArray(),
            "生产组合必须登记工具审批场景（且此刻只有这一个场景）。");

        var viaPort = registry.Resolve<IClassifier>(ToolApprovalOperatorAdapter.SceneKeyValue);
        Assert.IsInstanceOfType<ToolApprovalOperatorAdapter>(viaPort, "该场景必须解析出适配器。");

        var wrapped = provider.GetRequiredService<IToolCallClassifier>();
        Assert.AreEqual(wrapped.ClassifierId, viaPort.OperatorId, "端口算子身份必须取自被包装分类器（不得自造新身份）。");

        var adapter = provider.GetRequiredService<ToolApprovalOperatorAdapter>();
        Assert.AreSame(viaPort, adapter, "注册表登记的必须是同一个适配器实例（不克隆、不重造）。");
    }

    [TestMethod]
    public async Task Wiring_PortResult_IsEquivalentToDirectPipelineCall()
    {
        var services = new ServiceCollection();
        services.AddPuddingToolRegistry();

        using var provider = services.BuildServiceProvider();

        var pipeline = provider.GetRequiredService<IToolCallClassifier>();
        var viaPort = provider.GetRequiredService<IOperatorRegistry>().Resolve<IClassifier>(ToolApprovalOperatorAdapter.SceneKeyValue);

        var context = ToolApprovalAdapterTestData.Context(command: "dotnet test", arguments: """{"command":"dotnet test"}""");

        // 直接调用既有管线（无 adapter） vs 经新端口的适配器路径
        var direct = await pipeline.ClassifyAsync(context);
        var mapped = await viaPort.ClassifyAsync(ToolApprovalOperatorContext.Create(context));

        Assert.AreEqual(
            ToolApprovalOperatorAdapter.NormalizeLabel(direct.Outcome),
            mapped.PrimaryLabel,
            "经端口的标签必须是既有结论的规范化键（逐条等价，不得漂移）。");
        Assert.AreEqual(direct.Reason, mapped.Envelope.Reason, "理由必须逐位相等。");
        Assert.AreEqual(direct.ReasonCode, mapped.Envelope.ReasonCode, "原因码必须逐位相等。");
        Assert.AreEqual(direct.ClassifierId, mapped.Envelope.OperatorId, "算子 id 必须逐位相等。");
        Assert.AreEqual(direct.ClassifierModel, mapped.Envelope.ModelId, "模型 id 必须逐位相等。");

        // 既有裁决的分布必须被原样承载（null ⇒ 空字典，不得伪造）
        if (direct.PerOutcomeConfidence is null)
        {
            Assert.IsEmpty(mapped.Distribution);
        }
        else
        {
            Assert.AreEqual(direct.PerOutcomeConfidence.Count, mapped.Distribution.Count);
            foreach (var pair in direct.PerOutcomeConfidence)
            {
                Assert.AreEqual(pair.Value, mapped.Distribution[pair.Key], $"分布键 {pair.Key} 的值必须相等。");
            }
        }

        // 审批裁决的分数与阈值一律留空（本切片不引入阈值策略）
        Assert.IsNull(mapped.Envelope.Score);
        Assert.IsNull(mapped.Envelope.Threshold);
    }

    [TestMethod]
    public void Wiring_UnregisteredScene_FailsClosed()
    {
        var services = new ServiceCollection();
        services.AddPuddingToolRegistry();

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IOperatorRegistry>();

        Assert.IsFalse(registry.TryResolve<IClassifier>("scene-not-registered", out _));
        Assert.ThrowsExactly<InvalidOperationException>(() => registry.Resolve<IClassifier>("scene-not-registered"));
        Assert.ThrowsExactly<InvalidOperationException>(() => registry.Resolve<IJudge>(ToolApprovalOperatorAdapter.SceneKeyValue));
    }
}
