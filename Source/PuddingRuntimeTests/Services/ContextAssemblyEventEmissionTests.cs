using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Abstractions;
using PuddingCode.Agents;
using PuddingCode.Configuration;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingPlatform.Services;
using PuddingRuntime.Services;
using PuddingRuntime.Services.Skills;

namespace PuddingRuntimeTests.Services;

/// <summary>
/// P0-1: context.assembled 事件 —— 层正文收集、脱敏、SHA-256、64KB 截断与事件发射的单元测试。
/// </summary>
[TestClass]
public sealed class ContextAssemblyEventEmissionTests
{
    // ── (a) 层正文收集完整性 ────────────────────────────────────────

    [TestMethod]
    public async Task AssembleAsync_LayerInfos_Carry_FullContent()
    {
        var store = new ContextAssemblyStore();
        var pipeline = CreatePipeline(store);

        var result = await pipeline.AssembleAsync(CreateRequest(), CancellationToken.None);

        Assert.IsNotNull(result.LayerInfos, "LayerInfos should be populated.");
        Assert.IsTrue(result.LayerInfos!.Count > 0, "Expected at least one layer info.");

        foreach (var layer in result.LayerInfos)
        {
            Assert.IsNotNull(layer.FullContent,
                $"Layer '{layer.LayerName}' FullContent should not be null.");
            if (layer.TokenCount > 0)
            {
                Assert.IsFalse(string.IsNullOrEmpty(layer.FullContent),
                    $"Layer '{layer.LayerName}' has tokens but empty FullContent.");
            }
        }
    }

    // ── (b) 64KB 截断 + truncated 标记 ─────────────────────────────

    [TestMethod]
    public async Task BuildLayerEmissionAsync_Truncates_Over_64KB_And_Marks_Truncated()
    {
        var bigContent = new string('a', 100_000); // 100KB, ASCII
        var layer = new ContextLayerInfo { LayerName = "L9-CURRENT", FullContent = bigContent };

        var emission = await ContextAssemblyService.BuildLayerEmissionAsync(layer, keyVaultService: null, CancellationToken.None);

        Assert.IsTrue(emission.Truncated, "Oversized layer must be marked truncated.");
        Assert.IsTrue(Encoding.UTF8.GetByteCount(emission.Content) <= ContextAssemblyService.MaxLayerContentBytes,
            "Truncated content must fit within the 64KB limit.");
        // Hash is computed on the full (stripped) content, before truncation.
        Assert.AreEqual(ContextAssemblyService.ComputeSha256Hex(bigContent), emission.ContentHash,
            "contentHash should be the SHA-256 of the full untruncated content.");
    }

    [TestMethod]
    public void TruncateToUtf8ByteLimit_DoesNot_Split_CodePoints()
    {
        // 30000 个 '你'（每个 3 字节 = 90000 字节），64KB 截断必须落在完整码点边界。
        var content = new string('你', 30_000);
        var truncated = ContextAssemblyService.TruncateToUtf8ByteLimit(content, ContextAssemblyService.MaxLayerContentBytes);

        Assert.IsTrue(Encoding.UTF8.GetByteCount(truncated) <= ContextAssemblyService.MaxLayerContentBytes);
        Assert.IsFalse(truncated.Contains('\uFFFD'), "Truncation must not produce replacement characters.");
    }

    // ── (c) SHA-256 hash 正确性 ─────────────────────────────────────

    [TestMethod]
    public void ComputeSha256Hex_Returns_Correct_Lowercase_Hex()
    {
        Assert.AreEqual(
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            ContextAssemblyService.ComputeSha256Hex(""));
        Assert.AreEqual(
            "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824",
            ContextAssemblyService.ComputeSha256Hex("hello"));
    }

    // ── (d) 脱敏调用 ────────────────────────────────────────────────

