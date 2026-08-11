using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Abstractions;
using PuddingCode.Agents;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingRuntime.Services;
using PuddingRuntime.Services.Skills;

namespace PuddingRuntimeTests.Services;

/// <summary>
/// 验证 ContextPipeline 在会话启动组装时注入 L3-USER-PREFERENCES 层（Prefetch）。
/// </summary>
[TestClass]
public sealed class ContextPipelineUserPreferencesLayerTests
{
    [TestMethod]
    public async Task AssembleAsync_Injects_User_Preferences_Layer()
    {
        var store = new ContextAssemblyStore();
        var fakePrefs = new FakeUserPreferenceService(
            "--- LAYER: USER-PREFERENCES ---\n[USER PREFERENCES]\n- **language**: 中文\n");
        var pipeline = CreatePipeline(store, fakePrefs);

        var result = await pipeline.AssembleAsync(CreateRequest(), CancellationToken.None);

        StringAssert.Contains(result.SystemPrompt, "--- LAYER: USER-PREFERENCES ---");
        StringAssert.Contains(result.SystemPrompt, "- **language**: 中文");

        // 层顺序：L3-USER 之前是 L3-WORKSPACE-ENVIRONMENT，USER-PREFERENCES 位于 L3-USER 之后、L4-PINNED 之前
        Assert.IsTrue(result.SystemPrompt.IndexOf("--- LAYER: USER-PREFERENCES ---", StringComparison.Ordinal)
            > result.SystemPrompt.IndexOf("--- LAYER: USER ---", StringComparison.Ordinal));
        Assert.IsTrue(result.SystemPrompt.IndexOf("--- CONTEXT-LAYER: L4-PINNED ---", StringComparison.Ordinal)
            > result.SystemPrompt.IndexOf("--- LAYER: USER-PREFERENCES ---", StringComparison.Ordinal));

        var prefsSnapshot = result.Layers.FirstOrDefault(l => l.LayerName == "用户偏好记忆");
        Assert.IsNotNull(prefsSnapshot);
        Assert.IsTrue(prefsSnapshot.EstimatedTokens > 0);
    }

    [TestMethod]
    public async Task AssembleAsync_Skips_Layer_When_No_Preferences()
    {
        var store = new ContextAssemblyStore();
        var fakePrefs = new FakeUserPreferenceService(null);
        var pipeline = CreatePipeline(store, fakePrefs);

        var result = await pipeline.AssembleAsync(CreateRequest(), CancellationToken.None);

        Assert.IsFalse(result.SystemPrompt.Contains("USER-PREFERENCES", StringComparison.Ordinal));
        Assert.IsFalse(result.Layers.Any(l => l.LayerName == "用户偏好记忆"));
    }

    [TestMethod]
    public async Task AssembleAsync_Tolerates_Prefetch_Failure()
    {
        var store = new ContextAssemblyStore();
        var failingPrefs = new ThrowingUserPreferenceService();
        var pipeline = CreatePipeline(store, failingPrefs);

        // 不应抛出；上下文组装正常完成且不包含偏好层
        var result = await pipeline.AssembleAsync(CreateRequest(), CancellationToken.None);

        Assert.IsFalse(result.SystemPrompt.Contains("USER-PREFERENCES", StringComparison.Ordinal));
        StringAssert.Contains(result.SystemPrompt, "--- LAYER: USER ---");
    }

    // ── 测试基础设施 ─────────────────────────────────────────────────

    private static ContextPipeline CreatePipeline(
        ContextAssemblyStore store,
        IUserPreferenceService userPreferenceService)
    {
        var memory = new FakeMemoryEngine();
        var skillRegistry = new AgentSkillPackageRegistry();
        var sandbox = new SandboxExecutor(NullLogger<SandboxExecutor>.Instance);
        var skillRuntime = new SkillRuntime(Array.Empty<IAgentSkill>(), sandbox, NullLogger<SkillRuntime>.Instance);
        var workspaceProfile = new FakeWorkspaceProfileProvider();
        var promptBuilder = new SystemPromptBuilder(
            memory,
            skillRuntime,
            skillRegistry,
            NullLogger<SystemPromptBuilder>.Instance,
            new StartupEnvironmentInfo(),
            workspaceProfileProvider: workspaceProfile);

        return new ContextPipeline(
            memory,
            skillRuntime,
            skillRegistry,
            promptBuilder,
            new MemoryCache(new MemoryCacheOptions()),
            store,
            NullLogger<ContextPipeline>.Instance,
            new FakeExecutionEnvironmentProvider(),
            workspaceProfileProvider: workspaceProfile,
            userPreferenceService: userPreferenceService);
    }

