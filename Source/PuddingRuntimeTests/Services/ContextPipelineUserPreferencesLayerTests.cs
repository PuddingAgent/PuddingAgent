using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PuddingCode.Abstractions;
using PuddingCode.Agents;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingRuntime.Services;
using PuddingRuntime.Services.Skills;

namespace PuddingRuntimeTests.Services;

/// <summary>
/// 偏好快照仅在内容变化或模型可见历史缺失时追加，不重写系统前缀。
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

        Assert.IsFalse(result.SystemPrompt.Contains("USER-PREFERENCES", StringComparison.Ordinal));
        StringAssert.Contains(result.UserContextPrefix, "--- LAYER: USER-PREFERENCES ---");
        StringAssert.Contains(result.UserContextPrefix, "- **language**: 中文");
        StringAssert.Contains(result.UserContextPrefix, "complete snapshot replaces");

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

    [TestMethod]
    public async Task PreferenceChange_PreservesSystemPrefix_AndDoesNotMutateHistory()
    {
        var request = CreateRequest();
        var first = await CreatePipeline(new(), new FakeUserPreferenceService("language: Chinese"))
            .AssembleAsync(request, CancellationToken.None);
        ChatMessage[] history = [new(ChatRole.System, first.SystemPrompt), new(ChatRole.User, first.UserContextPrefix!)];
        var original = history.Select(m => m.Content).ToArray();
        var next = await CreatePipeline(new(), new FakeUserPreferenceService("language: English\n" + new string('x', 12000)))
            .AssembleAsync(request with { SessionHistory = history, IsFirstMessage = false }, CancellationToken.None);
        Assert.AreEqual(first.SystemPrompt, next.SystemPrompt, "Preference size must not change stable-layer trim budgets.");
        StringAssert.Contains(next.UserContextPrefix, "language: English");
        CollectionAssert.AreEqual(original, history.Select(m => m.Content).ToArray());
    }

    [TestMethod]
    public async Task ColdAssembly_ReusesVisibleSnapshot_AndReemitsAfterCompaction()
    {
        var request = CreateRequest();
        var first = await CreatePipeline(new(), new FakeUserPreferenceService("language: Chinese"))
            .AssembleAsync(request, CancellationToken.None);
        var resumedRequest = request with { IsFirstMessage = false,
            SessionHistory = [new(ChatRole.System, first.SystemPrompt), new(ChatRole.User, first.UserContextPrefix!)] };
        var restored = await CreatePipeline(new(), new FakeUserPreferenceService("language: Chinese"))
            .AssembleAsync(resumedRequest, CancellationToken.None);
        Assert.IsFalse(restored.UserContextPrefix?.Contains("L9-USER-PREFERENCES") == true);
        Assert.AreEqual(first.SystemPrompt, restored.SystemPrompt);

        var compacted = await CreatePipeline(new(), new FakeUserPreferenceService("language: Chinese"))
            .AssembleAsync(resumedRequest with { SessionHistory = [new(ChatRole.System, first.SystemPrompt),
                new(ChatRole.User, "<compact_summary>continue</compact_summary>")] }, CancellationToken.None);
        StringAssert.Contains(compacted.UserContextPrefix, "language: Chinese");
        Assert.AreEqual(first.SystemPrompt, compacted.SystemPrompt);
    }

    [TestMethod]
    public async Task SuccessfulEmptyRead_RevokesPreferences_ButFailedReadDoesNot()
    {
        var prior = ContextPipeline.BuildPreferenceUpdate([], "language: Chinese")!;
        var request = CreateRequest() with { IsFirstMessage = false, SessionHistory = [new(ChatRole.User, prior)] };
        var failed = await CreatePipeline(new(), new ThrowingUserPreferenceService()).AssembleAsync(request, CancellationToken.None);
        Assert.IsFalse(failed.UserContextPrefix?.Contains("L9-USER-PREFERENCES") == true);
        var deleted = await CreatePipeline(new(), new FakeUserPreferenceService(null)).AssembleAsync(request, CancellationToken.None);
        StringAssert.Contains(deleted.UserContextPrefix, "earlier stored preferences are revoked");
        Assert.AreEqual(failed.SystemPrompt, deleted.SystemPrompt);
        Assert.IsNull(ContextPipeline.BuildPreferenceUpdate([new(ChatRole.User, deleted.UserContextPrefix!)], null));
    }

    [TestMethod]
    public async Task ActualPreferenceService_StorageFailure_DoesNotEmitRevocation()
    {
        var library = new Mock<IMemoryLibrary>();
        library.Setup(x => x.ListLibrariesAsync("workspace-1", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("storage temporarily unavailable"));
        var service = new UserPreferenceService(library.Object,
            NullLogger<UserPreferenceService>.Instance);
        var prior = ContextPipeline.BuildPreferenceUpdate([], "language: Chinese")!;
        var result = await CreatePipeline(new(), service).AssembleAsync(CreateRequest() with
        {
            IsFirstMessage = false, SessionHistory = [new(ChatRole.User, prior)],
        }, CancellationToken.None);
        Assert.IsFalse(result.UserContextPrefix?.Contains("L9-USER-PREFERENCES") == true);
        Assert.IsFalse(result.UserContextPrefix?.Contains("revoked") == true);
        library.Verify(x => x.ListLibrariesAsync("workspace-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task MissingPreferenceBook_DoesNotSearchOtherWorkspaces()
    {
        var library = new Mock<IMemoryLibrary>();
        library.Setup(x => x.ListLibrariesAsync("workspace-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<LibraryRecord>());
        var service = new UserPreferenceService(library.Object, NullLogger<UserPreferenceService>.Instance);
        Assert.IsNull(await service.LoadPreferencesAsync("workspace-1"));
        library.Verify(x => x.ListLibrariesAsync("workspace-1", It.IsAny<CancellationToken>()), Times.Once);
        library.VerifyNoOtherCalls();
    }

    [TestMethod]
    public void PreferenceReversion_ComparesLatestSnapshot_NotAnyHistoricalMatch()
    {
        var a = ContextPipeline.BuildPreferenceUpdate([], "A")!;
        var b = ContextPipeline.BuildPreferenceUpdate([new(ChatRole.User, a)], "B")!;
        var revert = ContextPipeline.BuildPreferenceUpdate([new(ChatRole.User, a), new(ChatRole.User, b)], "A");
        Assert.AreEqual(a, revert);
        Assert.IsNull(ContextPipeline.BuildPreferenceUpdate([new(ChatRole.User, b), new(ChatRole.User, revert!)], "A"));
    }

    [TestMethod]
    public void IncompleteOrAssistantEchoSnapshot_DoesNotSuppressRequiredContext()
    {
        var a = ContextPipeline.BuildPreferenceUpdate([], "language: Chinese")!;
        Assert.IsNotNull(ContextPipeline.BuildPreferenceUpdate([new(ChatRole.Assistant, a)], "language: Chinese"));
        Assert.IsNotNull(ContextPipeline.BuildPreferenceUpdate([new(ChatRole.User, a.Replace("language: Chinese", "omitted"))], "language: Chinese"));
    }

    [TestMethod]
    public void DeletedPreferences_AfterCompaction_ExplicitlySupersedeOldSummaryFacts()
    {
        ChatMessage[] history = [new(ChatRole.User, "<compact_summary>Stored preference: language Chinese</compact_summary>")];
        var cleared = ContextPipeline.BuildPreferenceUpdate(history, null);
        StringAssert.Contains(cleared, "earlier stored preferences are revoked");
        Assert.IsNull(ContextPipeline.BuildPreferenceUpdate([.. history, new(ChatRole.User, cleared!)], null));
        Assert.IsNull(ContextPipeline.BuildPreferenceUpdate([], null));
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
