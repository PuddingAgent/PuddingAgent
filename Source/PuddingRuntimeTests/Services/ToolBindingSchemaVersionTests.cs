using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingMemoryEngine.Data;
using PuddingRuntime.Services;

namespace PuddingRuntimeTests.Services;

/// <summary>
/// C01-B-3 / AC4-A：工具**定义身份**（toolId + definitionHash，不存 schema 正文）随 composition 记录落库，
/// 并在恢复路径上给出「定义级精确恢复 vs 仅 ID 级恢复」的可观测判定（R13），
/// 且缺定义/哈希不符时**不谎称成功**、**不阻塞会话**（R15）。
/// </summary>
[TestClass]
public sealed class ToolBindingSchemaVersionTests
{
    private const string SchemaOnlyMarker = "SCHEMA-BODY-MUST-NOT-BE-PERSISTED";

    // ── 用例 1：落库 + 回读往返一致（有序、无 schema 正文）──────────

    [TestMethod]
    public async Task Append_ToolBindings_RoundTrips_Ordered_WithoutSchemaBody()
    {
        var (connection, factory) = CreateSqliteMemoryDatabase();
        using (connection)
        {
            var store = new SqliteCompositionStore(factory);
            var bindings = CompositionSnapshot.ComputeToolBindings(
            [
                Tool("file_read", SchemaOnlyMarker + "-file_read"),
                Tool("search_tools"),
            ]);
            var record = Record("s1", 1, ["file_read", "search_tools"]) with { ToolBindings = bindings };

            var result = await store.AppendAsync(record, expectedRevision: 0);
            Assert.IsTrue(result.IsCommitted, "CAS 提交必须成功。");

            var latest = await store.GetLatestAsync("s1");
            Assert.IsNotNull(latest);
            Assert.IsNotNull(latest.ToolBindings, "落库的 ToolBindings 必须可回读（非 NULL）。");
            CollectionAssert.AreEqual(
                new[] { "file_read", "search_tools" },
                latest.ToolBindings!.Select(b => b.ToolId).ToArray(),
                "ToolBindings 必须与 ToolIds 同序（有序）。");
            Assert.AreEqual(bindings[0].DefinitionHash, latest.ToolBindings[0].DefinitionHash);
            Assert.AreEqual(bindings[1].DefinitionHash, latest.ToolBindings[1].DefinitionHash);

            // 明文落库内容：只有定义指纹，绝无 schema 正文。
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT ToolBindings FROM CompositionSnapshots WHERE SessionId = 's1';";
            var raw = (string?)cmd.ExecuteScalar();
            Assert.IsNotNull(raw);
            StringAssert.Contains(raw, "definitionHash");
            Assert.IsFalse(
                raw.Contains(SchemaOnlyMarker, StringComparison.Ordinal),
                "落库内容不得包含工具 schema 正文（01:257 原则）。");
            Assert.IsFalse(raw.Contains("description", StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(raw.Contains("parameters", StringComparison.OrdinalIgnoreCase));
        }
    }

    [TestMethod]
    public void ComputeToolDefinitionHash_ReusesExistingCanonicalRule_SingleToolSubset()
    {
        var tool = Tool("file_read");

        // R9：不引入第三套 hash 口径——单工具口径就是既有整表 canonical 规则的单工具子集形态。
        Assert.AreEqual(
            CompositionSnapshot.ComputeToolSpecHash([tool]),
            CompositionSnapshot.ComputeToolDefinitionHash(tool));

        // 定义不同（描述变化）→ 指纹必须不同（否则无法证明「定义级」恢复）。
        Assert.AreNotEqual(
            CompositionSnapshot.ComputeToolDefinitionHash(Tool("file_read", "changed")),
            CompositionSnapshot.ComputeToolDefinitionHash(tool));

        // 逐项绑定与整表单工具口径一致。
        var bindings = CompositionSnapshot.ComputeToolBindings([tool]);
        Assert.AreEqual(1, bindings.Count);
        Assert.AreEqual(tool.Name, bindings[0].ToolId);
        Assert.AreEqual(CompositionSnapshot.ComputeToolDefinitionHash(tool), bindings[0].DefinitionHash);
    }

    // ── 用例 2：定义哈希不符 → 不精确 + 命中 UnresolvedToolIds ──────

    [TestMethod]
    public async Task Recover_DefinitionHashChanged_ReportsNotExact_WithUnresolvedId()
    {
        var manager = new AgentSessionManager();
        // 记录时保存的是「旧定义」的身份
        var stored = Record("s1", 1, ["file_read"]) with
        {
            ToolBindings = CompositionSnapshot.ComputeToolBindings([Tool("file_read", "old definition")]),
        };
        var store = new FakeCompositionStore(stored);
        // 当前 catalog 中同名工具的定义已变更 → canonical 哈希不符
        var catalog = new FakeToolDefinitionCatalog(Tool("file_read", "new definition"));
        var service = new CompositionRecoveryService(manager, store, toolDefinitionCatalog: catalog);

        var result = await service.RecoverAsync("s1");

        Assert.IsFalse(result.SchemaExactRestore, "定义哈希不符时不得谎称精确恢复。");
        CollectionAssert.AreEqual(new[] { "file_read" }, result.UnresolvedToolIds.ToArray());
        // 不阻塞：照常水合、照常 Recovered。
        Assert.AreEqual(CompositionRecoveryStatus.Recovered, result.Status);
        Assert.IsTrue(result.ToolsHydrated);
        CollectionAssert.AreEquivalent(new[] { "file_read" }, manager.GetLoadedToolIds("s1").ToArray());
    }

    // ── 用例 3：工具已不存在 → 不精确 + 命中 + 不抛/不阻塞 ───────────

    [TestMethod]
    public async Task Recover_MissingToolDefinition_FailsClosed_WithoutBlocking()
    {
        var manager = new AgentSessionManager();
        var stored = Record("s1", 1, ["file_read", "retired_tool"]) with
        {
            ToolBindings = CompositionSnapshot.ComputeToolBindings(
            [
                Tool("file_read"),
                Tool("retired_tool"),
            ]),
        };
        var store = new FakeCompositionStore(stored);
        // catalog 中 retired_tool 已不存在；file_read 定义未变。
        var catalog = new FakeToolDefinitionCatalog(Tool("file_read"));
        var service = new CompositionRecoveryService(manager, store, toolDefinitionCatalog: catalog);

        var result = await service.RecoverAsync("s1"); // 不得抛异常

        Assert.IsFalse(result.SchemaExactRestore);
        CollectionAssert.AreEqual(new[] { "retired_tool" }, result.UnresolvedToolIds.ToArray());
        Assert.AreEqual(CompositionRecoveryStatus.Recovered, result.Status, "恢复不得被判定为失败（R15 不阻塞）。");
        // 可见集不收缩：仍按 ID 水合全量（不丢已提交前缀）。
        CollectionAssert.AreEquivalent(
            new[] { "file_read", "retired_tool" },
            manager.GetLoadedToolIds("s1").ToArray());
    }

    [TestMethod]
    public async Task Recover_ExactDefinitionMatch_ReportsSchemaExactRestore()
    {
        var manager = new AgentSessionManager();
        var definitions = new[] { Tool("file_read"), Tool("search_tools") };
        var stored = Record("s1", 1, ["file_read", "search_tools"]) with
        {
            ToolBindings = CompositionSnapshot.ComputeToolBindings(definitions),
        };
        var store = new FakeCompositionStore(stored);
        var service = new CompositionRecoveryService(
            manager,
            store,
            toolDefinitionCatalog: new FakeToolDefinitionCatalog(definitions));

        var result = await service.RecoverAsync("s1");

        Assert.IsTrue(result.SchemaExactRestore, "定义全部匹配（同 toolId + 同 definitionHash）→ 方可判定精确恢复。");
        Assert.IsEmpty(result.UnresolvedToolIds);
        Assert.AreEqual(2, result.HydratedToolCount);
    }

    // ── 用例 4：历史行 ToolBindings 为 NULL → 不谎称，但仍可恢复 ────

    [TestMethod]
    public async Task Recover_LegacyNullBindings_DoesNotClaimExact_ButStillRecovers()
    {
        var manager = new AgentSessionManager();
        // 历史行（C01-B-3 之前写入）：只有 ToolIds，没有定义身份证据。
        var stored = Record("s1", 1, ["file_read", "search_tools"]);
        var store = new FakeCompositionStore(stored);
        var service = new CompositionRecoveryService(
            manager,
            store,
            toolDefinitionCatalog: new FakeToolDefinitionCatalog(Tool("file_read"), Tool("search_tools")));

        var result = await service.RecoverAsync("s1");

        Assert.IsFalse(result.SchemaExactRestore, "历史行无定义身份证据 → 不得谎称精确恢复。");
        Assert.IsEmpty(
            result.UnresolvedToolIds,
            "历史行不得虚构「被证明缺失/不符」的工具项（不可恢复的是证据本身）。");
        // 仍可恢复执行（R7/R15：不抛、不拒绝、不收缩）。
        Assert.AreEqual(CompositionRecoveryStatus.Recovered, result.Status);
        Assert.AreEqual(2, result.HydratedToolCount);
        CollectionAssert.AreEquivalent(
            new[] { "file_read", "search_tools" },
            manager.GetLoadedToolIds("s1").ToArray());
    }

    [TestMethod]
    public async Task Recover_WithoutCatalog_DoesNotClaimExact()
    {
        var manager = new AgentSessionManager();
        var stored = Record("s1", 1, ["file_read"]) with
        {
            ToolBindings = CompositionSnapshot.ComputeToolBindings([Tool("file_read")]),
        };
        var store = new FakeCompositionStore(stored);
        var service = new CompositionRecoveryService(manager, store); // 未接线 catalog

        var result = await service.RecoverAsync("s1");

        Assert.IsFalse(result.SchemaExactRestore, "无 catalog 时无法证明定义等价 → 不得谎称精确。");
        Assert.AreEqual(CompositionRecoveryStatus.Recovered, result.Status);
    }

    [TestMethod]
    public async Task SqliteStore_LegacyNullColumn_ReadsAsNullBindings_NotEmptyList()
    {
        var (connection, factory) = CreateSqliteMemoryDatabase();
        using (connection)
        {
            var store = new SqliteCompositionStore(factory);
            // 直接落一条 ToolBindings 为 NULL 的历史行（模拟 C01-B-3 之前的库）。
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText =
                    "INSERT INTO CompositionSnapshots " +
                    "(SessionId, CompositionVersion, SystemPromptHash, ToolSpecHash, PrefixHash, SerializationVersion, ToolIds, PermissionEpoch, CreatedAtUtc) " +
                    "VALUES ('legacy', 1, 'sys', 'tool', 'prefix', 'prefix-v1', '[\"file_read\"]', 0, 1);";
                await cmd.ExecuteNonQueryAsync();
            }

            var latest = await store.GetLatestAsync("legacy");

            Assert.IsNotNull(latest);
            Assert.IsNull(latest.ToolBindings, "NULL 列必须回读为 null（= 无定义身份证据），不得伪装成空集合。");
        }
    }

    // ── 用例 5：既有对象身份不回退（Revision / ContentId / ExposureRevision）──

    [TestMethod]
    public async Task CommitAsync_PersistsBindings_WithoutRegressingRevisionContentIdExposure()
    {
        var store = new FakeCompositionStore();
        var definitions = new[] { Tool("search_tools"), Tool("file_read") };
        var registry = new PersistentCompositionVersionRegistry(
            store,
            logger: null,
            toolDefinitionCatalog: new FakeToolDefinitionCatalog(definitions));
        string[] toolIds = ["search_tools", "file_read"];

        var observation = registry.Observe(
            "s1",
            systemPromptHash: "sys-1",
            toolSpecHash: CompositionSnapshot.ComputeToolSpecHash(definitions),
            toolIds: toolIds);
        var commit = await registry.CommitAsync(
            "s1",
            observation,
            systemPromptHash: "sys-1",
            toolSpecHash: CompositionSnapshot.ComputeToolSpecHash(definitions),
            toolIds: toolIds);

        Assert.IsTrue(commit.IsCommitted);
        var record = store.Records.Single(r => r.CompositionVersion == observation.Revision);
        // 既有对象身份不回退。
        Assert.AreEqual(observation.Revision, record.CompositionVersion);
        Assert.AreEqual(observation.ContentId, record.ContentId);
        Assert.AreEqual(observation.ExposureRevision, record.ExposureRevision);
        Assert.AreEqual(CompositionSnapshot.ComputeContentId("sys-1", CompositionSnapshot.ComputeToolSpecHash(definitions)), record.ContentId);
        CollectionAssert.AreEqual(toolIds, record.ToolIds.ToArray());
        // 本片新增：定义身份随记录落库，顺序与 ToolIds 一致。
        Assert.IsNotNull(record.ToolBindings);
        CollectionAssert.AreEqual(toolIds, record.ToolBindings!.Select(b => b.ToolId).ToArray());
        Assert.AreEqual(
            CompositionSnapshot.ComputeToolDefinitionHash(definitions[1]),
            record.ToolBindings[1].DefinitionHash);
    }

    // ── 辅助 ─────────────────────────────────────────────

    private static LlmToolDefinition Tool(string name, string description = "definition")
        => new()
        {
            Name = name,
            Description = description,
            Parameters = new ToolParameterSchema(
                [new ToolParameter($"{name}_arg", "string", "arg description")],
                [$"{name}_arg"]),
        };

    private static SessionCompositionRecord Record(string sessionId, long revision, string[] toolIds) => new()
    {
        SessionId = sessionId,
        CompositionVersion = revision,
        ContentId = $"cid-{revision}",
        SystemPromptHash = $"sys-{revision}",
        ToolSpecHash = $"tool-{revision}",
        PrefixHash = $"prefix-{revision}",
        ToolIds = toolIds,
        ChangeReason = "initial",
    };

    private static (SqliteConnection Connection, IDbContextFactory<MemoryDbContext> Factory) CreateSqliteMemoryDatabase()
    {
        // in-memory SQLite：连接必须保持打开，否则每次新建连接都会得到空库。
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<MemoryDbContext>()
            .UseSqlite(connection)
            .Options;
        var factory = new PooledDbContextFactory<MemoryDbContext>(options);
        using (var db = factory.CreateDbContext())
            db.Database.EnsureCreated();
        return (connection, factory);
    }

    private sealed class FakeToolDefinitionCatalog : IToolDefinitionCatalog
    {
        private readonly List<LlmToolDefinition> _definitions;

        public FakeToolDefinitionCatalog(params LlmToolDefinition[] definitions)
            => _definitions = definitions.ToList();

        public IReadOnlyList<LlmToolDefinition> GetAvailableToolDefinitions() => _definitions;
    }

    private sealed class FakeCompositionStore : ICompositionStore
    {
        private readonly List<SessionCompositionRecord> _records = new();

        public FakeCompositionStore(params SessionCompositionRecord[] records) => _records.AddRange(records);

        public IReadOnlyList<SessionCompositionRecord> Records
        {
            get
            {
                lock (_records)
                    return _records.ToArray();
            }
        }

        public Task<SessionCompositionRecord?> GetLatestAsync(string sessionId, CancellationToken ct = default)
        {
            lock (_records)
            {
                return Task.FromResult(_records
                    .Where(r => r.SessionId == sessionId)
                    .OrderByDescending(r => r.CompositionVersion)
                    .FirstOrDefault());
            }
        }

        public Task<CompositionAppendResult> AppendAsync(
            SessionCompositionRecord record,
            long expectedRevision,
            CancellationToken ct = default)
        {
            lock (_records)
            {
                if (_records.Any(r => r.SessionId == record.SessionId && r.CompositionVersion == record.CompositionVersion))
                    return Task.FromResult(CompositionAppendResult.Conflict(expectedRevision, record.CompositionVersion));
                _records.Add(record);
                return Task.FromResult(CompositionAppendResult.Committed(record.CompositionVersion));
            }
        }

        public Task<IReadOnlyList<SessionCompositionRecord>> LoadAsync(string sessionId, CancellationToken ct = default)
        {
            lock (_records)
            {
                return Task.FromResult<IReadOnlyList<SessionCompositionRecord>>(_records
                    .Where(r => r.SessionId == sessionId)
                    .OrderBy(r => r.CompositionVersion)
                    .ToArray());
            }
        }
    }
}