    [TestMethod]
    public async Task BuildLayerEmissionAsync_Strips_Secrets_Via_KeyVault()
    {
        var keyVault = new FakeKeyVaultService { Secret = "SECRET-123", Replacement = "[REDACTED]" };
        var layer = new ContextLayerInfo
        {
            LayerName = "L0-STATIC",
            FullContent = "api key = SECRET-123 and some text",
        };

        var emission = await ContextAssemblyService.BuildLayerEmissionAsync(layer, keyVault, CancellationToken.None);

        Assert.AreEqual(1, keyVault.StripCalls, "StripAsync should be invoked once per layer.");
        Assert.IsFalse(emission.Content.Contains("SECRET-123", StringComparison.Ordinal),
            "Secret must be removed from emitted content.");
        Assert.IsTrue(emission.Content.Contains("[REDACTED]", StringComparison.Ordinal),
            "Stripped content should contain the replacement.");
        Assert.AreEqual(ContextAssemblyService.ComputeSha256Hex(emission.Content), emission.ContentHash,
            "Hash should be computed over the stripped content.");
    }

    // ── (e) 事件发射 payload 结构 ───────────────────────────────────

    [TestMethod]
    public async Task EmitAsync_Writes_Canonical_Event_With_Expected_Payload()
    {
        var store = new RecordingEventStore();
        var emitter = new ContextAssemblyEventEmitter(store, NullLogger<ContextAssemblyEventEmitter>.Instance);
        var layers = new List<ContextAssemblyLayerEmission>
        {
            new("L0-STATIC", "hash0", "content-0", false),
            new("L9-CURRENT", "hash1", "content-1", true),
        };

        await emitter.EmitAsync(
            sessionId: "session-1",
            workspaceId: "workspace-1",
            agentId: "agent-1",
            turnId: null,
            traceId: "trace-1",
            layers,
            assembledAtIso: "2026-08-14T00:00:00.000Z",
            CancellationToken.None);

        Assert.AreEqual(1, store.Appends.Count);
        var (conversationId, expectedVersion, events, _) = store.Appends[0];
        Assert.AreEqual("session-1", conversationId);
        Assert.AreEqual(-1, expectedVersion);
        Assert.AreEqual(1, events.Count);

        var evt = events[0];
        Assert.AreEqual(ConversationEventTypes.ContextAssembled, evt.Type);
        Assert.IsTrue(evt.EventId.StartsWith("context:assembled:", StringComparison.Ordinal),
            $"EventId should use the context:assembled prefix, got '{evt.EventId}'.");
        Assert.AreEqual(1, evt.SchemaVersion);
        Assert.AreEqual("workspace-1", evt.WorkspaceId);
        Assert.AreEqual("agent-1", evt.AgentId);
        Assert.AreEqual(ConversationEventSourceKind.Agent, evt.SourceKind);
        Assert.AreEqual("trace-1", evt.TraceId);
        Assert.AreEqual("runtime.context_assembly", evt.ProducerComponent);

        var payload = evt.Payload;
        Assert.AreEqual(JsonValueKind.Object, payload.ValueKind);
        Assert.AreEqual("session-1", payload.GetProperty("sessionId").GetString());
        Assert.AreEqual("agent-1", payload.GetProperty("agentId").GetString());
        Assert.AreEqual("2026-08-14T00:00:00.000Z", payload.GetProperty("assembledAt").GetString());

        var layersArray = payload.GetProperty("layers");
        Assert.AreEqual(2, layersArray.GetArrayLength());

        var first = layersArray[0];
        Assert.AreEqual("L0-STATIC", first.GetProperty("name").GetString());
        Assert.AreEqual("hash0", first.GetProperty("contentHash").GetString());
        Assert.AreEqual("content-0", first.GetProperty("content").GetString());
        Assert.IsFalse(first.GetProperty("truncated").GetBoolean());

        var second = layersArray[1];
        Assert.AreEqual("L9-CURRENT", second.GetProperty("name").GetString());
        Assert.AreEqual("hash1", second.GetProperty("contentHash").GetString());
        Assert.IsTrue(second.GetProperty("truncated").GetBoolean());
    }

