using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Platform;
using PuddingConversationReadModel;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services.AgentChat;

namespace PuddingPlatformTests.Services;

/// <summary>
/// 阶段 C3a：统一打开/历史/过程分页查询的有界读取与一致性合同（真实 SQLite 临时库）。
/// </summary>
[TestClass]
public sealed class ConversationOpenQueryServiceTests
{
    [TestMethod]
    public async Task OpenAsync_ReturnsBoundedRecentPageAndConsistentCursors()
    {
        await using var temp = TempQueryDb.Create();
        await using (var db = new PlatformDbContext(temp.Options))
        {
            await db.Database.EnsureCreatedAsync();
            await SeedConversationAsync(db, messageCount: 25, checkpoint: 10, head: 10);
            await db.SaveChangesAsync();
        }

        var service = NewService(temp);
        var snapshot = await service.OpenAsync("conv-1", messageLimit: 20);

        Assert.AreEqual("conv-1", snapshot.ConversationId);
        Assert.AreEqual(10L, snapshot.SnapshotCursor);
        Assert.AreEqual(10L, snapshot.EventHead);
        Assert.AreEqual(ReadModelInitializationState.Ready, snapshot.Initialization);
        Assert.AreEqual(ConversationSyncState.Ready, snapshot.SyncState);
        Assert.IsNull(snapshot.RetryAfterMs);

        Assert.HasCount(20, snapshot.History.Items);
        Assert.IsTrue(snapshot.History.HasMore);
        Assert.AreEqual("msg-25", snapshot.History.Items[0].MessageId, "newest first");
        Assert.AreEqual("msg-06", snapshot.History.Items[^1].MessageId);
        Assert.AreEqual(snapshot.History.Items[^1].CreatedAt, snapshot.History.NextBeforeCreatedAt);
        Assert.AreEqual("msg-06", snapshot.History.NextBeforeMessageId);

        Assert.IsNotNull(snapshot.ActiveTurn);
        Assert.AreEqual("turn-2", snapshot.ActiveTurn.TurnId);
        Assert.IsTrue(snapshot.ActiveTurn.HasAssistantBody);
        Assert.AreEqual(ReadModelInitializationState.Ready, snapshot.Runtime.Initialization);
        Assert.AreEqual(7, snapshot.Runtime.Revision);
        Assert.IsNotEmpty(snapshot.RepresentationTag);
        Assert.AreEqual(SummaryCompleteness.Complete, snapshot.Completeness);
    }

    [TestMethod]
    public async Task OpenAsync_LargeMessage_TruncatesPreviewWithoutWholeBody()
    {
        await using var temp = TempQueryDb.Create();
        await using (var db = new PlatformDbContext(temp.Options))
        {
            await db.Database.EnsureCreatedAsync();
            db.ChatMessages.Add(Message("msg-big", "agent", new string('中', 10_000), 1_000, "turn-1"));
            await db.SaveChangesAsync();
        }

        var service = NewService(temp);
        var snapshot = await service.OpenAsync("conv-1");

        Assert.HasCount(1, snapshot.History.Items);
        var item = snapshot.History.Items[0];
        Assert.IsTrue(Encoding.UTF8.GetByteCount(item.Preview) <= 8 * 1024);
        Assert.IsTrue(item.HasMoreContent);
        Assert.AreEqual(10_000, item.ContentLength);
    }

