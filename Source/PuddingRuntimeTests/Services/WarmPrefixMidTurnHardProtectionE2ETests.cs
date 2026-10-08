using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingCode.Services;
using PuddingMemoryEngine.Data;
using PuddingMemoryEngine.Entities;
using PuddingRuntime.Services;
using PuddingRuntime.Services.AgentLoop;
using PuddingRuntime.Services.Skills;

namespace PuddingRuntimeTests.Services;

/// <summary>
/// **端到端**：在一次真实驱动（`ExecuteAsync`）的 Turn 里验证 F3 的「逐轮硬保护」与 F2 的落库。
/// <para>
/// 场景（用户点名的验收用例）：第 0 轮判**软**维护 ⇒ 只延期、不调摘要模型；随后本轮助手正文
/// 把请求推过有效上限 ⇒ **第 1 轮必须重新评估硬保护**并同步压缩；压缩结果必须**落库**
/// （摘要行 + 精确到行的 CompactedBy），而不是只改内存。
/// </para>
/// <para>
/// 这是本仓库此前缺失的串联证据：各段（判定 / CAS / 身份 / 落库 / 重启水合）都有单测，
/// 但没有一次真实 Turn 把它们串起来。
/// </para>
/// </summary>
[TestClass]
public sealed class WarmPrefixMidTurnHardProtectionE2ETests
{
    private const string SessionId = "session-mid-turn-hard";
    private const string WorkspaceId = "ws-1";

    /// <summary>
    /// 用**词表拼接**而不是重复单字符：真实分词器对 `aaaa…` 压缩率极高（实测约 7.7 字符/token），
    /// 会让「按字符数估 token」严重失准。词表拼接稳定在 4~5 字符/token。
    /// </summary>
    private static string VariedText(int chars)
    {
        const string words = "alpha bravo charlie delta echo foxtrot golf hotel india juliet kilo lima ";
        var builder = new System.Text.StringBuilder(chars + words.Length);
        while (builder.Length < chars)
            builder.Append(words);
        return builder.ToString(0, chars);
    }

    /// <summary>第 0 轮的助手正文：足够大，使第 1 轮请求越过有效上限。</summary>
    private static readonly string LongAssistantBody = VariedText(44_000);

    private static string ContinueWithToolJson =>
        "{\"status\":\"CONTINUE\",\"message\":\"" + LongAssistantBody +
        "\",\"tool\":{\"name\":\"task_update\",\"args\":{\"task_id\":\"task-1\",\"assignment_id\":\"assign-1\",\"status\":\"in_progress\"}}}";

    private const string DoneJson = "{\"status\":\"DONE\",\"message\":\"finished\"}";

    /// <summary>摘要响应：内容短于被替换跨度即可（否则按无收益拒绝）。</summary>
    private const string SummaryResponse =
        "## Primary Request and Intent\n- e2e mid-turn hard protection\n## Next Step\n- assert durable commit";

    [TestMethod]
    public async Task SoftDeferredFirstRound_ThenGrowthCrossesHardLimit_CompactsMidTurn_AndPersists()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = CreateOptions(connection);
        await using (var db = new MemoryDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            await SeedAsync(db, messageCount: 36, charsPerMessage: 3_000);
        }

        var factory = new Factory(options);
        var llm = new ScriptedLlmClient(ContinueWithToolJson, SummaryResponse, DoneJson);
        var tools = new RecordingToolInvocationService();
        var snapshotStore = new ContextUsageSnapshotStore();
        var manager = CreateManager(factory);
        var log = new CapturingLogger();
        var service = CreateService(llm, tools, manager, snapshotStore, factory, log);

        // 走生产同款水合：内存历史里的消息**带持久身份登记**（F2 覆盖标注的前提）。
        // 注意：水合会把 DB 里的 system 行过滤掉（system prompt 由上下文管线在 Turn 内装配），
        // 而 warm-prefix 候选要求历史**以 system 开头**，故这里补一条前导 system（它不在被替换跨度内，
        // 因此不会影响跨度身份解析）。
        var history = manager.GetOrCreateHistory(SessionId);
        var hydration = await manager.TryHydrateStreamHistoryFromDbAsync(
            SessionId, history, 200_000, CancellationToken.None);
        Assert.IsTrue(hydration.Succeeded, "水合必须成功，否则本用例不成立");
        Assert.IsTrue(history.Count > 12, $"水合后历史必须足够长，实际 {history.Count}");
        history.Insert(0, new ChatMessage(ChatRole.System, "assembled system prompt"));
        Assert.AreEqual(ChatRole.System, history[0].Role);