    // ── 辅助：ContextPipeline 构造（与 ContextPipelineLayerTests 一致）──

    private static ContextPipeline CreatePipeline(ContextAssemblyStore store)
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
            workspaceProfileProvider: workspaceProfile);
    }

    private static ContextRequest CreateRequest(string agentInstanceId = "agent-1") => new()
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
        AgentInstanceId = agentInstanceId,
        IsFirstMessage = true,
    };

    // ── Fakes ───────────────────────────────────────────────────────

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

    private sealed class FakeKeyVaultService : IKeyVaultService
    {
        public int StripCalls { get; private set; }
        public string Secret { get; init; } = "SECRET";
        public string Replacement { get; init; } = "[REDACTED]";

        public Task<string> StripAsync(string text, CancellationToken ct = default)
        {
            StripCalls++;
            return Task.FromResult(text.Replace(Secret, Replacement, StringComparison.Ordinal));
        }

        public Task<string> EncryptAsync(string plainText, CancellationToken ct = default) => Task.FromResult(plainText);
        public Task<string> DecryptAsync(string encryptedValue, CancellationToken ct = default) => Task.FromResult(encryptedValue);
        public Task<string> InjectAsync(string text, CancellationToken ct = default) => Task.FromResult(text);
        public Task<KeyVaultSecretSummary> CreateSecretAsync(CreateKeyVaultSecretCommand request, CancellationToken ct = default)
            => Task.FromResult(new KeyVaultSecretSummary());
        public Task<KeyVaultSecretSummary?> UpdateSecretAsync(string keyVaultId, UpdateKeyVaultSecretCommand request, CancellationToken ct = default)
            => Task.FromResult<KeyVaultSecretSummary?>(null);
        public Task<KeyVaultSecretDetail?> GetSecretAsync(string keyVaultId, bool includePlainText = false, CancellationToken ct = default)
            => Task.FromResult<KeyVaultSecretDetail?>(null);
        public Task<IReadOnlyList<KeyVaultSecretSummary>> ListSecretsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<KeyVaultSecretSummary>>(Array.Empty<KeyVaultSecretSummary>());
        public Task<bool> DeleteSecretAsync(string keyVaultId, CancellationToken ct = default)
            => Task.FromResult(false);
    }

    private sealed class RecordingEventStore : IConversationEventStore
    {
        public List<(string conversationId, long expectedVersion, IReadOnlyList<NewConversationEvent> events, EventWriteCondition condition)> Appends { get; } = new();

        public Task<AppendResult> AppendAsync(
            string conversationId,
            long expectedVersion,
            IReadOnlyList<NewConversationEvent> events,
            EventWriteCondition condition,
            CancellationToken ct)
        {
            Appends.Add((conversationId, expectedVersion, events, condition));
            return Task.FromResult(new AppendResult(1, events.Count, events.Count));
        }

        public Task<EventPage> ReadForwardAsync(string conversationId, long afterExclusive, long? throughInclusive, int limit, CancellationToken ct)
            => Task.FromResult(new EventPage(Array.Empty<ConversationEvent>(), null, false));

        public Task<EventPage> ReadBackwardAsync(string conversationId, long beforeExclusive, int limit, CancellationToken ct)
            => Task.FromResult(new EventPage(Array.Empty<ConversationEvent>(), null, false));

        public Task<EventPage> ReadByTypePrefixBackwardAsync(string conversationId, string typePrefix, long beforeExclusive, int limit, CancellationToken ct)
            => Task.FromResult(new EventPage(Array.Empty<ConversationEvent>(), null, false));

        public Task<EventBounds> GetBoundsAsync(string conversationId, CancellationToken ct)
            => Task.FromResult(new EventBounds(null, null));

        public Task EnsureTablesAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