    [TestMethod]
    public async Task OpenAsync_LegacyCheckpointWithoutReadState_ReturnsPartial()
    {
        await using var temp = TempQueryDb.Create();
        await using (var db = new PlatformDbContext(temp.Options))
        {
            await db.Database.EnsureCreatedAsync();
            db.ConversationProjectionCheckpoints.Add(new ConversationProjectionCheckpointEntity
            {
                ConversationId = "conv-1",
                ProjectedThrough = 50,
                UpdatedAt = DateTimeOffset.UtcNow.ToString("O"),
            });
            db.ConversationHeads.Add(new ConversationHeadEntity { ConversationId = "conv-1", HeadSequence = 60 });
            db.ChatMessages.Add(Message("msg-1", "agent", "legacy", 1_000, "turn-1"));
            await db.SaveChangesAsync();
        }

        var service = NewService(temp);
        var snapshot = await service.OpenAsync("conv-1");

        Assert.AreEqual(ReadModelInitializationState.Partial, snapshot.Initialization);
        Assert.AreEqual(ReadModelInitializationState.Partial, snapshot.Runtime.Initialization);
        Assert.AreEqual(SummaryCompleteness.Partial, snapshot.Completeness);
        Assert.AreEqual("baseline_incomplete", snapshot.CompletenessReason);
        Assert.AreEqual(ConversationSyncState.Ready, snapshot.SyncState);
        Assert.AreEqual(50L, snapshot.SnapshotCursor);
        Assert.AreEqual(60L, snapshot.EventHead);
        Assert.HasCount(1, snapshot.History.Items);
    }

    [TestMethod]
    public async Task OpenAsync_CheckpointZeroHeadPositive_ReturnsSyncing()
    {
        await using var temp = TempQueryDb.Create();
        await using (var db = new PlatformDbContext(temp.Options))
        {
            await db.Database.EnsureCreatedAsync();
            db.ConversationHeads.Add(new ConversationHeadEntity { ConversationId = "conv-1", HeadSequence = 5 });
            await db.SaveChangesAsync();
        }

        var service = NewService(temp);
        var snapshot = await service.OpenAsync("conv-1");

        Assert.AreEqual(ConversationSyncState.Syncing, snapshot.SyncState);
        Assert.AreEqual(500, snapshot.RetryAfterMs);
        Assert.AreEqual(0L, snapshot.SnapshotCursor);
        Assert.AreEqual(5L, snapshot.EventHead);
    }

    [TestMethod]
    public async Task GetHistoryPageAsync_KeysetPaginatesAndRejectsForeignCursor()
    {
        await using var temp = TempQueryDb.Create();
        await using (var db = new PlatformDbContext(temp.Options))
        {
            await db.Database.EnsureCreatedAsync();
            for (var index = 1; index <= 30; index++)
                db.ChatMessages.Add(Message($"msg-{index:D2}", "agent", $"content-{index}", 1_000 + index, "turn-1"));
            await db.SaveChangesAsync();
        }

        var service = NewService(temp);
        var first = await service.GetHistoryPageAsync(new ConversationHistoryQuery("conv-1", Limit: 10));
        Assert.HasCount(10, first.Items);
        Assert.IsTrue(first.HasMore);
        Assert.AreEqual("msg-30", first.Items[0].MessageId);
        Assert.AreEqual("msg-21", first.Items[^1].MessageId);
        Assert.AreEqual(1_021, first.NextBeforeCreatedAt);
        Assert.AreEqual("msg-21", first.NextBeforeMessageId);

        var second = await service.GetHistoryPageAsync(new ConversationHistoryQuery(
            "conv-1",
            BeforeCreatedAt: first.NextBeforeCreatedAt,
            BeforeMessageId: first.NextBeforeMessageId,
            Limit: 10));
        Assert.HasCount(10, second.Items);
        Assert.AreEqual("msg-20", second.Items[0].MessageId);
        Assert.AreEqual("msg-11", second.Items[^1].MessageId);

        Assert.ThrowsExactly<ConversationQueryException>(() =>
            service.GetHistoryPageAsync(new ConversationHistoryQuery(
                "conv-1",
                BeforeCreatedAt: 1_010,
                BeforeMessageId: "missing-message",
                Limit: 10)).GetAwaiter().GetResult());
    }