        // 前提自检（防止分词器/配置漂移让用例悄悄失去意义）：
        // 第 0 轮必须「已过软触发且仍在限内」—— 这正是「软维护延期」的定义域。
        var config = CreateDispatchRequest().LlmConfig!;
        var limit = LlmRequestBudgetGuard.ResolveEffectiveInputLimit(config);
        var initial = snapshotStore.MeasureLlmRequest(SessionId, history, tools: null, config.ModelId);
        Assert.IsTrue(
            initial.UsedTokens <= limit,
            $"前提失败：第 0 轮必须仍在有效上限内（{initial.UsedTokens} <= {limit}）");
        Assert.IsTrue(
            initial.UsedTokens >= limit * 0.8,
            $"前提失败：第 0 轮必须已过软触发 0.8（{initial.UsedTokens} >= {limit * 0.8:F0}）");

        var result = await service.ExecuteAsync(CreateDispatchRequest());

        Assert.AreEqual(AgentExecutionState.Completed, result.ExecutionState, "Turn 必须跑到终态");

        // ① 中途确实发生了**摘要调用**（warm-prefix 的摘要请求带固定指令）。
        var summaryCalls = llm.Requests.Count(messages => messages.Any(m =>
            m.Content?.Contains("## Primary Request and Intent", StringComparison.Ordinal) == true
            && m.Content.Contains("compaction engine", StringComparison.Ordinal)));
        if (summaryCalls != 1)
        {
            var sizes = string.Join(
                " | ",
                llm.Requests.Select((r, i) =>
                    $"#{i}:{r.Count}msgs/{r.Sum(r2 => ContextUsageSnapshotStore.CountTokens(r2.Content ?? string.Empty))}tok"));
            var logLines = string.Join(
                "\n  ",
                log.Lines.Where(l =>
                    l.Contains("Compaction", StringComparison.Ordinal)
                    || l.Contains("ContextBudget", StringComparison.Ordinal)
                    || l.Contains("compaction", StringComparison.Ordinal)));
            Assert.Fail(
                $"诊断：llmCalls={llm.Requests.Count} toolCalls={tools.Requests.Count} " +
                $"historyCount={history.Count} effectiveLimit=" +
                $"{LlmRequestBudgetGuard.ResolveEffectiveInputLimit(CreateDispatchRequest().LlmConfig)} " +
                $"requests=[{sizes}]\n  相关日志:\n  {logLines}");
        }
        Assert.AreEqual(1, summaryCalls,
            $"第 0 轮软维护应延期（不调摘要）；第 1 轮越界后必须恰好压缩一次，实际摘要调用={summaryCalls}");

        // ② 压缩结果必须**落库**（F2 端到端）：摘要行 + 被覆盖行打标 + 代际推进。
        await using var verify = new MemoryDbContext(options);
        var summaryRow = await verify.Messages
            .FirstOrDefaultAsync(m => m.SessionId == SessionId && m.ContentType == "compact_summary");
        Assert.IsNotNull(summaryRow, "warm-prefix 必须把 checkpoint 落库，而不是只改内存");
        Assert.AreEqual("warm_prefix_checkpoint", summaryRow!.Source);

        var covered = await verify.Messages
            .CountAsync(m => m.SessionId == SessionId && m.CompactedBy != null);
        Assert.IsTrue(covered > 0, "被替换跨度必须逐行打上 CompactedBy");
        Assert.AreEqual(summaryRow.MessageId, (await verify.Messages
            .FirstAsync(m => m.SessionId == SessionId && m.CompactedBy != null)).CompactedBy);

        var session = await verify.Sessions.SingleAsync(s => s.SessionId == SessionId);
        Assert.AreEqual(1, session.CompactionGeneration, "落库必须推进持久代际");

