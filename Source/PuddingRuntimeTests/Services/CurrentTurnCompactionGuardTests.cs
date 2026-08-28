using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingMemoryEngine.Data;
using PuddingMemoryEngine.Entities;
using PuddingRuntime.Services;
using PuddingRuntime.Services.AgentLoop;

namespace PuddingRuntimeTests.Services;

[TestClass]
public sealed class CurrentTurnCompactionGuardTests
{
    private const string SessionId = "session-1";

    // ───────────────────────────── 纯函数守卫 ─────────────────────────────

    [TestMethod]
    public void Guard_Aborts_WhenFencedUserMessageFallsInScope()
    {
        var candidates = new List<CurrentTurnCompactionGuardMessage>
        {
            new("m1", "user", "old question"),
            new("m2", "agent", "old answer"),
            new("m3", "user", BuildFenceContent("a".PadLeft(64, 'a'), "current ask")),
            new("m4", "agent", "working"),
        };
        var scope = candidates.Take(2).ToList();

        Assert.IsFalse(CurrentTurnCompactionGuard.ShouldAbortCompaction(candidates, scope));

        var scopeWithFence = candidates.Take(3).ToList();
        Assert.IsTrue(CurrentTurnCompactionGuard.ShouldAbortCompaction(candidates, scopeWithFence));
    }

    [TestMethod]
    public void Guard_Aborts_WhenLastUserMessageFallsInScope_EvenWithoutFence()
    {
        var candidates = new List<CurrentTurnCompactionGuardMessage>
        {
            new("m1", "user", "old"),
            new("m2", "agent", "reply"),
            new("m3", "user", "latest ask, fence was stripped"),
            new("m4", "agent", "ack"),
        };
        var scope = new List<CurrentTurnCompactionGuardMessage> { candidates[0], candidates[2] };

        // m3 是最后一条 User 消息（accepted current turn 的保守等价物），落入 scope 即中止。
        Assert.IsTrue(CurrentTurnCompactionGuard.ShouldAbortCompaction(candidates, scope));
    }

    [TestMethod]
    public void Guard_DoesNotAbort_WhenScopeContainsOnlyOldTurns()
    {
        var candidates = new List<CurrentTurnCompactionGuardMessage>
        {
            new("m1", "user", "old"),
            new("m2", "agent", "reply"),
            new("m3", "user", "latest"),
            new("m4", "agent", "ack"),
        };
        var scope = new List<CurrentTurnCompactionGuardMessage> { candidates[0], candidates[1] };

        Assert.IsFalse(CurrentTurnCompactionGuard.ShouldAbortCompaction(candidates, scope));
    }

    [TestMethod]
    public void Guard_DoesNotAbort_WhenScopeEmpty()
    {
        var candidates = new List<CurrentTurnCompactionGuardMessage> { new("m1", "user", "x") };
        Assert.IsFalse(CurrentTurnCompactionGuard.ShouldAbortCompaction(candidates, new List<CurrentTurnCompactionGuardMessage>()));
        Assert.IsFalse(CurrentTurnCompactionGuard.ShouldAbortCompaction(new List<CurrentTurnCompactionGuardMessage>(), candidates));
    }

    [TestMethod]
    public void Guard_FenceMarker_Detection()
    {
        Assert.IsTrue(CurrentTurnCompactionGuard.HasCurrentTurnFence("pre [CURRENT USER TURN input_sha256=abc] post"));
        Assert.IsFalse(CurrentTurnCompactionGuard.HasCurrentTurnFence("plain text"));
        Assert.IsFalse(CurrentTurnCompactionGuard.HasCurrentTurnFence(null));
        Assert.IsFalse(CurrentTurnCompactionGuard.IsCurrentTurnMessage("agent", "[CURRENT USER TURN input_sha256=x]"));
        Assert.IsTrue(CurrentTurnCompactionGuard.IsCurrentTurnMessage("User", "[CURRENT USER TURN input_sha256=x]"));
    }