    [TestMethod]
    public async Task GetProcessPageAsync_ScansDisplayableTailAndAdvancesOverNonDisplayable()
    {
        await using var temp = TempQueryDb.Create();
        await using (var db = new PlatformDbContext(temp.Options))
        {
            await db.Database.EnsureCreatedAsync();
            for (var sequence = 1; sequence <= 5; sequence++)
                AddEvent(db, sequence, ConversationEventTypes.MessageContentAppended, """{"delta":"chunk"}""");
            AddEvent(db, 6, ConversationEventTypes.UsageRecorded, """{"usage":{}}""");
            AddEvent(db, 7, ConversationEventTypes.ToolCallCompleted, """{"name":"read","output":"ok"}""");
            db.ConversationHeads.Add(new ConversationHeadEntity { ConversationId = "conv-1", HeadSequence = 7 });
            await db.SaveChangesAsync();
        }

        var service = NewService(temp);
        var page = await service.GetProcessPageAsync(new ConversationProcessQuery("conv-1", Limit: 2));

        Assert.HasCount(2, page.Items);
        Assert.AreEqual(5L, page.Items[0].Sequence);
        Assert.AreEqual(7L, page.Items[1].Sequence);
        Assert.AreEqual(1, page.OmittedNonDisplayable);
        Assert.AreEqual("ItemLimit", page.Truncation);
        Assert.AreEqual(5L, page.ScannedBackThroughSequence);
        Assert.IsTrue(page.HasMoreBefore);
        Assert.AreEqual(5L, page.NextBeforeSequence);

        var older = await service.GetProcessPageAsync(new ConversationProcessQuery("conv-1", BeforeSequence: 5, Limit: 10));
        Assert.HasCount(4, older.Items);
        Assert.AreEqual(1L, older.Items[0].Sequence);
        Assert.AreEqual(4L, older.Items[^1].Sequence);
        Assert.IsFalse(older.HasMoreBefore);
    }

    [TestMethod]
    public async Task GetProcessPageAsync_EmptyDisplayablePageStillAdvancesBoundary()
    {
        await using var temp = TempQueryDb.Create();
        await using (var db = new PlatformDbContext(temp.Options))
        {
            await db.Database.EnsureCreatedAsync();
            AddEvent(db, 1, ConversationEventTypes.UsageRecorded, "{}");
            AddEvent(db, 2, ConversationEventTypes.UsageRecorded, "{}");
            AddEvent(db, 3, ConversationEventTypes.UsageRecorded, "{}");
            db.ConversationHeads.Add(new ConversationHeadEntity { ConversationId = "conv-1", HeadSequence = 3 });
            await db.SaveChangesAsync();
        }

        var service = NewService(temp);
        var page = await service.GetProcessPageAsync(new ConversationProcessQuery("conv-1", Limit: 10));

        Assert.IsEmpty(page.Items);
        Assert.AreEqual(3, page.OmittedNonDisplayable);
        Assert.AreEqual(1L, page.ScannedBackThroughSequence);
        Assert.AreEqual(1L, page.NextBeforeSequence);
        Assert.IsFalse(page.HasMoreBefore);
    }

    [TestMethod]
    public async Task GetProcessPageAsync_RejectsOutOfRangeCursor()
    {
        await using var temp = TempQueryDb.Create();
        await using (var db = new PlatformDbContext(temp.Options))
        {
            await db.Database.EnsureCreatedAsync();
            AddEvent(db, 1, ConversationEventTypes.MessageContentAppended, """{"delta":"a"}""");
            db.ConversationHeads.Add(new ConversationHeadEntity { ConversationId = "conv-1", HeadSequence = 1 });
            await db.SaveChangesAsync();
        }

        var service = NewService(temp);

        var throughAhead = await Assert.ThrowsExactlyAsync<ConversationQueryException>(() =>
            service.GetProcessPageAsync(new ConversationProcessQuery("conv-1", ThroughSequence: 5)));
        Assert.AreEqual("invalid_cursor", throughAhead.Code);

        var beforeAhead = await Assert.ThrowsExactlyAsync<ConversationQueryException>(() =>
            service.GetProcessPageAsync(new ConversationProcessQuery("conv-1", BeforeSequence: 10, ThroughSequence: 5)));
        Assert.AreEqual("invalid_cursor", beforeAhead.Code);
    }

