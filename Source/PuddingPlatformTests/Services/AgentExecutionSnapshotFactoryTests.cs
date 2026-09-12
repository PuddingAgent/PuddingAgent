using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Abstractions;
using PuddingCode.Core;
using PuddingPlatform.Services.Snapshot;

namespace PuddingPlatformTests.Services;

[TestClass]
public sealed class AgentExecutionSnapshotFactoryTests
{
    [TestMethod]
    public async Task CreateAsync_ShouldSnapshotAgentExecutionGuardrails()
    {
        var factory = new AgentExecutionSnapshotFactory(
            NullLogger<AgentExecutionSnapshotFactory>.Instance);
        var profile = new AgentRuntimeProfile
        {
            WorkspaceId = "default",
            AgentId = "agent-1",
            DisplayName = "Agent 1",
            MaxRounds = 12,
            MaxElapsedSeconds = 90,
            MaxToolCallsTotal = 7,
        };

        var snapshot = await factory.CreateAsync(
            profile,
            previousSnapshot: null,
            CancellationToken.None);

        Assert.AreEqual(12, snapshot.BudgetMaxRounds);
        Assert.AreEqual(7, snapshot.BudgetMaxToolCalls);
        Assert.AreEqual(TimeSpan.FromSeconds(90), snapshot.Timeout);
    }

    // ── V5-T2：视觉策略来源接入（配置合同 ∩ 模型类别，ADR-088 决策 2/5）─────────

    [TestMethod]
    public async Task CreateAsync_WithVisionContract_ProjectsConfiguredPolicyAndVersion()
    {
        // C1：配置驱动的视觉合同 → 快照策略按合同值；未配置字段沿用产品默认；
        // C4：版本可观测 —— ImageTokenEstimatorVersion = 合同 Version。
        var contract = new VisionCapabilityContract
        {
            Version = "deepseek-2026-09-12-t2-test",
            MaxImagesPerRequest = 4,
            InlineMaxBytesPerImage = 1_000_000,
            EstimatedTokensPerImageUpperBound = 512,
        };
        var factory = CreateFactory(VisionModel(contract: contract));

        var snapshot = await factory.CreateAsync(Profile(), null, CancellationToken.None);

        Assert.IsNotNull(snapshot.VisionPolicy);
        Assert.AreEqual(4, snapshot.VisionPolicy.MaxImagesPerRequest);
        Assert.AreEqual(1_000_000L, snapshot.VisionPolicy.InlineMaxBytesPerImage);
        Assert.AreEqual(512, snapshot.VisionPolicy.EstimatedTokensPerImageUpperBound);
        Assert.AreEqual("deepseek-2026-09-12-t2-test", snapshot.VisionPolicy.ImageTokenEstimatorVersion);
        Assert.AreEqual(VisionRequestPolicy.Default.InlineMaxTotalBytes, snapshot.VisionPolicy.InlineMaxTotalBytes);
        Assert.IsTrue(snapshot.SupportsVision);
    }

    [TestMethod]
    public async Task CreateAsync_WithVisionTagButNoContract_UsesProductDefaultPolicy()
    {
        // C3（缺配置语义显式）：具备 vision 标签但未配置合同 → 明确的产品默认护栏策略
        // （非静默假定支持：支持性由 vision 标签判定；策略值为产品门槛，非协议限制）。
        var factory = CreateFactory(VisionModel(contract: null));

        var snapshot = await factory.CreateAsync(Profile(), null, CancellationToken.None);

        Assert.IsTrue(snapshot.SupportsVision);
        Assert.IsNotNull(snapshot.VisionPolicy);
        Assert.AreEqual(VisionRequestPolicy.Default.MaxImagesPerRequest, snapshot.VisionPolicy.MaxImagesPerRequest);
        Assert.AreEqual(
            VisionRequestPolicy.Default.ImageTokenEstimatorVersion,
            snapshot.VisionPolicy.ImageTokenEstimatorVersion);
    }

    [TestMethod]
    public async Task CreateAsync_EmbeddingModel_IsNeverMarkedVisionCapable()
    {
        // C2：IsEmbedding 模型即便配了 vision 标签与视觉合同，也绝不投影图片输入策略。
        var contract = new VisionCapabilityContract { Version = "must-be-ignored" };
        var factory = CreateFactory(VisionModel(isEmbedding: true, contract: contract));

        var snapshot = await factory.CreateAsync(Profile(), null, CancellationToken.None);

        Assert.IsNull(snapshot.VisionPolicy);
    }

