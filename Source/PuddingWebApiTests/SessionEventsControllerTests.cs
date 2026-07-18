using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PuddingCode.Abstractions;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services;

namespace PuddingWebApiTests;

/// <summary>
/// SessionEventsController 集成测试。
/// 覆盖：未知 replay 404、未知 stream 404、frozen stream 410。
/// 关联 ADR-053/ADR-054。
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class SessionEventsControllerTests
{
    private static CustomWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [ClassInitialize]
    public static void ClassInit(TestContext _)
    {
        _factory = new CustomWebApplicationFactory();
    }

    [ClassCleanup]
    public static void ClassCleanup()
    {
        _factory.Dispose();
    }

    [TestInitialize]
    public void TestInit()
    {
        _client = _factory.CreateClient();
        JwtHelper.SetBearerToken(_client);
    }

    [TestCleanup]
    public void TestCleanup()
    {
        _client?.Dispose();
    }

    // ── P1-1: unknown replay → 404 ─────────────────────
    [TestMethod]
    public async Task Replay_Returns404_WhenSessionNotFound()
    {
        var unknownId = $"ghost-session-{Guid.NewGuid():N}";
        var response = await _client.GetAsync($"/api/sessions/{unknownId}/replay?from=0&limit=50");
        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── P1-2: unknown stream → 404 ─────────────────────
    [TestMethod]
    public async Task EventsStream_Returns404_WhenSessionNotFound()
    {
        var unknownId = $"ghost-session-{Guid.NewGuid():N}";
        var response = await _client.GetAsync($"/api/sessions/{unknownId}/events/stream");

        // 在建立 SSE 连接之前，会话不存在验证直接返回 404
        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── P1-3: frozen stream → 410 ─────────────────────
    [TestMethod]
    public async Task EventsStream_Returns410_WhenSessionIsFrozen()
    {
        // 1. 创建 session
        var createResp = await _client.PostAsJsonAsync("/api/sessions", new
        {
            workspaceId = "default",
            agentTemplateId = "global:general-assistant"
        });
        var created = await createResp.Content.ReadFromJsonAsync<SessionDto>(JsonOpts);

        // 2. 归档 → Status = Frozen
        var archiveResp = await _client.PostAsync($"/api/sessions/{created!.SessionId}/archive", null);
        Assert.AreEqual(HttpStatusCode.OK, archiveResp.StatusCode);

        // 3. 尝试连接事件流 → 应返回 410
        var streamResp = await _client.GetAsync($"/api/sessions/{created.SessionId}/events/stream");
        Assert.AreEqual(HttpStatusCode.Gone, streamResp.StatusCode);
    }

    // ── P1-4: unknown replay with limit boundary ───────
    [TestMethod]
    public async Task Replay_Returns404_RegardlessOfLimitBoundary()
    {
        var unknownId = $"ghost-session-{Guid.NewGuid():N}";
        var response = await _client.GetAsync($"/api/sessions/{unknownId}/replay?from=99999&limit=500");
        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── P1-5: unknown stream does not return 500 ───────
    [TestMethod]
    public async Task EventsStream_Returns404_Not500_ForUnknownSession()
    {
        // 确认未知 session 不会导致内部异常（如 NRE），而是稳定返回 404
        var unknownId = $"ghost-session-{Guid.NewGuid():N}";
        var response = await _client.GetAsync($"/api/sessions/{unknownId}/events/stream");
        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
        Assert.AreNotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [TestMethod]
    public async Task Compact_Passes_Runtime_Profile_To_Compaction_Service()
    {
        var capture = new CapturingCompactionService();
        using var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IContextCompactionService>();
                services.AddSingleton<IContextCompactionService>(capture);
                services.RemoveAll<IAgentRuntimeProfileResolver>();
                services.AddSingleton<IAgentRuntimeProfileResolver>(new FixedAgentRuntimeProfileResolver());
                services.RemoveAll<ICompactionSessionSuccessor>();
                services.AddSingleton<ICompactionSessionSuccessor>(new FixedCompactionSessionSuccessor());
            });
        });
        using var client = factory.CreateClient();
        JwtHelper.SetBearerToken(client);

        var createResp = await client.PostAsJsonAsync("/api/sessions", new
        {
            workspaceId = "default",
            agentTemplateId = "global:research-assistant",
            title = "compact profile test"
        });
        createResp.EnsureSuccessStatusCode();
        var created = await createResp.Content.ReadFromJsonAsync<SessionDto>(JsonOpts);

        var compactResp = await client.PostAsJsonAsync($"/api/sessions/{created!.SessionId}/compact", new
        {
            workspaceId = "default",
            agentId = "default.global_research-assistant.c1",
            reason = "manual slash command"
        });

        Assert.AreEqual(HttpStatusCode.OK, compactResp.StatusCode);
        Assert.IsNotNull(capture.LastRequest);
        Assert.IsNotNull(capture.LastRequest!.LlmConfig);
        Assert.AreEqual("deepseek-v4-flash", capture.LastRequest.LlmConfig!.ModelId);
        Assert.IsNotNull(capture.LastRequest.CapabilityPolicy);
        Assert.IsNotNull(capture.LastRequest.ToolDefinitions);
        Assert.IsNotNull(capture.LastRequest.SkillPackages);

        using var compactBody = JsonDocument.Parse(
            await compactResp.Content.ReadAsStringAsync());
        var compactionId = compactBody.RootElement
            .GetProperty("compactionId")
            .GetString();
        var successorId = compactBody.RootElement
            .GetProperty("newSessionId")
            .GetString();
        Assert.IsFalse(string.IsNullOrWhiteSpace(compactionId));
        Assert.IsFalse(string.IsNullOrWhiteSpace(successorId));

        using var sourceEvents = JsonDocument.Parse(
            await client.GetStringAsync(
                $"/api/sessions/{created.SessionId}/events?from=0&limit=50"));
        var sourceTypes = sourceEvents.RootElement
            .GetProperty("events")
            .EnumerateArray()
            .Select(item => item.GetProperty("type").GetString())
            .ToArray();
        CollectionAssert.Contains(
            sourceTypes,
            ConversationEventTypes.ContextCompactionStarted);
        CollectionAssert.Contains(
            sourceTypes,
            ConversationEventTypes.ContextCompactionCompleted);

        using var successorEvents = JsonDocument.Parse(
            await client.GetStringAsync(
                $"/api/sessions/{successorId}/events?from=0&limit=50"));
        var successorCompleted = successorEvents.RootElement
            .GetProperty("events")
            .EnumerateArray()
            .Single(item =>
                item.GetProperty("type").GetString()
                == ConversationEventTypes.ContextCompactionCompleted);
        Assert.AreEqual(
            compactionId,
            successorCompleted
                .GetProperty("payload")
                .GetProperty("compactionId")
                .GetString());

        using var successorBootstrap = JsonDocument.Parse(
            await client.GetStringAsync(
                $"/api/conversations/{successorId}/bootstrap?messageLimit=1"));
        var lifecycleCompleted = successorBootstrap.RootElement
            .GetProperty("lifecycleEvents")
            .EnumerateArray()
            .Single(item =>
                item.GetProperty("type").GetString()
                == ConversationEventTypes.ContextCompactionCompleted);
        Assert.AreEqual(
            compactionId,
            lifecycleCompleted
                .GetProperty("payload")
                .GetProperty("compactionId")
                .GetString());
    }

    [TestMethod]
    public async Task Compact_Persists_Failed_Terminal_Event()
    {
        using var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IContextCompactionService>();
                services.AddSingleton<IContextCompactionService>(
                    new ThrowingCompactionService());
                services.RemoveAll<IAgentRuntimeProfileResolver>();
                services.AddSingleton<IAgentRuntimeProfileResolver>(
                    new FixedAgentRuntimeProfileResolver());
                services.RemoveAll<ICompactionSessionSuccessor>();
                services.AddSingleton<ICompactionSessionSuccessor>(
                    new FixedCompactionSessionSuccessor());
            });
        });
        using var client = factory.CreateClient();
        JwtHelper.SetBearerToken(client);

        var createResp = await client.PostAsJsonAsync("/api/sessions", new
        {
            workspaceId = "default",
            agentTemplateId = "global:research-assistant",
            title = "compact failure test"
        });
        createResp.EnsureSuccessStatusCode();
        var created = await createResp.Content.ReadFromJsonAsync<SessionDto>(JsonOpts);
        var compactionId = $"compact-failure-{Guid.NewGuid():N}";

        var compactResp = await client.PostAsJsonAsync(
            $"/api/sessions/{created!.SessionId}/compact",
            new
            {
                workspaceId = "default",
                agentId = "default.global_research-assistant.c1",
                compactionId,
            });

        Assert.AreEqual(
            HttpStatusCode.InternalServerError,
            compactResp.StatusCode);
        using var sourceEvents = JsonDocument.Parse(
            await client.GetStringAsync(
                $"/api/sessions/{created.SessionId}/events?from=0&limit=50"));
        var failed = sourceEvents.RootElement
            .GetProperty("events")
            .EnumerateArray()
            .Single(item =>
                item.GetProperty("type").GetString()
                == ConversationEventTypes.ContextCompactionFailed);
        Assert.AreEqual(
            compactionId,
            failed.GetProperty("payload").GetProperty("compactionId").GetString());
    }

    private sealed class FixedCompactionSessionSuccessor : ICompactionSessionSuccessor
    {
        public Task<CompactionSuccessor> CreateAsync(
            CreateCompactionSuccessorCommand command,
            CancellationToken ct)
            => Task.FromResult(new CompactionSuccessor(
                $"successor-{command.PreviousConversationId}",
                "compact profile successor"));
    }

    private sealed class CapturingCompactionService : IContextCompactionService
    {
        public ContextCompactionRequest? LastRequest { get; private set; }

        public Task<ContextHealthSnapshot> GetHealthAsync(
            string sessionId,
            CancellationToken ct = default,
            int? contextWindowTokens = null,
            int? maxOutputTokens = null,
            int toolCount = 0)
            => Task.FromResult(new ContextHealthSnapshot(
                sessionId,
                UsedTokens: 1,
                ContextWindowTokens: contextWindowTokens ?? 1024,
                EffectiveWindowTokens: contextWindowTokens ?? 1024,
                RemainingTokens: 1023,
                UsageRatio: 0.001,
                ContextHealthState.Healthy,
                ShouldSuggestCompact: false,
                ShouldAutoCompact: false,
                ShouldBlockSend: false));

        public Task<ContextCompactionResult> CompactAsync(
            ContextCompactionRequest request,
            CancellationToken ct = default)
        {
            LastRequest = request;
            if (request.LlmConfig is null)
                throw new InvalidOperationException("Expected compact request to include LlmConfig.");

            return Task.FromResult(new ContextCompactionResult(
                request.SessionId,
                SummaryMessageId: "summary-1",
                request.Mode,
                request.Level,
                BeforeTokens: 100,
                AfterTokens: 60,
                CompactedMessageCount: 1,
                SummaryPreview: "summary",
                SummaryMarkdown: "summary"));
        }
    }

    private sealed class ThrowingCompactionService : IContextCompactionService
    {
        public Task<ContextHealthSnapshot> GetHealthAsync(
            string sessionId,
            CancellationToken ct = default,
            int? contextWindowTokens = null,
            int? maxOutputTokens = null,
            int toolCount = 0)
            => throw new NotSupportedException();

        public Task<ContextCompactionResult> CompactAsync(
            ContextCompactionRequest request,
            CancellationToken ct = default)
            => throw new InvalidOperationException("synthetic compact failure");
    }

    private sealed class FixedAgentRuntimeProfileResolver : IAgentRuntimeProfileResolver
    {
        public Task<AgentRuntimeProfile> ResolveAsync(
            string workspaceId,
            string agentId,
            CancellationToken ct = default)
            => Task.FromResult(new AgentRuntimeProfile
            {
                WorkspaceId = workspaceId,
                AgentId = agentId,
                DisplayName = "Research",
                SourceTemplateId = "research-assistant",
                PreferredProviderId = "deepseek",
                PreferredModelId = "deepseek-v4-flash",
                LlmConfig = new LlmConfig
                {
                    Endpoint = "https://api.deepseek.com",
                    KeyVaultId = "test-key",
                    ModelId = "deepseek-v4-flash",
                    MaxContextTokens = 1_048_576,
                    MaxOutputTokens = 393_216,
                },
                CapabilityPolicy = new CapabilityPolicy(),
                ToolDefinitions = [],
                SkillPackages = [],
            });
    }
}