    [TestMethod]
    public async Task OpenAsync_ExposesCompactionAndBoundedSubAgentSummary()
    {
        await using var temp = TempQueryDb.Create();
        await using (var db = new PlatformDbContext(temp.Options))
        {
            await db.Database.EnsureCreatedAsync();
            db.ConversationProjectionCheckpoints.Add(new ConversationProjectionCheckpointEntity
            {
                ConversationId = "conv-1",
                ProjectedThrough = 3,
                UpdatedAt = DateTimeOffset.UtcNow.ToString("O"),
            });
            db.ConversationHeads.Add(new ConversationHeadEntity { ConversationId = "conv-1", HeadSequence = 3 });
            db.ConversationReadStates.Add(new ConversationReadStateEntity
            {
                ConversationId = "conv-1",
                InitializationState = (int)ReadModelInitializationState.Ready,
                CurrentTurnId = "turn-1",
                CompactionRunning = true,
                CompactionRunId = "compaction-run-1",
                CompactionStartedSequence = 2,
                SubAgentRunsJson =
                    """[{"runId":"child-1","subAgentId":"sub-1","turnId":"turn-1","state":1,"firstSequence":1,"lastSequence":1}]""",
                SubAgentRunsOmitted = 3,
                ReadModelVersion = ConversationReadModelLimits.CurrentReadModelVersion,
                Revision = 4,
                UpdatedAt = DateTimeOffset.UtcNow.ToString("O"),
            });
            db.ChatMessages.Add(Message("msg-1", "agent", "hello", 1_000, "turn-1"));
            await db.SaveChangesAsync();
        }

        var service = NewService(temp);
        var snapshot = await service.OpenAsync("conv-1");

        Assert.IsTrue(snapshot.Runtime.CompactionRunning);
        Assert.AreEqual("compaction-run-1", snapshot.Runtime.CompactionRunId);
        Assert.IsFalse(snapshot.Runtime.CompactionLastAttemptFailed);
        Assert.HasCount(1, snapshot.Runtime.SubAgentRuns);
        Assert.AreEqual("child-1", snapshot.Runtime.SubAgentRuns[0].RunId);
        Assert.AreEqual(3, snapshot.Runtime.SubAgentRunsOmitted);
    }

    [TestMethod]
    public async Task GetProcessPageAsync_ExposesStructuredToolAndSubAgentFields()
    {
        await using var temp = TempQueryDb.Create();
        var longOutput = new string('o', 20_000);
        await using (var db = new PlatformDbContext(temp.Options))
        {
            await db.Database.EnsureCreatedAsync();
            AddEvent(db, 1, ConversationEventTypes.ToolCallRequested,
                """{"toolCallId":"call-1","name":"read","arguments":"{\"path\":\"/a\"}"}""");
            AddEvent(db, 2, ConversationEventTypes.ToolCallCompleted,
                $$"""{"toolCallId":"call-1","name":"read","output":"{{longOutput}}","exitCode":0}""");
            AddEvent(db, 3, ConversationEventTypes.SubAgentRunCreated,
                """{"sub_agent_id":"sub-1","runId":"run-child-1","status":"created"}""");
            db.ConversationHeads.Add(new ConversationHeadEntity { ConversationId = "conv-1", HeadSequence = 3 });
            await db.SaveChangesAsync();
        }

        var service = NewService(temp);
        var page = await service.GetProcessPageAsync(new ConversationProcessQuery("conv-1", Limit: 10));

        Assert.HasCount(3, page.Items);
        var requested = page.Items.Single(item => item.Type == ConversationEventTypes.ToolCallRequested);
        Assert.AreEqual("call-1", requested.ToolCallId);
        Assert.AreEqual("read", requested.Name);
        Assert.AreEqual("requested", requested.Status);
        Assert.AreEqual("""{"path":"/a"}""", requested.Arguments);

        var completed = page.Items.Single(item => item.Type == ConversationEventTypes.ToolCallCompleted);
        Assert.AreEqual("call-1", completed.ToolCallId);
        Assert.AreEqual(0, completed.ExitCode);
        Assert.AreEqual("completed", completed.Status);
        Assert.IsNotNull(completed.Output);
        Assert.IsTrue(Encoding.UTF8.GetByteCount(completed.Output) <= 16 * 1024);

        var subAgent = page.Items.Single(item => item.Type == ConversationEventTypes.SubAgentRunCreated);
        Assert.AreEqual("sub-1", subAgent.SubAgentId);
        Assert.AreEqual("run-child-1", subAgent.RunId);
        Assert.AreEqual("created", subAgent.Status);
    }