    // ─────────────────────── 集成：压缩中止 / 放行 ───────────────────────

    [TestMethod]
    public async Task FullCompact_Aborts_WhenCurrentTurnFallsInCompactionScope()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = CreateOptions(connection);
        await using var db = new MemoryDbContext(options);
        await db.Database.EnsureCreatedAsync();

        const int total = 26;
        var hash = ComputeInputHash("mid-1", "当前轮请求：修复 compaction 丢围栏问题");
        var fenceContent = BuildFenceContent(hash, "当前轮请求：修复 compaction 丢围栏问题");
        await SeedMessagesAsync(db, SessionId, total, i =>
        {
            if (i == 20)
                return ("user", fenceContent);
            if (i < 20)
                return (i % 2 == 1 ? "user" : "agent", $"message {i}");
            return ("agent", $"assistant reply {i}");
        });

        var service = CreateService(options, dataPaths: null);
        var result = await service.CompactAsync(CreateRequest());

        // 中止：零压缩 + 明确标记。
        Assert.AreEqual(0, result.CompactedMessageCount);
        Assert.IsTrue(result.SkippedDueToCurrentTurnGuard);

        // 历史 100% 保留：无摘要行、无 CompactedBy 标记、fence 内容逐字未动。
        await using var verify = new MemoryDbContext(options);
        Assert.AreEqual(0, await verify.Messages.CountAsync(m => m.SessionId == SessionId && m.ContentType == "compact_summary"));
        Assert.AreEqual(0, await verify.Messages.CountAsync(m => m.SessionId == SessionId && m.CompactedBy != null));
        var fenceRow = await verify.Messages.SingleAsync(m => m.SessionId == SessionId && m.Content == fenceContent);
        Assert.AreEqual(20, fenceRow.Sequence);
        Assert.IsNull(fenceRow.CompactedBy);
    }

    [TestMethod]
    public async Task FullCompact_Proceeds_WhenCurrentTurnRetainedInRecentWindow()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = CreateOptions(connection);
        await using var db = new MemoryDbContext(options);
        await db.Database.EnsureCreatedAsync();

        const int total = 26;
        var hash = ComputeInputHash("mid-2", "recent turn");
        var fenceContent = BuildFenceContent(hash, "recent turn");
        await SeedMessagesAsync(db, SessionId, total, i =>
        {
            if (i == 24)
                return ("user", fenceContent);
            if (i < 24)
                return (i % 2 == 1 ? "user" : "agent", $"message {i}");
            return ("agent", $"assistant reply {i}");
        });

        var service = CreateService(options, dataPaths: null);
        var result = await service.CompactAsync(CreateRequest());

        // fence 位于保留窗口（i=24 > 26-6=20）→ 压缩照常进行，fence 行不被压缩。
        Assert.AreEqual(total - 6, result.CompactedMessageCount);
        Assert.IsFalse(result.SkippedDueToCurrentTurnGuard);

        await using var verify = new MemoryDbContext(options);
        Assert.AreEqual(total - 6, await verify.Messages.CountAsync(m => m.SessionId == SessionId && m.CompactedBy != null));
        var fenceRow = await verify.Messages.SingleAsync(m => m.SessionId == SessionId && m.Content == fenceContent);
        Assert.IsNull(fenceRow.CompactedBy);
        Assert.AreEqual(1, await verify.Messages.CountAsync(m => m.SessionId == SessionId && m.ContentType == "compact_summary"));
    }

    [TestMethod]
    public async Task FullCompact_Abort_WritesCompactionLogWithReason()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = CreateOptions(connection);
        await using var db = new MemoryDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var hash = ComputeInputHash("mid-3", "abort logging");
        await SeedMessagesAsync(db, SessionId, 26, i =>
        {
            if (i == 20)
                return ("user", BuildFenceContent(hash, "abort logging"));
            if (i < 20)
                return (i % 2 == 1 ? "user" : "agent", $"message {i}");
            return ("agent", $"assistant reply {i}");
        });

        using var temp = new TempDataRoot();
        var service = CreateService(options, dataPaths: temp.Paths);
        await service.CompactAsync(CreateRequest());

        var logPath = Path.Combine(temp.Paths.DiagnosticsLogsRoot, "compaction-log.jsonl");
        Assert.IsTrue(File.Exists(logPath));
        StringAssert.Contains(await File.ReadAllTextAsync(logPath), "current_turn_in_compaction_scope");
    }

    // ─────────────────────── 补救：DB 原始历史找回 ───────────────────────

    [TestMethod]
    public async Task Recovery_ByInputHash_FindsCompactedFenceRow_AndRestoresContentParts()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = CreateOptions(connection);
        await using var db = new MemoryDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var hash = ComputeInputHash("mid-4", "multimodal turn");
        var fence = BuildFenceContent(hash, "multimodal turn");
        var parts = ContentPartsEnvelope.Encode(new List<LlmContentPart> { new LlmTextPart(fence) });
        db.Sessions.Add(CreateSession(SessionId));
        db.Messages.Add(new MessageEntity
        {
            MessageId = "summary-1",
            SessionId = SessionId,
            Sequence = 2,
            Role = "system",
            ContentType = "compact_summary",
            Content = "summary",
            CreatedAt = 2,
        });
        db.Messages.Add(new MessageEntity
        {
            MessageId = "s-fence",
            SessionId = SessionId,
            Sequence = 3,
            Role = "user",
            ContentType = "text",
            Content = fence,
            AttachmentsJson = parts,
            CompactedBy = "summary-1",
            CreatedAt = 3,
        });
        await db.SaveChangesAsync();

        var recovered = await CurrentTurnDbRecovery.TryFindMessageByInputHashAsync(
            new TestMemoryDbContextFactory(options), null, SessionId, hash, NullLogger.Instance);

        Assert.IsNotNull(recovered);
        Assert.AreEqual(ChatRole.User, recovered.Role);
        StringAssert.Contains(recovered.Content, $"[CURRENT USER TURN input_sha256={hash}]");
        StringAssert.Contains(recovered.Content, $"[/CURRENT USER TURN input_sha256={hash}]");
        Assert.IsNotNull(recovered.ContentParts);
        Assert.IsTrue(recovered.ContentParts.OfType<LlmTextPart>().Any(p => p.Text.Contains(hash, StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task Recovery_ByInputHash_ReturnsNull_WhenAbsent()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = CreateOptions(connection);
        await using var db = new MemoryDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await SeedMessagesAsync(db, SessionId, 5, i => (i % 2 == 1 ? "user" : "agent", $"message {i}"));

        var recovered = await CurrentTurnDbRecovery.TryFindMessageByInputHashAsync(
            new TestMemoryDbContextFactory(options), null, SessionId,
            new string('b', 64), NullLogger.Instance);

        Assert.IsNull(recovered);
    }

    [TestMethod]
    public async Task Recovery_Tail_ReturnsRowsFromFenceOnward()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = CreateOptions(connection);
        await using var db = new MemoryDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var hash = ComputeInputHash("mid-5", "tail turn");
        await SeedMessagesAsync(db, SessionId, 8, i =>
        {
            if (i == 5)
                return ("user", BuildFenceContent(hash, "tail turn"));
            return (i % 2 == 1 ? "user" : "agent", $"message {i}");
        });

        var tail = await CurrentTurnDbRecovery.TryRecoverCurrentTurnTailAsync(
            new TestMemoryDbContextFactory(options), null, SessionId, "mid-5", null, NullLogger.Instance);

        Assert.AreEqual(4, tail.Count);
        Assert.AreEqual(ChatRole.User, tail[0].Role);
        StringAssert.Contains(tail[0].Content, "[CURRENT USER TURN input_sha256=");
    }

    // ─────────────── Ensure 包装：fail-closed 前的一次性恢复 ───────────────

    private static RuntimeDispatchRequest MakeRequest(string messageId, string text) => new()
    {
        SessionId = SessionId,
        AgentTemplateId = "template-1",
        MessageText = text,
        WorkspaceId = "workspace-1",
        MessageId = messageId,
    };

    [TestMethod]
    public async Task EnsureWithRecovery_PassesWithoutLookup_WhenFencePresent()
    {
        var request = MakeRequest("mid", "hello");
        var hash = ComputeInputHash("mid", "hello");
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "sys"),
            new(ChatRole.User, BuildFenceContent(hash, "hello")),
        };
        var lookupCalled = false;

        await AgentExecutionService.EnsureCurrentTurnInputPresentWithRecoveryAsync(
            messages, request,
            _ => { lookupCalled = true; return Task.FromResult<ChatMessage?>(null); },
            NullLogger.Instance,
            CancellationToken.None);

        Assert.IsFalse(lookupCalled);
        Assert.AreEqual(2, messages.Count);
    }

    [TestMethod]
    public async Task EnsureWithRecovery_InjectsRecoveredMessage_Idempotently()
    {
        var request = MakeRequest("mid", "hello");
        var hash = ComputeInputHash("mid", "hello");
        var messages = new List<ChatMessage> { new(ChatRole.System, "sys") };
        var recoveredMessage = new ChatMessage(ChatRole.User, BuildFenceContent(hash, "hello"));
        var lookupCalls = 0;

        await AgentExecutionService.EnsureCurrentTurnInputPresentWithRecoveryAsync(
            messages, request,
            _ => { lookupCalls++; return Task.FromResult<ChatMessage?>(recoveredMessage); },
            NullLogger.Instance,
            CancellationToken.None);

        Assert.AreEqual(1, messages.Count(m => m.Role == ChatRole.User));
        Assert.AreEqual(1, lookupCalls);

        // 幂等：围栏已在出站历史 → 第二次调用不再触发 lookup、不再注入。
        await AgentExecutionService.EnsureCurrentTurnInputPresentWithRecoveryAsync(
            messages, request,
            _ => { lookupCalls++; return Task.FromResult<ChatMessage?>(recoveredMessage); },
            NullLogger.Instance,
            CancellationToken.None);

        Assert.AreEqual(1, lookupCalls);
        Assert.AreEqual(2, messages.Count);
    }

    [TestMethod]
    public async Task EnsureWithRecovery_ThrowsFailClosed_WhenRecoveryReturnsNull()
    {
        var request = MakeRequest("mid", "hello");
        var messages = new List<ChatMessage> { new(ChatRole.User, "no fence here") };

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => AgentExecutionService.EnsureCurrentTurnInputPresentWithRecoveryAsync(
                messages, request,
                _ => Task.FromResult<ChatMessage?>(null),
                NullLogger.Instance,
                CancellationToken.None));

        Assert.AreEqual(1, messages.Count);
    }

    [TestMethod]
    public async Task EnsureWithRecovery_ThrowsFailClosed_WhenLookupThrows()
    {
        var request = MakeRequest("mid", "hello");
        var messages = new List<ChatMessage>();

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => AgentExecutionService.EnsureCurrentTurnInputPresentWithRecoveryAsync(
                messages, request,
                _ => throw new InvalidOperationException("db down"),
                NullLogger.Instance,
                CancellationToken.None));
    }

    [TestMethod]
    public async Task EnsureWithRecovery_Multimodal_PassesWithContentPartsCarryingFences()
    {
        var request = MakeRequest("mid", "multimodal");
        var hash = ComputeInputHash("mid", "multimodal");
        var fence = BuildFenceContent(hash, "multimodal");
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, fence, ContentParts: new List<LlmContentPart> { new LlmTextPart(fence) }),
        };

        await AgentExecutionService.EnsureCurrentTurnInputPresentWithRecoveryAsync(
            messages, request,
            _ => Task.FromResult<ChatMessage?>(null),
            NullLogger.Instance,
            CancellationToken.None);

        Assert.AreEqual(1, messages.Count);
    }

    // ───── CaptureCurrentTurn 解析失败的 DB 补救（TrimHistoryAsync 接线） ─────

    [TestMethod]
    public async Task CaptureWithRecovery_FallsBackToRawDb_WhenLiveFenceMissing()
    {
        // 事故1 Capture 失败象限：live history 的围栏消息已被摘要吞掉（最后一条
        // User 消息无围栏 → 解析失败），DB 原史仍保留围栏行 + 其后同轮行。
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = CreateOptions(connection);
        await using var db = new MemoryDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var hash = ComputeInputHash("mid-capture", "capture recovery turn");
        var fence = BuildFenceContent(hash, "capture recovery turn");
        await SeedMessagesAsync(db, SessionId, 4, i =>
        {
            if (i == 3)
                return ("user", fence);
            return (i % 2 == 1 ? "user" : "agent", $"message {i}");
        });

        var manager = CreateContextWindowManager(options);
        var liveHistory = new List<ChatMessage>
        {
            new(ChatRole.System, "sys"),
            new(ChatRole.User, "older ask without fence"),
        };

        var recovered = await manager.CaptureCurrentTurnWithRecoveryAsync(
            liveHistory, SessionId, $"{SessionId}-m3", null, CancellationToken.None);

        Assert.IsTrue(recovered.Count >= 1);
        Assert.AreEqual(ChatRole.User, recovered[0].Role);
        StringAssert.Contains(recovered[0].Content!, "[CURRENT USER TURN input_sha256=");
        StringAssert.Contains(recovered[0].Content!, $"[/CURRENT USER TURN input_sha256={hash}]");
    }

    [TestMethod]
    public async Task CaptureWithRecovery_ParsesLiveFence_WithoutDbLookup()
    {
        // live 解析成功 → 直接返回片段，不触发 DB 补救（factory 为 null 也不受影响）。
        var manager = CreateContextWindowManager(options: null);
        var hash = ComputeInputHash("mid-live", "live turn");
        var history = new List<ChatMessage>
        {
            new(ChatRole.System, "sys"),
            new(ChatRole.User, BuildFenceContent(hash, "live turn")),
            new(ChatRole.Assistant, "ack"),
        };

        var segment = await manager.CaptureCurrentTurnWithRecoveryAsync(
            history, SessionId, "mid-live", null, CancellationToken.None);

        Assert.AreEqual(2, segment.Count);
        StringAssert.Contains(segment[0].Content!, $"[CURRENT USER TURN input_sha256={hash}]");
    }

    [TestMethod]
    public async Task CaptureWithRecovery_ReturnsEmpty_WhenDbHasNoFenceRow()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = CreateOptions(connection);
        await using var db = new MemoryDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await SeedMessagesAsync(db, SessionId, 4, i => (i % 2 == 1 ? "user" : "agent", $"message {i}"));

        var manager = CreateContextWindowManager(options);
        var liveHistory = new List<ChatMessage>
        {
            new(ChatRole.System, "sys"),
            new(ChatRole.User, "older ask without fence"),
        };

        var recovered = await manager.CaptureCurrentTurnWithRecoveryAsync(
            liveHistory, SessionId, "missing-mid", null, CancellationToken.None);

        Assert.AreEqual(0, recovered.Count);
    }

    [TestMethod]
    public async Task CaptureWithRecovery_ReturnsEmpty_WithoutCurrentTurnIdentity()
    {
        // 无当前轮身份：维持原行为返回空，不触发 DB 补救（factory 为 null 也不抛）。
        var manager = CreateContextWindowManager(options: null);
        var liveHistory = new List<ChatMessage>
        {
            new(ChatRole.System, "sys"),
            new(ChatRole.User, "no fence"),
        };

        var recovered = await manager.CaptureCurrentTurnWithRecoveryAsync(
            liveHistory, SessionId, null, null, CancellationToken.None);

        Assert.AreEqual(0, recovered.Count);
    }

    // ─────────────────────────────── 帮助方法 ───────────────────────────────

    private static ContextWindowManager CreateContextWindowManager(DbContextOptions<MemoryDbContext>? options)
        => new(
            new AgentSessionManager(NullLogger<AgentSessionManager>.Instance),
            new InMemoryRuntimeSessionStore(),
            new ExecutionControlRegistry(),
            new ExecutionJournal(),
            NullLogger<ContextWindowManager>.Instance,
            memoryDbFactory: options is null ? null : new TestMemoryDbContextFactory(options));

    private static ContextCompactionService CreateService(
        DbContextOptions<MemoryDbContext> options,
        PuddingDataPaths? dataPaths)
        => new(
            new TestMemoryDbContextFactory(options),
            new FixedSummaryGenerator("## 用户目标\n测试摘要。"),
            NullLogger<ContextCompactionService>.Instance,
            contentSummaryService: null,
            dataPaths: dataPaths);

    private static ContextCompactionRequest CreateRequest()
        => new(
            WorkspaceId: "workspace-1",
            SessionId: SessionId,
            AgentId: "agent-1",
            Mode: ContextCompactionMode.Manual,
            Level: ContextCompactionLevel.Full,
            Reason: "P0-TXN Phase 1 regression");

    private static string BuildFenceContent(string hash, string body)
        => $"[CURRENT USER TURN input_sha256={hash}]\n{body}\n[/CURRENT USER TURN input_sha256={hash}]";

    private static string ComputeInputHash(string messageId, string messageText)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(messageId + "\n" + messageText)));

    private static async Task SeedMessagesAsync(
        MemoryDbContext db,
        string sessionId,
        int count,
        Func<int, (string Role, string Content)> factory)
    {
        if (!await db.Sessions.AnyAsync(session => session.SessionId == sessionId))
            db.Sessions.Add(CreateSession(sessionId));

        for (var i = 1; i <= count; i++)
        {
            var (role, content) = factory(i);
            db.Messages.Add(new MessageEntity
            {
                MessageId = $"{sessionId}-m{i}",
                SessionId = sessionId,
                Sequence = i,
                Role = role,
                ContentType = "text",
                Content = content,
                CreatedAt = i,
            });
        }

        await db.SaveChangesAsync();
    }

    private static SessionEntity CreateSession(string sessionId) => new()
    {
        SessionId = sessionId,
        WorkspaceId = "workspace-1",
        AgentId = "agent-1",
        CreatedAt = 1,
        LastActivityAt = 1,
    };

    private static DbContextOptions<MemoryDbContext> CreateOptions(SqliteConnection connection) =>
        new DbContextOptionsBuilder<MemoryDbContext>()
            .UseSqlite(connection)
            .Options;

    private sealed class TestMemoryDbContextFactory(DbContextOptions<MemoryDbContext> options) : IDbContextFactory<MemoryDbContext>
    {
        public MemoryDbContext CreateDbContext() => new(options);

        public Task<MemoryDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class FixedSummaryGenerator(string summary) : IContextCompactionSummaryGenerator
    {
        public Task<string> GenerateSummaryAsync(
            ContextCompactionSummaryRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(summary);
    }

    private sealed class TempDataRoot : IDisposable
    {
        public TempDataRoot()
        {
            Root = Path.Combine(Path.GetTempPath(), "pudding-current-turn-guard-tests", Guid.NewGuid().ToString("N"));
            Paths = PuddingDataPaths.FromRoot(Root);
        }

        public string Root { get; }
        public PuddingDataPaths Paths { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }
}