    private static ContextRequest CreateRequest() => new()
    {
        Template = new AgentTemplateDefinition
        {
            TemplateId = "agent-template",
            Name = "Agent Template",
            TemplateType = AgentTemplateType.Task,
            SystemPrompt = "You are an agent.",
            Runtime = new RuntimeProfile { MaxContextTokens = 16000 },
        },
        WorkspaceId = "workspace-1",
        SessionId = $"session-{Guid.NewGuid():N}",
        AgentTemplateId = "agent-template",
        UserMessage = "Hello, what can you do?",
        AgentInstanceId = "agent-1",
        IsFirstMessage = true,
    };

    private sealed class FakeUserPreferenceService : IUserPreferenceService
    {
        private readonly string? _block;

        public FakeUserPreferenceService(string? block)
        {
            _block = block;
        }

        public Task<string?> LoadPreferencesAsync(
            string? workspaceId, int maxItems = 20, CancellationToken ct = default)
            => Task.FromResult(_block);

        public Task<PreferenceWriteResult> SavePreferenceAsync(
            string workspaceId, string key, string value,
            string? sourceSessionId = null, string? agentInstanceId = null,
            CancellationToken ct = default)
            => Task.FromResult(new PreferenceWriteResult(key, value, "book-1", "chapter-1", false));

        public Task<bool> DeletePreferenceAsync(
            string workspaceId, string key, CancellationToken ct = default)
            => Task.FromResult(true);
    }

    private sealed class ThrowingUserPreferenceService : IUserPreferenceService
    {
        public Task<string?> LoadPreferencesAsync(
            string? workspaceId, int maxItems = 20, CancellationToken ct = default)
            => throw new InvalidOperationException("simulated prefetch failure");

        public Task<PreferenceWriteResult> SavePreferenceAsync(
            string workspaceId, string key, string value,
            string? sourceSessionId = null, string? agentInstanceId = null,
            CancellationToken ct = default)
            => throw new InvalidOperationException("simulated prefetch failure");

        public Task<bool> DeletePreferenceAsync(
            string workspaceId, string key, CancellationToken ct = default)
            => throw new InvalidOperationException("simulated prefetch failure");
    }

    private sealed class FakeMemoryEngine : IMemoryEngine
    {
        public string? BuildMemoryContext(
            string sessionId, string? workspaceId, string? agentId, string? parentSessionId = null)
            => null;

        public Task<string?> RecallWithIntentAsync(
            string userMessage, string workspaceId, string agentId,
            string? sessionId = null, int maxTokens = 2000, CancellationToken ct = default)
            => Task.FromResult<string?>(null);

        public void WriteBack(
            string llmReply, string sessionId, string? workspaceId, string source,
            string? agentId = null, string? parentSessionId = null) { }

        public void ClearSession(string sessionId) { }
    }

    private sealed class FakeWorkspaceProfileProvider : IWorkspaceProfileProvider
    {
        public Task<string?> GetWorkspaceUserProfileAsync(string workspaceId, CancellationToken ct = default)
            => Task.FromResult<string?>(null);
    }

    private sealed class FakeExecutionEnvironmentProvider : IExecutionEnvironmentProvider
    {
        public string OsDescription => "TestOS";
        public string OsArchitecture => "X64";
        public string RuntimeVersion => "10.0";
        public string AppBaseDirectory => "E:\\app";
        public string PathSeparator => "\\";
        public bool IsContainer => false;
        public string DefaultShell => "powershell";
        public string EnvironmentFingerprint => "test-env";
        public string? GetWorkspaceRoot(string workspaceId) => $"E:\\workspaces\\{workspaceId}";
    }
}
