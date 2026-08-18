using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Models;
using PuddingCode.Observability;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingCode.Services;
using PuddingMemoryEngine.Data;
using PuddingMemoryEngine.Entities;
using PuddingRuntime.Services;
using PuddingRuntime.Services.AgentLoop;

namespace PuddingRuntimeTests.Services;

/// <summary>
/// P1-1 Task F-2：JSONL 冷启动路径 Tier 化 + query 透传的单元测试。
/// 覆盖：MapToTierInputs(JsonlEntry) 轮次/query 命中映射、Tier 填充保新弃旧、
/// generation 过滤（CoveredMessageIds）、query=null 回归、query 命中晋升。
/// </summary>
[TestClass]
public sealed class ContextWindowManagerJsonlTierTests
{
    // ── 1. MapToTierInputs(JsonlEntry)：轮次推导 + 当前轮标记 ──────────────

    [TestMethod]
    public void MapToTierInputs_JsonlEntry_DerivesTurnOrdinals_AndMarksLastUserTurnAsCurrent()
    {
        var entries = new List<JsonlEntry>
        {
            new() { MessageId = "m1", Role = "user", Content = "q1", CreatedAt = 1 },
            new() { MessageId = "m2", Role = "assistant", Content = "a1", CreatedAt = 2 },
            new() { MessageId = "m3", Role = "user", Content = "q2", CreatedAt = 3 },
            new() { MessageId = "m4", Role = "assistant", Content = "a2", CreatedAt = 4 },
            new() { MessageId = "m5", Role = "user", Content = "q3", CreatedAt = 5 },
            new() { MessageId = "m6", Role = "assistant", Content = "a3", CreatedAt = 6 },
        };

        var inputs = ContextWindowManager.MapToTierInputs(entries);

        Assert.AreEqual(6, inputs.Count);
        Assert.AreEqual("m1", inputs[0].SegmentId);
        Assert.AreEqual("m6", inputs[5].SegmentId);

        // 轮次：user 边界 0-based 递增，同轮消息归属当前轮。
        Assert.AreEqual(0, inputs[0].TurnOrdinal);
        Assert.AreEqual(0, inputs[1].TurnOrdinal);
        Assert.AreEqual(1, inputs[2].TurnOrdinal);
        Assert.AreEqual(1, inputs[3].TurnOrdinal);
        Assert.AreEqual(2, inputs[4].TurnOrdinal);
        Assert.AreEqual(2, inputs[5].TurnOrdinal);

        // 最后 user 轮（turn2）整轮标记 IsCurrentTurn → planner 视为 T0。
        Assert.IsFalse(inputs[0].IsCurrentTurn);
        Assert.IsFalse(inputs[2].IsCurrentTurn);
        Assert.IsTrue(inputs[4].IsCurrentTurn);
        Assert.IsTrue(inputs[5].IsCurrentTurn);
    }

    [TestMethod]
    public void MapToTierInputs_JsonlEntry_IsUserBoundary_IsCaseInsensitive()
    {
        var entries = new List<JsonlEntry>
        {
            new() { MessageId = "m1", Role = "USER", Content = "q1", CreatedAt = 1 },
            new() { MessageId = "m2", Role = "Assistant", Content = "a1", CreatedAt = 2 },
        };

        var inputs = ContextWindowManager.MapToTierInputs(entries);

        Assert.AreEqual(0, inputs[0].TurnOrdinal);
        Assert.AreEqual(0, inputs[1].TurnOrdinal);
        Assert.IsTrue(inputs[0].IsCurrentTurn);
        Assert.IsTrue(inputs[1].IsCurrentTurn);
    }

    // ── 2. MapToTierInputs(JsonlEntry)：query 命中映射 ─────────────────────

