using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingMemoryEngine.Data;
using PuddingMemoryEngine.Entities;
using PuddingRuntime.Services;

namespace PuddingMemoryEngineTests;

[TestClass]
public sealed class ContextCompactionServiceTests
{
    [TestMethod]
    public void ContextHealthEvaluator_ReturnsCriticalWhenUsageExceedsCriticalThreshold()
    {
        var evaluator = new ContextHealthEvaluator();

        var health = evaluator.Evaluate(
            sessionId: "session-1",
            usedTokens: 160_000,
            contextWindowTokens: 200_000,
            maxOutputTokens: 20_000);

        Assert.AreEqual(ContextHealthState.Critical, health.State);
        Assert.AreEqual(180_000, health.EffectiveWindowTokens);
        Assert.AreEqual(20_000, health.RemainingTokens);
        Assert.IsTrue(health.ShouldAutoCompact);
        Assert.IsFalse(health.ShouldBlockSend);
    }

    [TestMethod]
    public async Task GetHealthAsync_UsesLatestOutboundContextUsageSnapshot_WhenAvailable()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = CreateOptions(connection);
        await using var db = new MemoryDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await SeedMessagesAsync(db, "session-usage", messageCount: 2);

        var usageStore = new ContextUsageSnapshotStore();
        usageStore.Set(new ContextUsageSnapshot
        {
            SessionId = "session-usage",
            UsedTokens = 90_000,
            Confidence = "estimated",
            Source = "llm_request",
            RecordedAt = DateTimeOffset.UtcNow,
        });
        var service = new ContextCompactionService(
            new TestMemoryDbContextFactory(options),
            new FixedSummaryGenerator("summary"),
            NullLogger<ContextCompactionService>.Instance,
            contextUsageSnapshotStore: usageStore);

        var health = await service.GetHealthAsync(
            "session-usage",
            contextWindowTokens: 130_000,
            maxOutputTokens: 20_000);