    [TestMethod]
    public async Task GetMessageContentAsync_SegmentsLargeUtf8ContentOnByteBoundaries()
    {
        await using var temp = TempQueryDb.Create();
        var content = string.Concat(Enumerable.Repeat("中文😀abc", 1_000));
        await using (var db = new PlatformDbContext(temp.Options))
        {
            await db.Database.EnsureCreatedAsync();
            db.ChatMessages.Add(Message("msg-large", "agent", content, 1_000, "turn-1"));
            await db.SaveChangesAsync();
        }

        var service = NewService(temp);
        var rebuilt = new StringBuilder();
        long offset = 0;
        var pageCount = 0;
        while (true)
        {
            var page = await service.GetMessageContentAsync(
                "conv-1", "msg-large", offsetBytes: offset, maxBytes: 7);
            Assert.AreEqual(offset, page.OffsetBytes);
            Assert.AreEqual(Encoding.UTF8.GetByteCount(content), page.TotalBytes);
            Assert.IsFalse(page.Content.Contains('\uFFFD'), "segment must not split a UTF-8 sequence");
            rebuilt.Append(page.Content);
            pageCount++;
            Assert.IsTrue(pageCount < 10_000, "pagination must terminate");
            if (!page.HasMore)
            {
                Assert.IsNull(page.NextOffsetBytes);
                break;
            }

            Assert.IsNotNull(page.NextOffsetBytes);
            offset = page.NextOffsetBytes!.Value;
            Assert.IsTrue(offset > page.OffsetBytes);
        }

        Assert.AreEqual(content, rebuilt.ToString());
    }

    [TestMethod]
    public async Task GetMessageContentAsync_ShortMessageReturnsWholeContentAndRejectsMissingMessage()
    {
        await using var temp = TempQueryDb.Create();
        await using (var db = new PlatformDbContext(temp.Options))
        {
            await db.Database.EnsureCreatedAsync();
            db.ChatMessages.Add(Message("msg-short", "agent", "hello", 1_000, "turn-1"));
            await db.SaveChangesAsync();
        }

        var service = NewService(temp);
        var page = await service.GetMessageContentAsync("conv-1", "msg-short");
        Assert.AreEqual("hello", page.Content);
        Assert.IsFalse(page.HasMore);
        Assert.IsNull(page.NextOffsetBytes);
        Assert.AreEqual(5L, page.TotalBytes);

        var missing = await Assert.ThrowsExactlyAsync<ConversationQueryException>(() =>
            service.GetMessageContentAsync("conv-1", "missing"));
        Assert.AreEqual("message_not_found", missing.Code);
    }

    // ── seed helpers ─────────────────────────────────────────────────────

    private static ConversationOpenQueryService NewService(TempQueryDb temp)
        => new(temp.DbFactory, NullLogger<ConversationOpenQueryService>.Instance);

