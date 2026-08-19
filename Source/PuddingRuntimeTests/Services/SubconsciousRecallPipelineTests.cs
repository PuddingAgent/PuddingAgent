using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Abstractions;
using PuddingCode.Platform;
using PuddingMemoryEngine.Data;
using PuddingMemoryEngine.Entities;
using PuddingRuntime.Services;

namespace PuddingRuntimeTests.Services;

[TestClass]
public sealed class SubconsciousRecallPipelineTests
{
    [TestMethod]
    public async Task RunAsync_PassesWorkspaceIdToMemoryRecall()
    {
        var recall = new RecordingMemoryRecallService();
        var llm = new StaticMemoryLlmClient("""{"need_recall":true,"relevant_ids":[1],"reason":"test"}""");
        var pipeline = new SubconsciousRecallPipeline(
            recall,
            llm,
            NullLogger<SubconsciousRecallPipeline>.Instance);

        var result = await pipeline.RunAsync(
            "继续 回顾 上次 讨论",
            workspaceId: "default",
            agentInstanceId: "agent-1",
            isFirstMessage: false,
            CancellationToken.None);

        Assert.AreEqual("default", recall.LastWorkspaceId);
        Assert.AreEqual("agent-1", recall.LastAgentInstanceId);
        Assert.IsFalse(string.IsNullOrWhiteSpace(result));
    }

    [TestMethod]
    public async Task RunAsync_DropsCoveredHashes_BeforeInjectingAugmentContent()
    {
        // P1-2 T5：构造 covered hash 的 SearchHit → 不进注入内容（covered 过滤）。
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = CreateOptions(connection);
        await using (var db = new MemoryDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            db.CompactionCoverageManifests.Add(new CompactionCoverageManifestEntity
            {
                CompactionId = "compaction-t5",
                SessionId = "agent-1",
                SourceGeneration = 0,
                TargetGeneration = 1,
                SourceMessageIds = """["msg-covered"]""",
                SourceHashes = """["covered-hash"]""",
            });
            await db.SaveChangesAsync();
        }

        var recall = new RecordingMemoryRecallService(
            new RecalledMemory
            {
                Snippet = "被压缩覆盖的旧消息原文片段。",
                RelevanceScore = 0.95,
                Source = "chunk-vector",
                SourceId = "chunk:s-1:c-1",
                SourceMessageId = "msg-covered",
                CanonicalContentHash = "covered-hash",
            },
            new RecalledMemory
            {
                Snippet = "未覆盖的活跃消息片段。",
                RelevanceScore = 0.9,
                Source = "chunk-vector",
                SourceId = "chunk:s-1:c-2",
                SourceMessageId = "msg-active",
                CanonicalContentHash = "active-hash",
            });
        var llm = new StaticMemoryLlmClient("""{"need_recall":true,"relevant_ids":[1,2],"reason":"test"}""");
        var pipeline = new SubconsciousRecallPipeline(
            recall,
            llm,
            NullLogger<SubconsciousRecallPipeline>.Instance,
            memoryDbFactory: new TestMemoryDbContextFactory(options));

        var result = await pipeline.RunAsync(
            "继续 回顾 上次 讨论",
            workspaceId: "default",
            agentInstanceId: "agent-1",
            isFirstMessage: false,
            CancellationToken.None);

        Assert.IsFalse(string.IsNullOrWhiteSpace(result));
        StringAssert.Contains(result, "未覆盖的活跃消息片段。");
        Assert.IsFalse(result!.Contains("被压缩覆盖的旧消息原文片段。"), "covered hash 片段不应进入注入内容。");
    }

    [TestMethod]
    public async Task RunAsync_DeduplicatesSameSourceHash_WithinOneRound()
    {
        // P1-2 T5：同一 source hash 的多个 chunk 同轮只注入 1 条（Score 高者优先）。
        var recall = new RecordingMemoryRecallService(
            new RecalledMemory
            {
                Snippet = "同一消息的第一块。",
                RelevanceScore = 0.95,
                Source = "chunk-vector",
                SourceId = "chunk:s-1:c-1",
                SourceMessageId = "msg-1",
                CanonicalContentHash = "same-hash",
            },
            new RecalledMemory
            {
                Snippet = "同一消息的第二块。",
                RelevanceScore = 0.6,
                Source = "chunk-vector",
                SourceId = "chunk:s-1:c-2",
                SourceMessageId = "msg-1",
                CanonicalContentHash = "same-hash",
            });
        var llm = new StaticMemoryLlmClient("""{"need_recall":true,"relevant_ids":[1,2],"reason":"test"}""");
        var pipeline = new SubconsciousRecallPipeline(
            recall,
            llm,
            NullLogger<SubconsciousRecallPipeline>.Instance);

        var result = await pipeline.RunAsync(
            "继续 回顾 上次 讨论",
            workspaceId: "default",
            agentInstanceId: "agent-1",
            isFirstMessage: false,
            CancellationToken.None);

        Assert.IsFalse(string.IsNullOrWhiteSpace(result));
        StringAssert.Contains(result, "同一消息的第一块。");
        Assert.IsFalse(result!.Contains("同一消息的第二块。"), "同轮 hash 去重：同 source 只注入 1 条。");
    }