        Assert.AreEqual(90_000, health.UsedTokens);
        Assert.AreEqual(110_000, health.EffectiveWindowTokens);
        Assert.AreEqual(20_000, health.RemainingTokens);
        Assert.AreEqual(ContextHealthState.Critical, health.State);
        Assert.IsTrue(health.ShouldAutoCompact);
    }

    [TestMethod]
    public async Task GetHealthAsync_UsesProviderReportedUsage_WhenSnapshotIsUpdated()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = CreateOptions(connection);
        await using var db = new MemoryDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await SeedMessagesAsync(db, "session-provider-usage", messageCount: 2);

        var usageStore = new ContextUsageSnapshotStore();
        usageStore.CaptureLlmRequest(
            "session-provider-usage",
            [new ChatMessage(ChatRole.User, "short local estimate")],
            tools: null);
        usageStore.RecordProviderUsage(
            "session-provider-usage",
            new TokenUsageDto
            {
                PromptTokens = 150_000,
                CompletionTokens = 25_000,
                TotalTokens = 175_000,
            });

        var service = new ContextCompactionService(
            new TestMemoryDbContextFactory(options),
            new FixedSummaryGenerator("summary"),
            NullLogger<ContextCompactionService>.Instance,
            contextUsageSnapshotStore: usageStore);

        var health = await service.GetHealthAsync(
            "session-provider-usage",
            contextWindowTokens: 200_000,
            maxOutputTokens: 20_000);

        Assert.AreEqual(175_000, health.UsedTokens);
        Assert.AreEqual(5_000, health.RemainingTokens);
        Assert.AreEqual("provider_usage", health.UsageSource);
        Assert.AreEqual("provider_reported", health.UsageConfidence);
        Assert.AreEqual(150_000, health.ProviderPromptTokens);
        Assert.AreEqual(175_000, health.ProviderTotalTokens);
        Assert.IsTrue(health.ShouldAutoCompact);
    }

    [TestMethod]
    public void ContextHealthEvaluator_UsesExplicitProviderInputLimit()
    {
        var health = new ContextHealthEvaluator().Evaluate(
            sessionId: "session-qwen",
            usedTokens: 985_215,
            contextWindowTokens: 1_000_000,
            maxOutputTokens: 4_096,
            maxInputTokens: 983_616);

        Assert.AreEqual(983_616, health.EffectiveWindowTokens);
        Assert.AreEqual(0, health.RemainingTokens);
        Assert.AreEqual(ContextHealthState.Blocking, health.State);
        Assert.IsTrue(health.ShouldBlockSend);
    }

    [TestMethod]
    public async Task FullCompactAsync_WritesSummaryAndMarksOnlyOlderMessages()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = CreateOptions(connection);
        await using var db = new MemoryDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await SeedMessagesAsync(db, "session-1", messageCount: 10);

        var service = new ContextCompactionService(
            new TestMemoryDbContextFactory(options),
            new FixedSummaryGenerator("## 用户目标\n保留早期关键决策。"),
            NullLogger<ContextCompactionService>.Instance);

        var result = await service.CompactAsync(new ContextCompactionRequest(
            WorkspaceId: "workspace-1",
            SessionId: "session-1",
            AgentId: "agent-1",
            Mode: ContextCompactionMode.Manual,
            Level: ContextCompactionLevel.Full,
            Reason: "manual slash command"));

        Assert.AreEqual(ContextCompactionMode.Manual, result.Mode);
        Assert.AreEqual(ContextCompactionLevel.Full, result.Level);
        Assert.AreEqual(4, result.CompactedMessageCount);
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.SummaryMessageId));

        db.ChangeTracker.Clear();
        var messages = await db.Messages
            .AsNoTracking()
            .OrderBy(m => m.Sequence)
            .ToListAsync();
        var summary = messages.Single(m => m.MessageId == result.SummaryMessageId);
        Assert.AreEqual("system", summary.Role);
        Assert.AreEqual("compact_summary", summary.ContentType);
        Assert.AreEqual("context_compaction", summary.Source);
        StringAssert.Contains(summary.Content, "保留早期关键决策");

        var compacted = messages
            .Where(m => m.CompactedBy == result.SummaryMessageId)
            .OrderBy(m => m.Sequence)
            .ToList();
        CollectionAssert.AreEqual(new long[] { 1, 2, 3, 4 }, compacted.Select(m => m.Sequence).ToArray());

        var retained = messages
            .Where(m => m.ContentType == "text" && m.CompactedBy is null)
            .OrderBy(m => m.Sequence)
            .Select(m => m.Sequence)
            .ToArray();
        CollectionAssert.AreEqual(new long[] { 5, 6, 7, 8, 9, 10 }, retained);
    }

    [TestMethod]
    public async Task FullCompactAsync_EvictsOversizedToolOutputInRetentionWindow()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = CreateOptions(connection);
        await using var db = new MemoryDbContext(options);
        await db.Database.EnsureCreatedAsync();

        // 模拟事故现场：保留窗口内一条数百 KB 的巨型 tool_output（grep 命中 minified JS bundle）
        var hugeToolOutput = new string('x', 200_000);
        await SeedMessagesAsync(db, "session-evict", messageCount: 10, lastMessageContent: hugeToolOutput);

        var generator = new CapturingSummaryGenerator();
        var service = new ContextCompactionService(
            new TestMemoryDbContextFactory(options),
            generator,
            NullLogger<ContextCompactionService>.Instance,
            options: new ContextCompactionOptions()); // 默认 MaxVerbatimMessageBytes = 16KB

        var result = await service.CompactAsync(new ContextCompactionRequest(
            WorkspaceId: "workspace-1",
            SessionId: "session-evict",
            AgentId: "agent-1",
            Mode: ContextCompactionMode.Manual,
            Level: ContextCompactionLevel.Full,
            Reason: "test oversized tool_output eviction"));

        // 逐条保留数量逻辑不变：仍只压缩前 4 条（10 - RecentMessagesToKeep=6）
        Assert.AreEqual(4, result.CompactedMessageCount);

        db.ChangeTracker.Clear();
        var messages = await db.Messages
            .AsNoTracking()
            .Where(m => m.SessionId == "session-evict")
            .OrderBy(m => m.Sequence)
            .ToListAsync();

        // 保留窗口仍为 5..10（未被压缩标记，逐条保留数量逻辑不变）
        var retained = messages
            .Where(m => m.ContentType == "text" && m.CompactedBy is null)
            .Select(m => m.Sequence)
            .ToArray();
        CollectionAssert.AreEqual(new long[] { 5, 6, 7, 8, 9, 10 }, retained);

        // 巨型 tool_output 不再原样保留全文：截断为 头部摘要 + 截断标记
        var evicted = messages.Single(m => m.Sequence == 10);
        Assert.IsTrue(
            evicted.Content!.Length < 10_000,
            $"巨型 tool_output 应被截断而非原样保留（实际长度 {evicted.Content.Length}）。");
        StringAssert.Contains(evicted.Content, "截断标记");
        StringAssert.Contains(evicted.Content, "session_event_log");
        StringAssert.Contains(evicted.Content, "200000"); // 标记注明原始大小
        Assert.IsFalse(
            evicted.Content.Contains(new string('x', 100_000), StringComparison.Ordinal),
            "截断后的保留副本不应包含巨型原文。");

        // 被截断部分照常参与摘要侧处理：摘要生成器应收到完整原文
        Assert.IsNotNull(generator.LastRequest);
        var fullInput = generator.LastRequest!.Single(m => m.Sequence == 10).Content;
        Assert.AreEqual(hugeToolOutput, fullInput);
    }

    [TestMethod]
    public async Task FullCompactAsync_NormalSizedRecentMessages_StayVerbatim()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = CreateOptions(connection);
        await using var db = new MemoryDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await SeedMessagesAsync(db, "session-normal", messageCount: 10, contentPaddingChars: 300);

        var service = new ContextCompactionService(
            new TestMemoryDbContextFactory(options),
            new FixedSummaryGenerator("## 用户目标\n保留早期关键决策。"),
            NullLogger<ContextCompactionService>.Instance,
            options: new ContextCompactionOptions()); // 默认阈值 16KB

        var result = await service.CompactAsync(new ContextCompactionRequest(
            WorkspaceId: "workspace-1",
            SessionId: "session-normal",
            AgentId: "agent-1",
            Mode: ContextCompactionMode.Manual,
            Level: ContextCompactionLevel.Full,
            Reason: "test normal messages verbatim"));

        Assert.AreEqual(4, result.CompactedMessageCount);

        db.ChangeTracker.Clear();
        var messages = await db.Messages
            .AsNoTracking()
            .Where(m => m.SessionId == "session-normal")
            .OrderBy(m => m.Sequence)
            .ToListAsync();

        // 尺寸正常的最近消息仍按原规则原样保留，内容不被改动
        var retained = messages
            .Where(m => m.ContentType == "text" && m.CompactedBy is null)
            .OrderBy(m => m.Sequence)
            .ToList();
        var expectedPadding = " " + new string('y', 300);
        CollectionAssert.AreEqual(
            new[] { 5, 6, 7, 8, 9, 10 }.Select(n => $"message {n}{expectedPadding}").ToArray(),
            retained.Select(m => m.Content).ToArray());
    }

    [TestMethod]
    public async Task FullCompactAsync_ZeroThreshold_FallsBackToLegacyVerbatimBehavior()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = CreateOptions(connection);
        await using var db = new MemoryDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var hugeToolOutput = new string('x', 100_000);
        await SeedMessagesAsync(db, "session-legacy", messageCount: 10, lastMessageContent: hugeToolOutput, contentPaddingChars: 300);

        var generator = new CapturingSummaryGenerator();
        var service = new ContextCompactionService(
            new TestMemoryDbContextFactory(options),
            generator,
            NullLogger<ContextCompactionService>.Instance,
            options: new ContextCompactionOptions { MaxVerbatimMessageBytes = 0 }); // 禁用驱逐 → 旧行为

        var result = await service.CompactAsync(new ContextCompactionRequest(
            WorkspaceId: "workspace-1",
            SessionId: "session-legacy",
            AgentId: "agent-1",
            Mode: ContextCompactionMode.Manual,
            Level: ContextCompactionLevel.Full,
            Reason: "test zero threshold legacy behavior"));

        Assert.AreEqual(4, result.CompactedMessageCount);

        db.ChangeTracker.Clear();
        var messages = await db.Messages
            .AsNoTracking()
            .Where(m => m.SessionId == "session-legacy")
            .OrderBy(m => m.Sequence)
            .ToListAsync();

        // 旧行为：巨型消息仍原样保留全文，内容不被截断
        var retainedGiant = messages.Single(m => m.Sequence == 10);
        Assert.AreEqual(hugeToolOutput, retainedGiant.Content);

        // 旧行为：摘要侧输入不出现完整原文克隆
        Assert.IsNotNull(generator.LastRequest);
        Assert.IsFalse(
            generator.LastRequest!.Any(m => m.Sequence == 10),
            "阈值=0 时不应把保留窗口消息注入摘要侧输入。");
    }

        private static async Task SeedMessagesAsync(
        MemoryDbContext db,
        string sessionId,
        int messageCount,
        string? lastMessageContent = null,
        int contentPaddingChars = 0)
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
            var padding = contentPaddingChars > 0 ? " " + new string('y', contentPaddingChars) : string.Empty;
            var content = i == messageCount && lastMessageContent is not null
                ? lastMessageContent
                : $"message {i}{padding}";
            db.Messages.Add(new MessageEntity
            {
                MessageId = $"msg-{i}",
                SessionId = sessionId,
                Sequence = i,
                Role = i % 2 == 0 ? "agent" : "user",
                ContentType = "text",
                Content = content,
                CreatedAt = i,
            });
        }

        await db.SaveChangesAsync();
    }

    private static DbContextOptions<MemoryDbContext> CreateOptions(SqliteConnection connection) =>
        new DbContextOptionsBuilder<MemoryDbContext>()
            .UseSqlite(connection)
            .Options;

    private sealed class TestMemoryDbContextFactory : IDbContextFactory<MemoryDbContext>
    {
        private readonly DbContextOptions<MemoryDbContext> _options;

        public TestMemoryDbContextFactory(DbContextOptions<MemoryDbContext> options)
        {
            _options = options;
        }

        public MemoryDbContext CreateDbContext() => new(_options);

        public Task<MemoryDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

        [TestMethod]
    public async Task FullCompactAsync_SkipWhenSummaryIncreasesTokens_SkipsCompaction()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = CreateOptions(connection);
        await using var db = new MemoryDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await SeedMessagesAsync(db, "session-skip", messageCount: 30);

        // Generate a summary that is deliberately longer than the input messages
        // Each seeded message is "message N" (~9 chars). 30 messages = ~270 chars.
        // Create a summary >1000 chars to exceed the token count of the input.
        var longSummary = new string('x', 2000);

        var compactionOptions = new ContextCompactionOptions
        {
            SkipWhenSummaryIncreasesTokens = true,
        };

        var service = new ContextCompactionService(
            new TestMemoryDbContextFactory(options),
            new FixedSummaryGenerator(longSummary),
            NullLogger<ContextCompactionService>.Instance,
            options: compactionOptions);

        var result = await service.CompactAsync(new ContextCompactionRequest(
            WorkspaceId: "workspace-1",
            SessionId: "session-skip",
            AgentId: "agent-1",
            Mode: ContextCompactionMode.Manual,
            Level: ContextCompactionLevel.Full,
            Reason: "test skip when summary increases tokens"));

        // Assert: compaction was skipped
        Assert.IsTrue(result.SkippedDueToTokenIncrease);
        Assert.AreEqual(0, result.CompactedMessageCount);
        Assert.AreEqual(string.Empty, result.SummaryMessageId);
        Assert.AreEqual(result.BeforeTokens, result.AfterTokens);

        // Verify no summary message was written to DB
        db.ChangeTracker.Clear();
        var messages = await db.Messages
            .AsNoTracking()
            .Where(m => m.SessionId == "session-skip")
            .ToListAsync();
        Assert.AreEqual(30, messages.Count(m => m.ContentType == "text"));
        Assert.AreEqual(0, messages.Count(m => m.ContentType == "compact_summary"));

        // Verify no messages were marked as compacted
        Assert.AreEqual(0, messages.Count(m => m.CompactedBy != null));
    }

    [TestMethod]
    public async Task FullCompactAsync_SkipWhenSummaryIncreasesTokens_Disabled_CompactsNormally()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = CreateOptions(connection);
        await using var db = new MemoryDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await SeedMessagesAsync(db, "session-noskip", messageCount: 30);

        var longSummary = new string('x', 2000);

        var compactionOptions = new ContextCompactionOptions
        {
            SkipWhenSummaryIncreasesTokens = false, // explicitly disabled
        };

        var service = new ContextCompactionService(
            new TestMemoryDbContextFactory(options),
            new FixedSummaryGenerator(longSummary),
            NullLogger<ContextCompactionService>.Instance,
            options: compactionOptions);

        var result = await service.CompactAsync(new ContextCompactionRequest(
            WorkspaceId: "workspace-1",
            SessionId: "session-noskip",
            AgentId: "agent-1",
            Mode: ContextCompactionMode.Manual,
            Level: ContextCompactionLevel.Full,
            Reason: "test no-skip when disabled"));

        // Assert: compaction was NOT skipped
        Assert.IsFalse(result.SkippedDueToTokenIncrease);
        Assert.IsTrue(result.CompactedMessageCount > 0);
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.SummaryMessageId));
    }

    [TestMethod]
    public async Task FullCompactAsync_SummaryShorterThanInput_CompactsNormally()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = CreateOptions(connection);
        await using var db = new MemoryDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await SeedMessagesAsync(db, "session-good", messageCount: 30);

        // Summary is short — should NOT be skipped
        var shortSummary = "short summary";

        var compactionOptions = new ContextCompactionOptions
        {
            SkipWhenSummaryIncreasesTokens = true,
        };

        var service = new ContextCompactionService(
            new TestMemoryDbContextFactory(options),
            new FixedSummaryGenerator(shortSummary),
            NullLogger<ContextCompactionService>.Instance,
            options: compactionOptions);

        var result = await service.CompactAsync(new ContextCompactionRequest(
            WorkspaceId: "workspace-1",
            SessionId: "session-good",
            AgentId: "agent-1",
            Mode: ContextCompactionMode.Manual,
            Level: ContextCompactionLevel.Full,
            Reason: "test normal compaction"));

        // Assert: compaction was NOT skipped
        Assert.IsFalse(result.SkippedDueToTokenIncrease);
        Assert.IsTrue(result.CompactedMessageCount > 0);
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.SummaryMessageId));
    }

    [TestMethod]
    public async Task ExtractiveGenerator_FiltersNoiseMessages()
    {
        var generator = new ExtractiveContextCompactionSummaryGenerator();
        var messages = new List<ContextCompactionMessage>
        {
            new("m1", 1, "user", "请把上下文压缩管线修好，项目在 E:\\repo\\app。"),
            new("m2", 2, "agent", "Runtime mode is now Yolo mode; 这是一条系统噪声。"),
            new("m3", 3, "user", "/yolo"),
            new("m4", 4, "agent", $"{AgentExecutionConstants.DuplicateMessagePlaceholder} 不要重复这条。"),
            new("m5", 5, "agent", "已完成 ContextCompactionService.cs:120 的重构。"),
        };

        var summary = await generator.GenerateSummaryAsync(
            new ContextCompactionSummaryRequest("workspace-1", "session-1", "agent-1", messages, "test"));

        StringAssert.Contains(summary, "E:\\repo\\app");
        StringAssert.Contains(summary, "ContextCompactionService.cs:120");
        Assert.IsFalse(summary.Contains("/yolo", StringComparison.OrdinalIgnoreCase), "不应包含 /yolo 噪声。");
        Assert.IsFalse(summary.Contains("duplicate message", StringComparison.OrdinalIgnoreCase), "不应包含 duplicate 占位噪声。");
        Assert.IsFalse(summary.Contains("Runtime mode is now Yolo", StringComparison.OrdinalIgnoreCase), "不应包含 Runtime mode 噪声。");
    }

    [TestMethod]
    public async Task ExtractiveGenerator_PreferenceAndMemoryNotesSections_DoNotShareSnippets()
    {
        var generator = new ExtractiveContextCompactionSummaryGenerator();
        var messages = new List<ContextCompactionMessage>();
        for (var i = 1; i <= 4; i++)
        {
            messages.Add(new ContextCompactionMessage(
                $"u{i}", i * 2 - 1, "user", $"用户偏好 {i}：希望输出使用中文，路径是 E:\\proj\\src\\File{i}.cs。"));
            messages.Add(new ContextCompactionMessage(
                $"a{i}", i * 2, "agent", $"好的，已按偏好处理消息 {i}。"));
        }

        var summary = await generator.GenerateSummaryAsync(
            new ContextCompactionSummaryRequest("workspace-1", "session-1", "agent-1", messages, "test"));

        var preferenceBullets = ExtractSectionBullets(summary, "## 保留的用户偏好和约束");
        var memoryNoteBullets = ExtractSectionBullets(summary, "## Memory Notes");

        Assert.IsTrue(preferenceBullets.Count > 0, "偏好章节应有摘录。");
        Assert.IsTrue(memoryNoteBullets.Count > 0, "Memory Notes 应有结构化事实。");
        var overlap = preferenceBullets.Intersect(memoryNoteBullets, StringComparer.Ordinal).ToList();
        Assert.AreEqual(0, overlap.Count, "偏好章节与 Memory Notes 不应重复同一批 snippets。");
        StringAssert.Contains(summary, "涉及文件");
    }

    [TestMethod]
    public async Task ExtractiveGenerator_ExtractsWindowsAbsolutePaths()
    {
        var generator = new ExtractiveContextCompactionSummaryGenerator();
        var messages = new List<ContextCompactionMessage>
        {
            new("m1", 1, "user", "问题在 E:\\github\\AgentNetworkPlan\\PuddingAgent\\Source\\PuddingRuntime\\Services\\ContextCompactionService.cs。"),
            new("m2", 2, "agent", "好的，我来检查该文件。"),
        };

        var summary = await generator.GenerateSummaryAsync(
            new ContextCompactionSummaryRequest("workspace-1", "session-1", "agent-1", messages, "test"));

        var locationSection = ExtractSectionText(summary, "## 涉及文件和代码位置");
        StringAssert.Contains(
            locationSection,
            "E:\\github\\AgentNetworkPlan\\PuddingAgent\\Source\\PuddingRuntime\\Services\\ContextCompactionService.cs");
    }

    private static List<string> ExtractSectionBullets(string summary, string sectionHeader)
    {
        var lines = summary.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var inSection = false;
        var bullets = new List<string>();
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                inSection = line.Equals(sectionHeader, StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (inSection && line.StartsWith("- ", StringComparison.Ordinal))
                bullets.Add(line);
        }

        return bullets;
    }

    private static string ExtractSectionText(string summary, string sectionHeader)
    {
        var lines = summary.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var inSection = false;
        var sb = new System.Text.StringBuilder();
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                if (line.Equals(sectionHeader, StringComparison.OrdinalIgnoreCase))
                {
                    inSection = true;
                    continue;
                }

                if (inSection)
                    break;
                continue;
            }

            if (inSection)
                sb.AppendLine(line);
        }

        return sb.ToString();
    }
    private sealed class CapturingSummaryGenerator : IContextCompactionSummaryGenerator
    {
        public IReadOnlyList<ContextCompactionMessage>? LastRequest { get; private set; }

        public Task<string> GenerateSummaryAsync(
            ContextCompactionSummaryRequest request,
            CancellationToken ct = default)
        {
            LastRequest = request.Messages;
            return Task.FromResult("## 用户目标\n保留早期关键决策。");
        }
    }

    private sealed class FixedSummaryGenerator : IContextCompactionSummaryGenerator
    {
        private readonly string _summary;

        public FixedSummaryGenerator(string summary)
        {
            _summary = summary;
        }

        public Task<string> GenerateSummaryAsync(
            ContextCompactionSummaryRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(_summary);
    }
}