    [TestMethod]
    public void MapToTierInputs_JsonlEntry_QueryHit_FlagsContainingSegments()
    {
        var entries = new List<JsonlEntry>
        {
            new() { MessageId = "m1", Role = "user", Content = "我们讨论缓存命中率优化方案", CreatedAt = 1 },
            new() { MessageId = "m2", Role = "assistant", Content = "好的，这是常规回答", CreatedAt = 2 },
            new() { MessageId = "m3", Role = "user", Content = "继续", CreatedAt = 3 },
        };

        var inputs = ContextWindowManager.MapToTierInputs(entries, query: "缓存命中率");

        Assert.IsTrue(inputs[0].IsQueryHit, "正文包含 query 命中串的消息必须标记 IsQueryHit");
        Assert.IsFalse(inputs[1].IsQueryHit);
        Assert.IsFalse(inputs[2].IsQueryHit);

        // query=null → 全部非命中（与旧行为一致）。
        var baseline = ContextWindowManager.MapToTierInputs(entries);
        Assert.IsTrue(baseline.All(i => !i.IsQueryHit));
    }

    // ── 3. JSONL 路径 Tier 化：预算紧张时近轮保真、冷轮被裁 ────────────────

    [TestMethod]
    public async Task BuildContextFromJsonlAsync_TierFilling_KeepsRecentTurns_DropsCold()
    {
        // 5 轮 × 2 条，每条约 100~101 tokens；预算 704：
        // T0(turn4)=201 + T1(turn2/turn3)=402 → 603，T2 内只够 1 条（CreatedAt 降序 → turn1-agent）。
        // turn1-user 与 turn0 整轮（最冷）被裁。
        var jsonlRoot = WriteJsonlSession("session-tier-budget", CreateFiveTurnMessages());
        try
        {
            var manager = CreateManager(jsonlReader: new JsonlSessionReader(jsonlRoot));

            var messages = await manager.BuildContextFromJsonlAsync(
                "session-tier-budget", maxTokenBudget: 704, CancellationToken.None);

            Assert.IsTrue(messages.Any(m => m.Content?.StartsWith("turn4-", StringComparison.Ordinal) == true),
                "T0 最近轮必须全保真");
            Assert.IsTrue(messages.Any(m => m.Content?.StartsWith("turn3-", StringComparison.Ordinal) == true),
                "T1 近轮必须全保真");
            Assert.IsTrue(messages.Any(m => m.Content?.StartsWith("turn2-", StringComparison.Ordinal) == true),
                "T1 近轮必须全保真");
            Assert.IsTrue(messages.Any(m => m.Content?.StartsWith("turn1-agent", StringComparison.Ordinal) == true),
                "T2 内 CreatedAt 较新的 turn1-agent 应抢到名额");
            Assert.IsFalse(messages.Any(m => m.Content?.StartsWith("turn1-user", StringComparison.Ordinal) == true),
                "预算耗尽后同 tier 内更旧消息不再填充");
            Assert.IsFalse(messages.Any(m => m.Content?.StartsWith("turn0-", StringComparison.Ordinal) == true),
                "更冷轮（turn0）整体不再填充（保新弃旧）");

            // 输出按 CreatedAt 升序（旧→新）。
            Assert.IsTrue(messages[0].Content?.StartsWith("turn1-agent", StringComparison.Ordinal) == true,
                "输出首条应为 CreatedAt 最小的选中消息（turn1-agent）");
            Assert.IsTrue(messages[^1].Content?.StartsWith("turn4-agent", StringComparison.Ordinal) == true,
                "输出末条应为 CreatedAt 最大的选中消息（turn4-agent）");
        }
        finally
        {
            Directory.Delete(jsonlRoot, recursive: true);
        }
    }

    // ── 4. generation 过滤仍生效（CoveredMessageIds 排除） ────────────────

    [TestMethod]
    public async Task BuildContextFromJsonlAsync_GenerationFilter_ExcludesCoveredMessages()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = CreateOptions(connection);
        await using var db = new MemoryDbContext(options);
        await db.Database.EnsureCreatedAsync();

        // 最新压缩覆盖清单：jsonl-covered-1 已被压缩覆盖。
        db.CompactionCoverageManifests.Add(new CompactionCoverageManifestEntity
        {
            SessionId = "session-gen-filter",
            SourceGeneration = 0,
            TargetGeneration = 1,
            SourceMessageIds = """["jsonl-covered-1"]""",
            SourceHashes = "[]",
            CoveredCount = 1,
            OmittedCount = 0,
            CreatedAtUtc = 1,
        });
        await db.SaveChangesAsync();

