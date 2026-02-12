using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingMemoryEngine.Data;
using PuddingMemoryEngine.Entities;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services;
using PuddingRuntime.Services;

namespace PuddingRuntimeTests.Services;

[TestClass]
public sealed class ContextCompactionContentSummaryTests
{
    [TestMethod]
    public async Task FullCompactAsync_WritesAgentContentSummary_WhenAgentIdIsPresent()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = CreateOptions(connection);
        await using var db = new MemoryDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await SeedMessagesAsync(db, "session-1", messageCount: 10);

        using var temp = new TempDataRoot();
        var contentSummary = new AgentContentSummaryService(temp.Paths, new ThrowingTextProcessingService());
        var service = new ContextCompactionService(
            new TestMemoryDbContextFactory(options),
            new FixedSummaryGenerator("## 用户目标\n保留早期关键决策。"),
            NullLogger<ContextCompactionService>.Instance,
            contentSummary);

        var result = await service.CompactAsync(new ContextCompactionRequest(
            WorkspaceId: "workspace-1",
            SessionId: "session-1",
            AgentId: "agent-1",
            Mode: ContextCompactionMode.Manual,
            Level: ContextCompactionLevel.Full,
            Reason: "manual slash command"));

        Assert.AreEqual(4, result.CompactedMessageCount);
        StringAssert.Contains(result.SummaryMarkdown, "保留早期关键决策");
        Assert.IsNotNull(result.Diagnostics);
        Assert.IsTrue(Guid.TryParse(result.Diagnostics.CompactionId, out _));
        Assert.AreEqual("session-1", result.Diagnostics.PreviousSessionId);
        Assert.AreEqual("msg-10", result.Diagnostics.PreviousLastMessageId);
        Assert.AreEqual(10, result.Diagnostics.ActiveMessageCountBefore);
        Assert.AreEqual(4, result.Diagnostics.CompactedMessageCount);
        Assert.AreEqual(result.SummaryMessageId, result.Diagnostics.SummaryMessageId);
        Assert.IsTrue(result.Diagnostics.SummaryCharacterCount > 0);
        Assert.IsTrue(result.Diagnostics.SummaryEstimatedTokens > 0);
        Assert.IsTrue(result.Diagnostics.DurationMs >= 0);
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.Diagnostics.CompletedAtUtc));
        var contentPath = temp.Paths.AgentInstanceContentSummaryFile("agent-1");
        Assert.IsTrue(File.Exists(contentPath));
        StringAssert.Contains(await File.ReadAllTextAsync(contentPath), "保留早期关键决策");
        var metadata = await contentSummary.ReadMetadataAsync("agent-1");
        Assert.IsNotNull(metadata);
        Assert.AreEqual("session-1", metadata.LastSessionId);
    }

    [TestMethod]
    public async Task FullCompactAsync_PublishesSessionCompressedHook()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = CreateOptions(connection);
        await using var db = new MemoryDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await SeedMessagesAsync(db, "session-hook", messageCount: 10);

        var hookPublisher = new RecordingHookPublisher();
        var service = new ContextCompactionService(
            new TestMemoryDbContextFactory(options),
            new FixedSummaryGenerator("## 摘要\nHook 系统需要知道压缩完成。"),
            NullLogger<ContextCompactionService>.Instance,
            hookPublisher: hookPublisher);

        var result = await service.CompactAsync(new ContextCompactionRequest(
            WorkspaceId: "workspace-1",
            SessionId: "session-hook",
            AgentId: "agent-1",
            Mode: ContextCompactionMode.Auto,
            Level: ContextCompactionLevel.Full,
            Reason: "auto threshold",
            CompactionId: "cmp-hook-1",
            AgentTemplateId: "template-1"));

        Assert.AreEqual(4, result.CompactedMessageCount);
        Assert.AreEqual(1, hookPublisher.Published.Count);

        var published = hookPublisher.Published.Single();
        Assert.AreEqual(HookEventNames.SessionCompressed, published.Name);
        var payload = Assert.IsInstanceOfType<SessionCompressedHookPayload>(published.Payload);
        Assert.AreEqual("workspace-1", payload.WorkspaceId);
        Assert.AreEqual("session-hook", payload.OriginalSessionId);
        Assert.AreEqual("agent-1", payload.AgentId);
        Assert.AreEqual("template-1", payload.AgentTemplateId);
        Assert.AreEqual("cmp-hook-1", payload.CompactionId);
        Assert.AreEqual("Auto", payload.Mode);
        Assert.AreEqual("Full", payload.Level);
        Assert.AreEqual("auto threshold", payload.Reason);
        Assert.AreEqual(10, payload.OriginalMessageCount);
        Assert.AreEqual(4, payload.DroppedMessageCount);
        Assert.IsFalse(string.IsNullOrWhiteSpace(payload.SummaryPreview));
        Assert.AreEqual("workspace-1", published.Options?.WorkspaceId);
        Assert.AreEqual("session-hook", published.Options?.SessionId);
        Assert.AreEqual("agent-1", published.Options?.AgentId);
        Assert.AreEqual("context_compaction", published.Options?.SourceId);
        Assert.AreEqual("context_compaction:cmp-hook-1", published.Options?.IdempotencyKey);
    }

    [TestMethod]
    public async Task FullCompactAsync_GeneratesSummary_WhenNoMessagesAreCompacted()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = CreateOptions(connection);
        await using var db = new MemoryDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await SeedMessagesAsync(db, "session-small", messageCount: 4);

        using var temp = new TempDataRoot();
        var contentSummary = new AgentContentSummaryService(temp.Paths, new ThrowingTextProcessingService());
        var generator = new RecordingSummaryGenerator("## 当前会话摘要\n当前会话正在排查手动压缩没有写入 content.md 的问题。");
        var service = new ContextCompactionService(
            new TestMemoryDbContextFactory(options),
            generator,
            NullLogger<ContextCompactionService>.Instance,
            contentSummary);

        var result = await service.CompactAsync(new ContextCompactionRequest(
            WorkspaceId: "workspace-1",
            SessionId: "session-small",
            AgentId: "agent-1",
            Mode: ContextCompactionMode.Manual,
            Level: ContextCompactionLevel.Full,
            Reason: "manual slash command"));

        Assert.AreEqual(0, result.CompactedMessageCount);
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.SummaryMessageId));
        StringAssert.Contains(result.SummaryMarkdown, "当前会话正在排查手动压缩");
        Assert.AreEqual(4, generator.LastMessages.Count);
        Assert.IsNotNull(result.Diagnostics);
        Assert.AreEqual(0, result.Diagnostics.CompactedMessageCount);
        Assert.AreEqual(4, result.Diagnostics.KeptRecentMessageCount);
        Assert.AreEqual(4, result.Diagnostics.SummaryInputMessageCount);
        Assert.IsTrue(result.Diagnostics.SummaryCharacterCount > 0);
        Assert.IsTrue(result.Diagnostics.SummaryEstimatedTokens > 0);

        await using var verifyDb = new MemoryDbContext(options);
        Assert.AreEqual(1, await verifyDb.Messages.CountAsync(m =>
            m.SessionId == "session-small" && m.ContentType == "compact_summary"));
        Assert.AreEqual(0, await verifyDb.Messages.CountAsync(m =>
            m.SessionId == "session-small" && m.CompactedBy != null));

        var contentPath = temp.Paths.AgentInstanceContentSummaryFile("agent-1");
        Assert.IsTrue(File.Exists(contentPath));
        StringAssert.Contains(
            await File.ReadAllTextAsync(contentPath),
            "当前会话正在排查手动压缩没有写入 content.md 的问题。");
        var metadata = await contentSummary.ReadMetadataAsync("agent-1");
        Assert.IsNotNull(metadata);
        Assert.AreEqual("session-small", metadata.LastSessionId);
    }

    [TestMethod]
    public async Task FullCompactAsync_ImportsOnlyCurrentSessionTranscript_WhenMemoryDbIsEmpty()
    {
        await using var memoryConnection = new SqliteConnection("Data Source=:memory:");
        await memoryConnection.OpenAsync();
        var memoryOptions = CreateOptions(memoryConnection);
        await using (var memoryDb = new MemoryDbContext(memoryOptions))
        {
            await memoryDb.Database.EnsureCreatedAsync();
        }

        await using var platformConnection = new SqliteConnection("Data Source=:memory:");
        await platformConnection.OpenAsync();
        var platformOptions = CreatePlatformOptions(platformConnection);
        await using (var platformDb = new PlatformDbContext(platformOptions))
        {
            await platformDb.Database.EnsureCreatedAsync();
            SeedTranscript(platformDb, "session-from-chat", "agent-1", messageCount: 10);
            SeedTranscript(platformDb, "other-session", "agent-1", messageCount: 3);
            await platformDb.SaveChangesAsync();
        }

        using var temp = new TempDataRoot();
        var contentSummary = new AgentContentSummaryService(temp.Paths, new ThrowingTextProcessingService());
        var service = new ContextCompactionService(
            new TestMemoryDbContextFactory(memoryOptions),
            new FixedSummaryGenerator("## 用户目标\n只压缩当前 session 窗口。"),
            NullLogger<ContextCompactionService>.Instance,
            contentSummary,
            new TestPlatformDbContextFactory(platformOptions));

        var result = await service.CompactAsync(new ContextCompactionRequest(
            WorkspaceId: "workspace-1",
            SessionId: "session-from-chat",
            AgentId: "agent-1",
            Mode: ContextCompactionMode.Manual,
            Level: ContextCompactionLevel.Full,
            Reason: "manual slash command"));

        Assert.AreEqual(4, result.CompactedMessageCount);
        var contentPath = temp.Paths.AgentInstanceContentSummaryFile("agent-1");
        Assert.IsTrue(File.Exists(contentPath));
        StringAssert.Contains(await File.ReadAllTextAsync(contentPath), "只压缩当前 session 窗口。");

        await using var verifyDb = new MemoryDbContext(memoryOptions);
        Assert.AreEqual(11, await verifyDb.Messages.CountAsync(m => m.SessionId == "session-from-chat"));
        Assert.AreEqual(0, await verifyDb.Messages.CountAsync(m => m.SessionId == "other-session"));
        Assert.AreEqual(1, await verifyDb.Messages.CountAsync(m =>
            m.SessionId == "session-from-chat" && m.ContentType == "compact_summary"));
        Assert.AreEqual(4, await verifyDb.Messages.CountAsync(m =>
            m.SessionId == "session-from-chat" && m.CompactedBy != null));
    }

    [TestMethod]
    public async Task FullCompactAsync_LimitsSummaryInputToLatestMaxMessages()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = CreateOptions(connection);
        await using var db = new MemoryDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await SeedMessagesAsync(db, "session-max", messageCount: 100);

        var generator = new RecordingSummaryGenerator("## 摘要\nmax window");
        var service = new ContextCompactionService(
            new TestMemoryDbContextFactory(options),
            generator,
            NullLogger<ContextCompactionService>.Instance);

        var result = await service.CompactAsync(new ContextCompactionRequest(
            WorkspaceId: "workspace-1",
            SessionId: "session-max",
            AgentId: "agent-1",
            Mode: ContextCompactionMode.Manual,
            Level: ContextCompactionLevel.Full,
            Reason: "manual slash command"));

        Assert.AreEqual(94, result.CompactedMessageCount);
        Assert.AreEqual(80, generator.LastMessages.Count);
        Assert.AreEqual(15, generator.LastMessages[0].Sequence);
        Assert.AreEqual(94, generator.LastMessages[^1].Sequence);
        StringAssert.Contains(result.SummaryMarkdown, "Sequence 15-94");
        StringAssert.Contains(result.SummaryMarkdown, "Sequence 1-14");
    }

    [TestMethod]
    public async Task FullCompactAsync_BackfillsEarlierWindow_WhenSummaryInputIsBelowMin()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = CreateOptions(connection);
        await using var db = new MemoryDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await SeedMessagesAsync(db, "session-min", messageCount: 30);
        db.Messages.Add(new MessageEntity
        {
            MessageId = "previous-summary",
            SessionId = "session-min",
            Sequence = 31,
            Role = "system",
            ContentType = "compact_summary",
            Content = "previous compact summary",
            Source = "context_compaction",
            CreatedAt = 31,
        });
        await db.SaveChangesAsync();

        var previouslyCompacted = await db.Messages
            .Where(m => m.SessionId == "session-min" && m.Sequence <= 20)
            .ToListAsync();
        foreach (var message in previouslyCompacted)
            message.CompactedBy = "previous-summary";
        await db.SaveChangesAsync();

        var generator = new RecordingSummaryGenerator("## 摘要\nmin window");
        var service = new ContextCompactionService(
            new TestMemoryDbContextFactory(options),
            generator,
            NullLogger<ContextCompactionService>.Instance);

        var result = await service.CompactAsync(new ContextCompactionRequest(
            WorkspaceId: "workspace-1",
            SessionId: "session-min",
            AgentId: "agent-1",
            Mode: ContextCompactionMode.Manual,
            Level: ContextCompactionLevel.Full,
            Reason: "manual slash command"));

        Assert.AreEqual(4, result.CompactedMessageCount);
        Assert.AreEqual(20, generator.LastMessages.Count);
        Assert.AreEqual(5, generator.LastMessages[0].Sequence);
        Assert.AreEqual(24, generator.LastMessages[^1].Sequence);
        StringAssert.Contains(result.SummaryMarkdown, "不足 MIN=20");
        StringAssert.Contains(result.SummaryMarkdown, "Sequence 5-20");
    }

    private static async Task SeedMessagesAsync(MemoryDbContext db, string sessionId, int messageCount)
    {
        db.Sessions.Add(new SessionEntity
        {
            SessionId = sessionId,
            WorkspaceId = "workspace-1",
            AgentId = "agent-1",
            Status = "Active",
            CreatedAt = 1,
            LastActivityAt = 1,
        });

        for (var i = 1; i <= messageCount; i++)
        {
            db.Messages.Add(new MessageEntity
            {
                MessageId = $"msg-{i}",
                SessionId = sessionId,
                Sequence = i,
                Role = i % 2 == 0 ? "agent" : "user",
                ContentType = "text",
                Content = $"message {i}",
                CreatedAt = i,
            });
        }

        await db.SaveChangesAsync();
    }

    private static void SeedTranscript(
        PlatformDbContext db,
        string sessionId,
        string agentId,
        int messageCount)
    {
        for (var i = 1; i <= messageCount; i++)
        {
            db.ChatMessages.Add(new ChatMessageEntity
            {
                SessionId = sessionId,
                WorkspaceId = "workspace-1",
                AgentInstanceId = agentId,
                AgentTemplateId = "template-1",
                Role = i % 2 == 0 ? "agent" : "user",
                Content = $"{sessionId} message {i}",
                CreatedAt = i,
            });
        }
    }

    private static DbContextOptions<MemoryDbContext> CreateOptions(SqliteConnection connection) =>
        new DbContextOptionsBuilder<MemoryDbContext>()
            .UseSqlite(connection)
            .Options;

    private static DbContextOptions<PlatformDbContext> CreatePlatformOptions(SqliteConnection connection) =>
        new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite(connection)
            .Options;

    private sealed class TestMemoryDbContextFactory(DbContextOptions<MemoryDbContext> options) : IDbContextFactory<MemoryDbContext>
    {
        public MemoryDbContext CreateDbContext() => new(options);

        public Task<MemoryDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class TestPlatformDbContextFactory(DbContextOptions<PlatformDbContext> options) : IDbContextFactory<PlatformDbContext>
    {
        public PlatformDbContext CreateDbContext() => new(options);

        public Task<PlatformDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class FixedSummaryGenerator(string summary) : IContextCompactionSummaryGenerator
    {
        public Task<string> GenerateSummaryAsync(
            ContextCompactionSummaryRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(summary);
    }

    private sealed class RecordingSummaryGenerator(string summary) : IContextCompactionSummaryGenerator
    {
        public IReadOnlyList<ContextCompactionMessage> LastMessages { get; private set; } = [];

        public Task<string> GenerateSummaryAsync(
            ContextCompactionSummaryRequest request,
            CancellationToken ct = default)
        {
            LastMessages = request.Messages;
            return Task.FromResult(summary);
        }
    }

    private sealed class RecordingHookPublisher : IHookPublisher
    {
        public List<PublishedHook> Published { get; } = [];

        public Task<string> PublishAsync<TPayload>(
            HookEventName name,
            TPayload payload,
            HookPublishOptions? options = null,
            CancellationToken ct = default)
        {
            Published.Add(new PublishedHook(name, payload, options));
            return Task.FromResult(Guid.NewGuid().ToString("N"));
        }
    }

    private sealed record PublishedHook(HookEventName Name, object? Payload, HookPublishOptions? Options);

    private sealed class ThrowingTextProcessingService : ISubconsciousTextProcessingService
    {
        public Task<string> SummarizeDailyLogAsync(DailyLogSummaryRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<string> SummarizeCurrentSessionAsync(CurrentSessionSummaryRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<string> CompressConversationAsync(ConversationCompressionRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class TempDataRoot : IDisposable
    {
        public TempDataRoot()
        {
            Root = Path.Combine(Path.GetTempPath(), "pudding-context-compaction-tests", Guid.NewGuid().ToString("N"));
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