        var manifest = await verify.CompactionCoverageManifests
            .FirstOrDefaultAsync(m => m.SessionId == SessionId);
        Assert.IsNotNull(manifest, "覆盖清单必须与摘要/打标同事务写入");
        Assert.AreEqual("warm_prefix_checkpoint", manifest!.Generator);
    }

    /// <summary>对照：同一个 Turn 若不越界（第 0 轮软维护延期后没有增长）⇒ 不应有任何落库。</summary>
    [TestMethod]
    public async Task SoftDeferredFirstRound_WithoutGrowth_PersistsNothing()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = CreateOptions(connection);
        await using (var db = new MemoryDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            await SeedAsync(db, messageCount: 36, charsPerMessage: 3_000);
        }

        var factory = new Factory(options);
        var llm = new ScriptedLlmClient(DoneJson);
        var tools = new RecordingToolInvocationService();
        var manager = CreateManager(factory);
        var service = CreateService(llm, tools, manager, new ContextUsageSnapshotStore(), factory);

        var history = manager.GetOrCreateHistory(SessionId);
        var hydration = await manager.TryHydrateStreamHistoryFromDbAsync(
            SessionId, history, 200_000, CancellationToken.None);
        Assert.IsTrue(hydration.Succeeded, "水合必须成功，否则对照用例不成立");

        await service.ExecuteAsync(CreateDispatchRequest());

        await using var verify = new MemoryDbContext(options);
        Assert.AreEqual(0, await verify.Messages.CountAsync(m =>
            m.SessionId == SessionId && m.ContentType == "compact_summary"),
            "未越界时不得落库");
        Assert.AreEqual(0, await verify.Messages.CountAsync(m =>
            m.SessionId == SessionId && m.CompactedBy != null));
        Assert.AreEqual(0, (await verify.Sessions.SingleAsync(s => s.SessionId == SessionId)).CompactionGeneration);
    }

    // ── 装配 ────────────────────────────────────────────────────────────────

    private static RuntimeDispatchRequest CreateDispatchRequest() => new()
    {
        SessionId = SessionId,
        AgentTemplateId = "workspace-task-agent",
        MessageText = "please proceed",
        WorkspaceId = WorkspaceId,
        AgentInstanceId = "agent-1",
        // 有效上限 = 30_000 − 1_000 − 1_024 = 27_976；36×2_000 字符 ≈ 24_000 token
        // ⇒ 第 0 轮在限内（软维护延期），助手正文 20_000 字符 ⇒ 第 1 轮越界。
        LlmConfig = new LlmConfig { ModelId = "test-model", MaxContextTokens = 30_000, MaxOutputTokens = 1_000 },
        SuppressContextAutoCompaction = true,
        MaxRounds = 5,
    };

    private static ContextWindowManager CreateManager(IDbContextFactory<MemoryDbContext> factory)
        => new(
            new AgentSessionManager(NullLogger<AgentSessionManager>.Instance),
            new InMemoryRuntimeSessionStore(),
            new ExecutionControlRegistry(),
            new ExecutionJournal(),
            NullLogger<ContextWindowManager>.Instance,
            memoryDbFactory: factory,
            canonicalMessageStore: new EmptyCanonicalStore());

    private static AgentExecutionService CreateService(
        ScriptedLlmClient llm,
        RecordingToolInvocationService tools,
        ContextWindowManager contextManager,
        ContextUsageSnapshotStore snapshotStore,
        IDbContextFactory<MemoryDbContext> memoryDbFactory,
        CapturingLogger? log = null)
    {
        var sessionManager = new AgentSessionManager(NullLogger<AgentSessionManager>.Instance);
        var runtimeSessionStore = new InMemoryRuntimeSessionStore();
        var memory = new FakeMemoryEngine();
        var sandbox = new SandboxExecutor(NullLogger<SandboxExecutor>.Instance);
        var guardrails = new AgentExecutionGuardrails();
        var controlRegistry = new ExecutionControlRegistry();
        var completionPolicy = new CompletionPolicy();
        var skillPackageRegistry = new AgentSkillPackageRegistry();
        var skillRuntime = new SkillRuntime(Array.Empty<IAgentSkill>(), sandbox, NullLogger<SkillRuntime>.Instance);
        var promptBuilder = new SystemPromptBuilder(
            memory, skillRuntime, skillPackageRegistry, NullLogger<SystemPromptBuilder>.Instance,
            new StartupEnvironmentInfo());
        var contextPipeline = new ContextPipeline(
            memory, skillRuntime, skillPackageRegistry, promptBuilder,
            new MemoryCache(new MemoryCacheOptions()), new ContextAssemblyStore(),
            NullLogger<ContextPipeline>.Instance, new FakeExecutionEnvironmentProvider());
        var sessionExecutionGate = new SessionExecutionGate(NullLogger<SessionExecutionGate>.Instance);
        var skillPackageDownloader = new SkillPackageDownloadService(
            new FakeHttpClientFactory(), NullLogger<SkillPackageDownloadService>.Instance);
        var compactionService = new ContextCompactionService(
            memoryDbFactory,
            new UnusedSummaryGenerator(),
            NullLogger<ContextCompactionService>.Instance,
            contentSummaryService: null,
            dataPaths: null);

        return new AgentExecutionService(
            sessionManager,
            runtimeSessionStore,
            memory,
            sandbox,
            llm,
            skillRuntime,
            guardrails,
            controlRegistry,
            new ExecutionJournal(),
            completionPolicy,
            skillPackageRegistry,
            skillPackageDownloader,
            Array.Empty<IAgentLoopHook>(),
            contextPipeline,
            contextManager,
            log ?? (ILogger<AgentExecutionService>)NullLogger<AgentExecutionService>.Instance,
            sessionExecutionGate,
            toolInvocationService: tools,
            contextUsageSnapshotStore: snapshotStore,
            contextCompactionService: compactionService);
    }

    private static DbContextOptions<MemoryDbContext> CreateOptions(SqliteConnection connection) =>
        new DbContextOptionsBuilder<MemoryDbContext>().UseSqlite(connection).Options;

    private static async Task SeedAsync(MemoryDbContext db, int messageCount, int charsPerMessage)
    {
        db.Sessions.Add(new SessionEntity
        {
            SessionId = SessionId,
            WorkspaceId = WorkspaceId,
            AgentId = "agent-1",
            Status = "Active",
            CreatedAt = 1,
            LastActivityAt = 1,
        });

        for (var i = 1; i <= messageCount; i++)
        {
            db.Messages.Add(new MessageEntity
            {
                MessageId = $"m{i}",
                SessionId = SessionId,
                Sequence = i,
                // warm-prefix 候选要求 durableHistory **以 system 开头**（leadingSystemCount != 0），
                // 否则 TryCreatePlan 静默返回 false、只剩请求副本裁剪（本用例最初就是这样失败的）。
                Role = i == 1 ? "system" : i % 2 == 0 ? "agent" : "user",
                ContentType = "text",
                Content = $"message {i} " + VariedText(charsPerMessage),
                CreatedAt = i,
            });
        }

        await db.SaveChangesAsync();
    }

    /// <summary>捕获运行时日志，用于在断言失败时给出「为什么没触发」的一手证据。</summary>
    private sealed class CapturingLogger : ILogger<AgentExecutionService>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Lines.Add($"[{logLevel}] {formatter(state, exception)}");
    }

    private sealed class Factory(DbContextOptions<MemoryDbContext> options) : IDbContextFactory<MemoryDbContext>
    {
        public MemoryDbContext CreateDbContext() => new(options);
    }

    private sealed class UnusedSummaryGenerator : IContextCompactionSummaryGenerator
    {
        public Task<string> GenerateSummaryAsync(ContextCompactionSummaryRequest request, CancellationToken ct = default)
            => throw new InvalidOperationException("warm-prefix 摘要走 LLM 调用，不应使用本生成器");
    }

    private sealed class EmptyCanonicalStore : ICompactionChatMessageStore
    {
        public Task<IReadOnlyList<ChatMessageRow>> GetForSessionAfterIdAsync(
            string sessionId, long afterId, int limit, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ChatMessageRow>>([]);

        public Task<IReadOnlyList<ChatMessageRow>> GetRecentForSessionAsync(
            string sessionId, int limit, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ChatMessageRow>>([]);

        public Task<int> GetCountForSessionAsync(string sessionId, CancellationToken ct = default)
            => Task.FromResult(0);
    }

    private sealed class ScriptedLlmClient(params string[] contents) : IRuntimeLlmClient
    {
        private readonly Queue<LlmResponse> _responses = new(contents.Select(c => new LlmResponse(c, null)));

        public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];

        public Task<LlmResponse> ChatAsync(
            string workspaceId,
            string sessionId,
            string agentTemplateId,
            IReadOnlyList<ChatMessage> messages,
            IReadOnlyList<LlmToolDefinition>? tools = null,
            LlmConfig? llmConfig = null,
            CancellationToken ct = default)
        {
            if (_responses.Count == 0)
                throw new InvalidOperationException("ScriptedLlmClient exhausted: more LLM rounds than scripted responses.");

            Requests.Add(messages.ToList());
            return Task.FromResult(_responses.Dequeue());
        }

        public async IAsyncEnumerable<StreamDelta> ChatStreamAsync(
            string workspaceId,
            string sessionId,
            string agentTemplateId,
            IReadOnlyList<ChatMessage> messages,
            IReadOnlyList<LlmToolDefinition>? tools = null,
            LlmConfig? llmConfig = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            var response = await ChatAsync(workspaceId, sessionId, agentTemplateId, messages, tools, llmConfig, ct);
            await Task.Yield();
            yield return new StreamDelta { ContentDelta = response.Content };
            yield return new StreamDelta { FinishReason = "stop" };
        }
    }

    private sealed class RecordingToolInvocationService : IToolInvocationService
    {
        public List<ToolInvocationRequest> Requests { get; } = [];

        public Task<ToolInvocationResult> InvokeAsync(ToolInvocationRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            return Task.FromResult(new ToolInvocationResult
            {
                Success = true,
                ToolCallId = request.ToolCallId,
                ToolName = request.ToolName,
            });
        }
    }

    private sealed class FakeMemoryEngine : IMemoryEngine
    {
        public string? BuildMemoryContext(
            string sessionId, string? workspaceId, string? agentId, string? parentSessionId = null) => null;

        public Task<string?> RecallWithIntentAsync(
            string userMessage, string workspaceId, string agentId,
            string? sessionId = null, int maxTokens = 2000, CancellationToken ct = default)
            => Task.FromResult<string?>(null);

        public void WriteBack(
            string llmReply, string sessionId, string? workspaceId, string source,
            string? agentId = null, string? parentSessionId = null) { }

        public void ClearSession(string sessionId) { }
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

    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
