using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Tools;
using PuddingRuntime.Services.Skills;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Tools;

/// <summary>
/// P0-4c regression fixtures：验证传给 LLM 的 <see cref="LlmToolDefinition"/> 数组按
/// Name（= ToolId / SkillId）以 <see cref="StringComparer.OrdinalIgnoreCase"/> 稳定排序，
/// 使同一 composition 下 tools 序列化字节确定，消除 toolSpecHash 前缀漂移。
/// </summary>
[TestClass]
public sealed class ToolSchemaStableOrderTests
{
    // ── PuddingToolSchemaService.BuildLlmTools ──────────────

    [TestMethod]
    public void PuddingToolSchemaService_BuildLlmTools_OrdersByName_OrdinalIgnoreCase()
    {
        // 固定构造顺序故意乱序（含大小写差异），验证排序按 OrdinalIgnoreCase 归一。
        var registry = new FixedOrderToolRegistry(
        [
            Descriptor("zebra_tool"),
            Descriptor("Mike_tool"),
            Descriptor("delta_tool"),
            Descriptor("alpha_tool"),
        ]);

        var schema = new PuddingToolSchemaService(registry);
        var tools = schema.BuildLlmTools(null);

        CollectionAssert.AreEqual(
            new[] { "alpha_tool", "delta_tool", "Mike_tool", "zebra_tool" },
            tools.Select(t => t.Name).ToArray());
    }

    [TestMethod]
    public void PuddingToolSchemaService_BuildLlmTools_OnlyReorders_KeepsContent()
    {
        var registry = new FixedOrderToolRegistry(
        [
            Descriptor("zebra_tool", description: "Zebra desc"),
            Descriptor("alpha_tool", description: "Alpha desc"),
        ]);

        var schema = new PuddingToolSchemaService(registry);
        var tools = schema.BuildLlmTools(null);

        // 内容不变：集合等价、描述保留、数量一致（排序只影响顺序）。
        Assert.AreEqual(2, tools.Count);
        CollectionAssert.AreEquivalent(
            new[] { "zebra_tool", "alpha_tool" },
            tools.Select(t => t.Name).ToArray());
        var byName = tools.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
        Assert.AreEqual("Zebra desc", byName["zebra_tool"].Description);
        Assert.AreEqual("Alpha desc", byName["alpha_tool"].Description);
    }

    // ── SkillRuntime.BuildLlmTools ─────────────────────────

    [TestMethod]
    public void SkillRuntime_BuildLlmTools_OrdersByName_OrdinalIgnoreCase()
    {
        var runtime = new SkillRuntime(
        [
            FakeSkill("zebra_skill"),
            FakeSkill("Mike_skill"),
            FakeSkill("delta_skill"),
            FakeSkill("alpha_skill"),
        ],
        sandbox: null!,
        NullLogger<SkillRuntime>.Instance);

        var tools = runtime.BuildLlmTools(new CapabilityPolicy
        {
            AllowedToolNames = ["zebra_skill", "Mike_skill", "delta_skill", "alpha_skill"],
        });

        CollectionAssert.AreEqual(
            new[] { "alpha_skill", "delta_skill", "Mike_skill", "zebra_skill" },
            tools.Select(t => t.Name).ToArray());
    }

    // ── helpers ───────────────────────────────────────────

    private static ToolDescriptor Descriptor(string id, string? name = null, string? description = null) => new()
    {
        ToolId = id,
        Name = name ?? id,
        Description = description ?? $"{id} description",
        Parameters = new ToolParameterSchema([], []),
    };

    private static IAgentSkill FakeSkill(string skillId) => new FakeAgentSkill(skillId);

    private sealed class FakeAgentSkill : IAgentSkill
    {
        public FakeAgentSkill(string skillId) => SkillId = skillId;

        public string SkillId { get; }
        public string Name => SkillId;
        public string Description => $"{SkillId} description";
        public bool RequiresShellExecution => false;
        public ToolPermissionLevel PermissionLevel => ToolPermissionLevel.Medium;

        public Task<SkillResult> ExecuteAsync(SkillInvokeRequest request, CancellationToken ct = default)
            => Task.FromResult(new SkillResult { Success = true, Output = request.Input, ExitCode = 0 });
    }

    /// <summary>固定顺序（不做任何排序）的 registry 假实现，用于隔离验证 schema 服务的排序逻辑。</summary>
    private sealed class FixedOrderToolRegistry : IPuddingToolRegistry
    {
        private readonly IReadOnlyList<ToolDescriptor> _descriptors;

        public FixedOrderToolRegistry(IReadOnlyList<ToolDescriptor> descriptors) => _descriptors = descriptors;

        public IPuddingTool? GetTool(string toolId) => null;
        public ToolDescriptor? GetDescriptor(string toolId) => null;
        public IReadOnlyList<ToolDescriptor> ListDescriptors() => _descriptors;
        public IReadOnlyList<ToolDescriptor> ListAvailable(CapabilityPolicy? policy) => _descriptors;
    }
}
