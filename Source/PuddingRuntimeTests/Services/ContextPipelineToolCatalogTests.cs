using PuddingCode.Platform;
using PuddingCode.Tools;
using PuddingRuntime.Services;

namespace PuddingRuntimeTests.Services;

/// <summary>
/// L1/L9 工具目录必须与实际 schema 投递面同口径（ADR-081）。
/// <para>
/// 回归对象（2026-11-20 实测）：目录曾只用 <c>ListAvailable(policy, workspaceId)</c> 重载，
/// 而 host-catalog 工具源（pudding.shell / pudding.files 等 host-service 包）只在
/// <c>PuddingToolRegistry.BuildToolSnapshot</c> 收到非空 <see cref="ToolExecutionContext"/> 时才并入
/// （PuddingToolRegistry.cs:49-56）。结果是目录报 6 个工具、实际可用 40+，
/// 模型以为工具不存在而反复 search_tools 试探（diagnose 记录连续 8 次）。
/// </para>
/// </summary>
[TestClass]
public sealed class ContextPipelineToolCatalogTests
{
    /// <summary>
    /// 记录真实收到的执行上下文。
    /// <para>
    /// 必须显式覆盖 context 重载：<see cref="IPuddingToolRegistry"/> 的默认实现会退化成
    /// <c>ListAvailable(policy, context.WorkspaceId)</c>（PuddingToolContracts.cs:359），
    /// 若依赖默认实现，本夹具就测不出「调用方丢 context」这一原始缺陷。
    /// </para>
    /// </summary>
    private sealed class RecordingToolRegistry : IPuddingToolRegistry
    {
        public ToolExecutionContext? LastContext { get; private set; }
        public string? LastWorkspaceId { get; private set; }

        public IPuddingTool? GetTool(string toolId) => null;
        public ToolDescriptor? GetDescriptor(string toolId) => null;
        public IReadOnlyList<ToolDescriptor> ListDescriptors() => [];
        public IReadOnlyList<ToolDescriptor> ListAvailable(CapabilityPolicy? policy) => [];

        public IReadOnlyList<ToolDescriptor> ListAvailable(CapabilityPolicy? policy, string workspaceId)
        {
            LastWorkspaceId = workspaceId;
            return [];
        }

        public IReadOnlyList<ToolDescriptor> ListAvailable(CapabilityPolicy? policy, ToolExecutionContext context)
        {
            LastContext = context;
            return [];
        }
    }

    private static ContextRequest CreateRequest(CapabilityPolicy? capability = null) => new()
    {
        WorkspaceId = "ws-catalog",
        SessionId = "sess-catalog",
        AgentInstanceId = "agent-catalog",
        ConfigurationAgentInstanceId = "agent-config",
        WorkingDirectory = @"C:\work",
        AgentTemplateId = "tpl-catalog",
        Capability = capability,
    };

    [TestMethod]
    public void ResolveToolDescriptors_PassesExecutionContext()
    {
        var registry = new RecordingToolRegistry();
        var capability = new CapabilityPolicy { AllowShellExecution = true };

        ContextPipeline.ResolveToolDescriptors(registry, CreateRequest(capability));

        Assert.IsNotNull(registry.LastContext,
            "目录查询必须携带 ToolExecutionContext：只有非空 context 才会并入 host-catalog 工具源");
        Assert.AreEqual("ws-catalog", registry.LastContext!.WorkspaceId);
        Assert.AreEqual("sess-catalog", registry.LastContext.SessionId);
        Assert.AreEqual("agent-catalog", registry.LastContext.AgentInstanceId);
        Assert.AreEqual("agent-config", registry.LastContext.ConfigurationAgentInstanceId);
        Assert.AreEqual(@"C:\work", registry.LastContext.WorkingDirectory);
        Assert.AreEqual("tpl-catalog", registry.LastContext.AgentTemplateId);
        Assert.AreSame(capability, registry.LastContext.CapabilityPolicy,
            "能力策略必须原样透传，目录只能据此缩小可发现范围");
        Assert.IsNull(registry.LastWorkspaceId,
            "不得退化到 (policy, workspaceId) 重载——那会丢掉 host-catalog 工具源");
    }

    /// <summary>
    /// 能力策略缺失是正常输入（未评估状态），必须仍能构造出可用上下文；
    /// host-catalog 侧会因此返回空集，但 native/toolSource 工具仍可见，不得抛异常。
    /// </summary>
    [TestMethod]
    public void CreateToolContext_NullCapability_StillReturnsUsableContext()
    {
        var context = ContextPipeline.CreateToolContext(CreateRequest());

        Assert.IsNotNull(context);
        Assert.IsNull(context.CapabilityPolicy);
        Assert.AreEqual("ws-catalog", context.WorkspaceId);
        Assert.AreEqual("sess-catalog", context.SessionId);
        Assert.AreEqual("agent-catalog", context.AgentInstanceId);
    }

    [TestMethod]
    public void ResolveToolDescriptors_NullRegistry_ReturnsEmpty()
    {
        var result = ContextPipeline.ResolveToolDescriptors(null, CreateRequest());

        Assert.AreEqual(0, result.Count);
    }
}