    [TestMethod]
    public async Task CreateAsync_ImageGenerationModel_IsNeverMarkedVisionCapable()
    {
        // C2：图像生成模型（image-generation 标签，锚点同 loader imageGeneration 节校验）不误标。
        var contract = new VisionCapabilityContract { Version = "must-be-ignored" };
        var factory = CreateFactory(VisionModel(tags: ["vision", "image-generation"], contract: contract));

        var snapshot = await factory.CreateAsync(Profile(), null, CancellationToken.None);

        Assert.IsNull(snapshot.VisionPolicy);
    }

    [TestMethod]
    public async Task CreateAsync_ModelWithoutVisionTag_GetsNoVisionPolicy()
    {
        // C3/类别安全：无 vision 标签 = 不支持图片输入；策略 null 与 SupportsVision=false 一致。
        var factory = CreateFactory(VisionModel(tags: ["fast", "cheap"], contract: null));

        var snapshot = await factory.CreateAsync(Profile(), null, CancellationToken.None);

        Assert.IsFalse(snapshot.SupportsVision);
        Assert.IsNull(snapshot.VisionPolicy);
    }

    [TestMethod]
    public async Task CreateAsync_ModelMissingFromConfig_GetsNoVisionPolicy()
    {
        // C3：模型不在配置（model-not-found）→ null，不伪造任何策略。
        var factory = CreateFactory(null);

        var snapshot = await factory.CreateAsync(Profile(), null, CancellationToken.None);

        Assert.IsNull(snapshot.VisionPolicy);
    }

    [TestMethod]
    public async Task CreateAsync_PreviousSnapshot_IsReused_VisionPolicyStaysFrozen()
    {
        // C5（旧 Run 冻结）：已有快照直接复用（CreateAsync 首行）；AgentExecutionSnapshot 为
        // 不可变 record，配置热更新（ILlmConfigService.Reload → GetAllModels）只影响此后新创建的快照。
        var factory = CreateFactory(VisionModel(new VisionCapabilityContract { Version = "v1" }));

        var first = await factory.CreateAsync(Profile(), null, CancellationToken.None);
        var second = await factory.CreateAsync(Profile(), first, CancellationToken.None);

        Assert.AreSame(first, second);
        Assert.AreEqual("v1", second.VisionPolicy!.ImageTokenEstimatorVersion);
    }

    private static AgentExecutionSnapshotFactory CreateFactory(LlmModelInfo? model) =>
        new(NullLogger<AgentExecutionSnapshotFactory>.Instance, new FakeLlmConfigService(model));

    private static AgentRuntimeProfile Profile() => new()
    {
        WorkspaceId = "default",
        AgentId = "agent-vision",
        DisplayName = "Vision Agent",
        PreferredProviderId = "deepseek",
        PreferredModelId = "deepseek-flash",
        MaxRounds = 10,
        MaxElapsedSeconds = 60,
        MaxToolCallsTotal = 20,
    };

    private static LlmModelInfo VisionModel(
        VisionCapabilityContract? contract = null,
        bool isEmbedding = false,
        IReadOnlyList<string>? tags = null) => new()
    {
        ProviderId = "deepseek",
        ModelId = "deepseek-flash",
        Protocol = "responses",
        CapabilityTags = tags is null ? ["vision", "fast"] : tags.ToList(),
        IsEmbedding = isEmbedding,
        VisionContract = contract,
    };

    private sealed class FakeLlmConfigService(LlmModelInfo? model) : ILlmConfigService
    {
        public IReadOnlyList<LlmProviderInfo> GetEnabledProviders() => [];
        public IReadOnlyList<LlmModelInfo> GetAllModels() => model is null ? [] : [model];
        public PuddingCode.Platform.LlmConfig? Resolve(string providerId, string modelId) => null;
        public LlmProfileInfo? ResolveProfile(string profileId) => null;
        public PuddingCode.Platform.LlmConfig? GetMemoryConfig() => null;
        public PuddingCode.Platform.LlmConfig? GetEmbeddingConfig() => null;
        public LlmProviderStrategy? GetProviderStrategy(string providerId) => null;
        public LlmProviderStrategy? GetModelStrategy(string providerId, string modelId) => null;
        public void Reload(object config) => throw new NotSupportedException();
    }
}