    [TestMethod]
    public async Task RunAsync_NoOpCompatible_WhenNoMemoryDbFactory()
    {
        // P1-2 T5：无 factory（_coverageFilter=null）时 covered 过滤 no-op——covered hash 片段仍注入、不抛异常。
        var recall = new RecordingMemoryRecallService(
            new RecalledMemory
            {
                Snippet = "covered 片段（无 factory 时不应被过滤）。",
                RelevanceScore = 0.9,
                Source = "chunk-vector",
                SourceId = "chunk:s-1:c-1",
                SourceMessageId = "msg-covered",
                CanonicalContentHash = "covered-hash",
            });
        var llm = new StaticMemoryLlmClient("""{"need_recall":true,"relevant_ids":[1],"reason":"test"}""");
        var pipeline = new SubconsciousRecallPipeline(
            recall,
            llm,
            NullLogger<SubconsciousRecallPipeline>.Instance);

        var result = await pipeline.RunAsync(
            "继续 回顾 上次 讨论",
            workspaceId: "default",
            agentInstanceId: "agent-1",
            isFirstMessage: false,
            CancellationToken.None);

        Assert.IsFalse(string.IsNullOrWhiteSpace(result));
        StringAssert.Contains(result, "covered 片段（无 factory 时不应被过滤）。");
    }

    private static DbContextOptions<MemoryDbContext> CreateOptions(SqliteConnection connection) =>
        new DbContextOptionsBuilder<MemoryDbContext>()
            .UseSqlite(connection)
            .Options;

    private sealed class RecordingMemoryRecallService : IMemoryRecallService
    {
        private readonly IReadOnlyList<RecalledMemory> _items;

        public RecordingMemoryRecallService(params RecalledMemory[] items)
        {
            // 无显式 items 时保留既有测试默认召回（Source=fact，无 hash，不触发 T5 过滤）。
            _items = items.Length > 0
                ? items
                : [new RecalledMemory
                {
                    Snippet = "用户上次讨论了 compact 后的 Session 切换和消息吞掉问题。",
                    RelevanceScore = 0.95,
                    Source = "fact",
                    SourceId = "fact-1",
                }];
        }

        public string? LastWorkspaceId { get; private set; }
        public string? LastAgentInstanceId { get; private set; }

        public Task<MemoryRecallResult> RecallAsync(
            string query,
            string workspaceId,
            string? agentInstanceId = null,
            IReadOnlyList<string>? recentContext = null,
            int topK = 10,
            CancellationToken ct = default)
        {
            LastWorkspaceId = workspaceId;
            LastAgentInstanceId = agentInstanceId;
            return Task.FromResult(new MemoryRecallResult
            {
                Items = _items,
            });
        }

        public Task<MemoryRecallStatus> GetStatusAsync(string workspaceId, CancellationToken ct = default)
            => Task.FromResult(new MemoryRecallStatus());
    }

    private sealed class StaticMemoryLlmClient(string response) : IMemoryLlmClient
    {
        public Task<MemoryClassification> ClassifyAsync(string messageText, CancellationToken ct = default)
            => Task.FromResult(new MemoryClassification(false, 0, 1, null, null));

        public Task<string?> SummarizeAsync(IReadOnlyList<string> memoryContents, CancellationToken ct = default)
            => Task.FromResult<string?>(null);

        public Task<MemoryQueryIntent?> ParseIntentAsync(string userMessage, CancellationToken ct = default)
            => Task.FromResult<MemoryQueryIntent?>(null);

        public Task<string> ChatAsync(
            string systemPrompt,
            string userMessage,
            IReadOnlyList<object>? tools = null,
            CancellationToken ct = default)
            => Task.FromResult(response);
    }

    private sealed class TestMemoryDbContextFactory(DbContextOptions<MemoryDbContext> options) : IDbContextFactory<MemoryDbContext>
    {
        public MemoryDbContext CreateDbContext() => new(options);

        public Task<MemoryDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
