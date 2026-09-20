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
    /// 覆盖：未知 stream 404、frozen stream 410。
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

    // ── ADR-057 S1+S4: SSE 时效语义（replay 帧标记 / 无游标 live-only）──

    /// <summary>
    /// 显式游标 0＝有意全量回放：历史帧必须带 replay=true，消费端才能把「历史」
    /// 与「此刻发生」区分开——此前帧上无法区分，孤儿 started 因此被当实时事件点亮。
    /// </summary>
    [TestMethod]
    public async Task EventsStream_WithExplicitZeroCursor_ReplaysHistoryAndMarksFramesAsReplay()
    {
        var sessionId = await CreateSessionAsync();
        await AppendEventsAsync(sessionId, count: 3);

        using var response = await _client.GetAsync(
            $"/api/sessions/{sessionId}/events/stream?afterSequence=0",
            HttpCompletionOption.ResponseHeadersRead);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        var frames = await ReadSseFramesAsync(
            response, maxFrames: 3, budget: TimeSpan.FromSeconds(20));

        Assert.HasCount(3, frames, "显式 0 必须回放已存在的全部历史事件。");
        Assert.AreEqual(1L, frames[0].Sequence, "回放必须从日志起点（sequence=1）开始。");
        Assert.IsTrue(
            frames.All(f => f.Replay),
            "历史回放帧必须带 replay=true；否则消费端会把几天前的 started 当成实时事件点亮。");
        Assert.IsTrue(
            frames[0].Sequence < frames[1].Sequence && frames[1].Sequence < frames[2].Sequence,
            "回放帧必须按 sequence 递增。");
    }

    /// <summary>
    /// 无游标＝客户端没有权威位置 → 只推实时帧（live-only），历史由快照负责。
    /// 因此连接后收到的第一帧必须是订阅之后产生的事件，且 replay=false。
    /// </summary>
    [TestMethod]
    public async Task EventsStream_WithoutCursor_DoesNotReplayHistory()
    {
        var sessionId = await CreateSessionAsync();
        var historicalHead = await AppendEventsAsync(sessionId, count: 3);

        var responseTask = _client.GetAsync(
            $"/api/sessions/{sessionId}/events/stream",
            HttpCompletionOption.ResponseHeadersRead);

        // seq<=historicalHead 的历史事件已经存在，无游标连接不得回放它们。
        // 持续追加新事件以驱动第一帧（追加会 signal 事件通知器，实时帧随即可达）。
        for (var i = 0; i < 20 && !responseTask.IsCompleted; i++)
        {
            await Task.Delay(250);
            await AppendEventsAsync(sessionId, count: 1);
        }

        using var response = await responseTask.WaitAsync(TimeSpan.FromSeconds(30));
        var frames = await ReadSseFramesAsync(
            response, maxFrames: 1, budget: TimeSpan.FromSeconds(20));

        Assert.HasCount(1, frames, "应收到实时帧——负结果不得由连接故障伪造。");
        Assert.IsFalse(frames[0].Replay, "无游标连接的首帧是实时帧，不得是 replay 历史帧。");
        Assert.IsGreaterThan(
            historicalHead,
            frames[0].Sequence,
            $"首帧必须是订阅之后产生的事件（sequence > {historicalHead}），实际 {frames[0].Sequence}；"
            + "若等于历史序号，说明无游标连接仍在全量回放历史。");
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
        // P0-4f: HTTP /compact 服务端入口创建根 Trace（Guid N），并透传到压缩服务调用。
        Assert.IsNotNull(capture.LastRequest!.TraceId);
        Assert.IsTrue(
            Guid.TryParseExact(capture.LastRequest.TraceId, "N", out _),
            $"HTTP /compact 必须创建 Guid-N 根 Trace，实际为 '{capture.LastRequest.TraceId}'。");
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
            int? maxInputTokens = null,
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

    // ── 测试工具（SSE 时效语义）──────────────────────────

    private async Task<string> CreateSessionAsync()
    {
        var createResp = await _client.PostAsJsonAsync("/api/sessions", new
        {
            workspaceId = "default",
            agentTemplateId = "global:general-assistant"
        });
        createResp.EnsureSuccessStatusCode();
        var created = await createResp.Content.ReadFromJsonAsync<SessionDto>(JsonOpts);
        Assert.IsNotNull(created, "创建会话失败，无法继续 SSE 语义断言。");
        return created!.SessionId;
    }

    /// <summary>直接经 Event Store 写入历史事件（含 sequence 分配与 head 推进）。</summary>
    private async Task<long> AppendEventsAsync(string conversationId, int count)
    {
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IConversationEventStore>();

        var drafts = new List<NewConversationEvent>(count);
        for (var i = 0; i < count; i++)
        {
            drafts.Add(new NewConversationEvent(
                $"evt-{Guid.NewGuid():N}",
                ConversationEventTypes.MessageCreated,
                1,
                "default",
                $"turn-{Guid.NewGuid():N}",
                null,
                null,
                $"msg-{Guid.NewGuid():N}",
                null,
                null,
                null,
                JsonSerializer.SerializeToElement(new { text = $"history-{i}" }),
                "default.global_general-assistant.6a8",
                ConversationEventSourceKind.Agent));
        }

        var result = await store.AppendAsync(
            conversationId,
            expectedVersion: -1,
            drafts,
            EventWriteCondition.ForRun("sse-semantics-test", 0),
            CancellationToken.None);
        return result.LastSequence;
    }

    private sealed record SseFrame(long Sequence, bool Replay);

    /// <summary>
    /// 读取至多 maxFrames 个 data 帧；heartbeat 注释行与空行忽略。
    /// SSE 是无限流，不能用「读到流结束」——预算耗尽即返回已读到的部分。
    /// </summary>
    private static async Task<List<SseFrame>> ReadSseFramesAsync(
        HttpResponseMessage response,
        int maxFrames,
        TimeSpan budget)
    {
        var frames = new List<SseFrame>();
        using var cts = new CancellationTokenSource(budget);

        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
            using var reader = new StreamReader(stream);

            while (frames.Count < maxFrames)
            {
                var line = await reader.ReadLineAsync(cts.Token);
                if (line is null) break;

                // 空行＝帧分隔；以 ':' 开头＝SSE 注释（本服务的 heartbeat）
                if (line.Length == 0 || line[0] == ':') continue;
                if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;

                using var doc = JsonDocument.Parse(line["data: ".Length..]);
                var root = doc.RootElement;
                frames.Add(new SseFrame(
                    root.GetProperty("sequence").GetInt64(),
                    root.TryGetProperty("replay", out var replay)
                        && replay.ValueKind == JsonValueKind.True));
            }
        }
        catch (OperationCanceledException)
        {
            // 预算耗尽：返回已读到的帧
        }

        return frames;
    }

    private sealed class ThrowingCompactionService : IContextCompactionService
    {
        public Task<ContextHealthSnapshot> GetHealthAsync(
            string sessionId,
            CancellationToken ct = default,
            int? contextWindowTokens = null,
            int? maxOutputTokens = null,
            int? maxInputTokens = null,
            int toolCount = 0)
            => throw new NotSupportedException();

        public Task<ContextCompactionResult> CompactAsync(
            ContextCompactionRequest request,
            CancellationToken ct = default)
            => throw new InvalidOperationException("synthetic compact failure");
    }

    // ── 安全（发现 Y+Z）：bootstrap 不再匿名可读 ─────────────

    /// <summary>
    /// bootstrap 返回消息正文与图片 artifactId。此前带 [AllowAnonymous]，
    /// 任意网络可达者只要拿到 conversationId 即可免认证读出会话内容与图片 ID。
    /// 前端 umi-request 全局请求拦截器（requestErrorConfig.ts）已附带 Bearer，
    /// 因此对已登录 Web 无影响——本用例同时守住这两侧。
    /// </summary>
    [TestMethod]
    public async Task ConversationBootstrap_RequiresAuthentication()
    {
        var sessionId = await CreateSessionAsync();

        using (var anonymous = _factory.CreateClient())
        {
            var unauthorized = await anonymous.GetAsync($"/api/conversations/{sessionId}/bootstrap");
            Assert.AreEqual(
                HttpStatusCode.Unauthorized,
                unauthorized.StatusCode,
                "匿名请求 bootstrap 必须 401：它返回消息正文，不能免认证可读。");
        }

        var authorized = await _client.GetAsync($"/api/conversations/{sessionId}/bootstrap");
        Assert.AreEqual(
            HttpStatusCode.OK,
            authorized.StatusCode,
            "已认证请求必须仍可用（前端 api.ts 的 getConversationBootstrap 依赖此端点）。");
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