        var jsonlRoot = CreateTempJsonlRoot();
        try
        {
            var writer = new JsonlSessionWriter(jsonlRoot);
            writer.Enqueue("session-gen-filter", new JsonlEntry
            {
                Type = "user", MessageId = "jsonl-covered-1", SessionId = "session-gen-filter",
                Role = "user", ContentType = "text", Content = "covered content 缓存命中率", CreatedAt = 1,
            });
            writer.Enqueue("session-gen-filter", new JsonlEntry
            {
                Type = "user", MessageId = "jsonl-live-1", SessionId = "session-gen-filter",
                Role = "user", ContentType = "text", Content = "live user message", CreatedAt = 2,
            });
            writer.Enqueue("session-gen-filter", new JsonlEntry
            {
                Type = "assistant", MessageId = "jsonl-live-2", SessionId = "session-gen-filter",
                Role = "assistant", ContentType = "text", Content = "live assistant message", CreatedAt = 3,
            });

            var manager = CreateManager(
                new TestMemoryDbContextFactory(options),
                new JsonlSessionReader(jsonlRoot));

            var messages = await manager.BuildContextFromJsonlAsync(
                "session-gen-filter", maxTokenBudget: 8000, CancellationToken.None);

            Assert.IsFalse(messages.Any(m => m.Content?.Contains("covered content", StringComparison.Ordinal) == true),
                "CoveredMessageIds 命中的 entry 必须被 generation 过滤排除（防止经 JSONL 旁路复活）");
            Assert.IsTrue(messages.Any(m => m.Content == "live user message"));
            Assert.IsTrue(messages.Any(m => m.Content == "live assistant message"));
        }
        finally
        {
            Directory.Delete(jsonlRoot, recursive: true);
        }
    }

    // ── 5. query=null 回归：最近消息优先保留（与改造前语义一致） ───────────

    [TestMethod]
    public async Task BuildContextFromJsonlAsync_QueryNull_KeepsRecentMessages_LegacySemantics()
    {
        // 4 轮 8 条小消息；预算充足 → 全部保留，输出旧→新；最近轮（T0）必在。
        var jsonlRoot = WriteJsonlSession("session-jsonl-null-query", [
            ("user", "turn0-user"),
            ("agent", "turn0-agent"),
            ("user", "turn1-user"),
            ("agent", "turn1-agent"),
            ("user", "turn2-user"),
            ("agent", "turn2-agent"),
            ("user", "turn3-user"),
            ("agent", "turn3-agent"),
        ]);
        try
        {
            var manager = CreateManager(jsonlReader: new JsonlSessionReader(jsonlRoot));

            var messages = await manager.BuildContextFromJsonlAsync(
                "session-jsonl-null-query", maxTokenBudget: 8000, CancellationToken.None, query: null);

            Assert.AreEqual(8, messages.Count, "预算充足时全部消息应保留");
            Assert.AreEqual("turn0-user", messages[0].Content, "输出必须按 CreatedAt 升序（旧→新）");
            Assert.AreEqual("turn3-agent", messages[^1].Content);
            Assert.IsTrue(messages.Any(m => m.Content == "turn3-user"), "最近轮必须保留");
        }
        finally
        {
            Directory.Delete(jsonlRoot, recursive: true);
        }
    }

    // ── 6. BuildContextFromJsonlAsync query 透传：命中旧消息晋升保留 ────────

    [TestMethod]
    public async Task BuildContextFromJsonlAsync_QueryHit_PromotesColdTurn_WhenBudgetIsTight()
    {
        // 与测试 3 相同数据 + query 命中 turn0-user → 晋升 T1，T0+T1+晋升=703 ≤ 704 → turn0-user 保留；
        // turn1 未命中且预算紧张 → 全裁；turn0-agent 未命中仍被裁。
        var jsonlRoot = WriteJsonlSession("session-jsonl-query-hit", CreateFiveTurnMessages());
        try
        {
            var manager = CreateManager(jsonlReader: new JsonlSessionReader(jsonlRoot));

            // 1) 无 query 基线：turn0 全裁。
            var baseline = await manager.BuildContextFromJsonlAsync(
                "session-jsonl-query-hit", maxTokenBudget: 704, CancellationToken.None);
            Assert.IsFalse(baseline.Any(m => m.Content?.StartsWith("turn0-", StringComparison.Ordinal) == true),
                "无 query 时 turn0 必须被裁");

            // 2) query 透传：命中 turn0-user → 晋升保留。
            var promoted = await manager.BuildContextFromJsonlAsync(
                "session-jsonl-query-hit", maxTokenBudget: 704, CancellationToken.None, query: "缓存命中率 提升");
            Assert.IsTrue(promoted.Any(m => m.Content?.StartsWith("turn0-user", StringComparison.Ordinal) == true),
                "query 命中后 turn0-user 必须晋升保留");
            Assert.IsFalse(promoted.Any(m => m.Content?.StartsWith("turn1-", StringComparison.Ordinal) == true),
                "query 未命中的 turn1 在预算紧张时必须被裁");
            Assert.IsFalse(promoted.Any(m => m.Content?.StartsWith("turn0-agent", StringComparison.Ordinal) == true),
                "turn0-agent 未命中不得晋升，预算紧张时仍被裁");
        }
        finally
        {
            Directory.Delete(jsonlRoot, recursive: true);
        }
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    /// <summary>5 轮 × 2 条：turn0-user 含 query 命中串「缓存命中率」，每条约 100~101 tokens。</summary>
    private static IReadOnlyList<(string Role, string Content)> CreateFiveTurnMessages()
        => [
            ("user", "turn0-user 缓存命中率 " + new string('x', 285)),
            ("agent", "turn0-agent " + new string('x', 291)),
            ("user", "turn1-user " + new string('x', 291)),
            ("agent", "turn1-agent " + new string('x', 291)),
            ("user", "turn2-user " + new string('x', 291)),
            ("agent", "turn2-agent " + new string('x', 291)),
            ("user", "turn3-user " + new string('x', 291)),
            ("agent", "turn3-agent " + new string('x', 291)),
            ("user", "turn4-user " + new string('x', 291)),
            ("agent", "turn4-agent " + new string('x', 291)),
        ];

    private static string WriteJsonlSession(
        string sessionId,
        IReadOnlyList<(string Role, string Content)> messages)
    {
        var root = CreateTempJsonlRoot();
        var writer = new JsonlSessionWriter(root);
        for (var i = 0; i < messages.Count; i++)
        {
            writer.Enqueue(sessionId, new JsonlEntry
            {
                Type = messages[i].Role,
                MessageId = $"jsonl-{i + 1}",
                SessionId = sessionId,
                Role = messages[i].Role,
                ContentType = "text",
                Content = messages[i].Content,
                BranchType = "MAIN",
                CreatedAt = i + 1,
            });
        }

        return root;
    }

    private static string CreateTempJsonlRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "pudding-jsonl-tier-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static ContextWindowManager CreateManager(
        IDbContextFactory<MemoryDbContext>? memoryDbFactory = null,
        JsonlSessionReader? jsonlReader = null)
        => new(
            new AgentSessionManager(NullLogger<AgentSessionManager>.Instance),
            new InMemoryRuntimeSessionStore(),
            new ExecutionControlRegistry(),
            new ExecutionJournal(),
            NullLogger<ContextWindowManager>.Instance,
            memoryDbFactory: memoryDbFactory,
            jsonlReader: jsonlReader);

    private static DbContextOptions<MemoryDbContext> CreateOptions(SqliteConnection connection) =>
        new DbContextOptionsBuilder<MemoryDbContext>()
            .UseSqlite(connection)
            .Options;

    private sealed class TestMemoryDbContextFactory(DbContextOptions<MemoryDbContext> options)
        : IDbContextFactory<MemoryDbContext>
    {
        public MemoryDbContext CreateDbContext() => new(options);

        public Task<MemoryDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