    private static async Task SeedConversationAsync(
        PlatformDbContext db,
        int messageCount,
        long checkpoint,
        long head)
    {
        db.ConversationProjectionCheckpoints.Add(new ConversationProjectionCheckpointEntity
        {
            ConversationId = "conv-1",
            ProjectedThrough = checkpoint,
            UpdatedAt = DateTimeOffset.UtcNow.ToString("O"),
        });
        db.ConversationHeads.Add(new ConversationHeadEntity { ConversationId = "conv-1", HeadSequence = head });
        db.ConversationReadStates.Add(new ConversationReadStateEntity
        {
            ConversationId = "conv-1",
            InitializationState = (int)ReadModelInitializationState.Ready,
            CurrentTurnId = "turn-2",
            ReadModelVersion = ConversationReadModelLimits.CurrentReadModelVersion,
            Revision = 7,
            UpdatedAt = DateTimeOffset.UtcNow.ToString("O"),
        });

        for (var index = 1; index <= messageCount; index++)
            db.ChatMessages.Add(Message($"msg-{index:D2}", "agent", $"content-{index}", 1_000 + index, "turn-1"));

        foreach (var turnId in new[] { "turn-1", "turn-2" })
        {
            db.ConversationTurnSummaries.Add(new ConversationTurnSummaryEntity
            {
                ConversationId = "conv-1",
                TurnId = turnId,
                RootRunId = "run-" + turnId,
                TerminalState = (int)TurnTerminalState.Completed,
                HasAssistantBody = true,
                AssistantMessageId = "assistant-" + turnId,
                ProcessCompleteness = (int)SummaryCompleteness.Complete,
                ActivePreview = "preview-" + turnId,
                FirstSequence = turnId == "turn-1" ? 1 : 2,
                LastSequence = turnId == "turn-1" ? 1 : 2,
                SummaryRevision = 1,
                ReadModelVersion = ConversationReadModelLimits.CurrentReadModelVersion,
                UpdatedAt = DateTimeOffset.UtcNow.ToString("O"),
            });
        }
    }

    private static ChatMessageEntity Message(
        string messageId,
        string role,
        string content,
        long createdAt,
        string? turnId)
        => new()
        {
            MessageId = messageId,
            SessionId = "conv-1",
            WorkspaceId = "ws",
            AgentInstanceId = "agent-1",
            AgentTemplateId = "tpl",
            Role = role,
            Content = content,
            TurnId = turnId,
            CreatedAt = createdAt,
        };

    private static void AddEvent(PlatformDbContext db, long sequence, string type, string payloadJson)
    {
        db.ConversationEvents.Add(new ConversationEventEntity
        {
            ConversationId = "conv-1",
            Sequence = sequence,
            EventId = $"evt-{sequence}",
            WorkspaceId = "ws",
            TurnId = "turn-1",
            Type = type,
            Payload = payloadJson,
            OccurredAt = DateTimeOffset.Parse("2026-10-18T00:00:00Z").AddSeconds(sequence).ToString("O"),
            CommittedAt = DateTimeOffset.Parse("2026-10-18T00:00:00Z").AddSeconds(sequence).ToString("O"),
        });
    }

    private sealed class TempQueryDb : IAsyncDisposable
    {
        private TempQueryDb(string rootPath)
        {
            RootPath = rootPath;
            Options = new DbContextOptionsBuilder<PlatformDbContext>()
                .UseSqlite($"Data Source={System.IO.Path.Combine(rootPath, "platform.db")}")
                .Options;
            DbFactory = new PlatformDbContextFactory(Options);
        }

        public string RootPath { get; }
        public DbContextOptions<PlatformDbContext> Options { get; }
        public PlatformDbContextFactory DbFactory { get; }

        public static TempQueryDb Create()
        {
            var root = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "pudding-open-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return new TempQueryDb(root);
        }

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            try
            {
                if (Directory.Exists(RootPath))
                    Directory.Delete(RootPath, recursive: true);
            }
            catch (IOException)
            {
                // best-effort cleanup of test temp data outside the repo
            }

            return ValueTask.CompletedTask;
        }
    }
}
