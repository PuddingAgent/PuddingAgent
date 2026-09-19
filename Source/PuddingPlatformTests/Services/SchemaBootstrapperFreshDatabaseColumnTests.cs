using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingPlatform.Data;
using PuddingPlatform.Services;
using PuddingPlatform.Services.Orchestration;
using PuddingPlatform.Services.Tasks;

namespace PuddingPlatformTests.Services;

/// <summary>
/// 「全新空库」路径验收（卡 4d152325）：所有 schema bootstrap 跑完后，
/// 目标表的实际列集合（PRAGMA table_info）必须与 CREATE TABLE / EF 模型声明的列集合完全一致。
/// <para>
/// 背景：2026-09-19 按卡压缩删除 16 处一次性 EnsureColumnAsync 列迁移（产品未发布，无旧库）。
/// 本测试锁定「删迁移后新库不缺列、不多列」：缺列说明 DDL/EF 模型未跟上实体；
/// 多列说明 EF 模型与 raw DDL 漂移。数据来源：conversation_events 由 ConversationEventStore
/// 的 raw DDL 建；sub_agent_runs 由 EF EnsureCreated 依实体声明建；其余由各 bootstrapper 的
/// raw DDL 建。
/// </para>
/// </summary>
[TestClass]
public sealed class SchemaBootstrapperFreshDatabaseColumnTests
{
    [TestMethod]
    public async Task FreshDatabase_AfterAllBootstrappers_TargetTablesMatchDeclaredColumns()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var db = new PlatformDbContext(options);

        // ① EF 全新库：sub_agent_runs 等实体表（列来自实体声明，含 3 个 parent 执行身份列）。
        await db.Database.EnsureCreatedAsync();
        // ② raw DDL bootstrappers（幂等 CREATE TABLE IF NOT EXISTS + 索引）。
        await WorkspaceTaskSchemaBootstrapper.EnsureCreatedAsync(db);
        await AgentOrchestrationSchemaBootstrapper.EnsureCreatedAsync(db);
        // ③ EF 模型未声明索引补建。
        await SubAgentRunSchemaBootstrapper.EnsureCreatedAsync(db);
        // ④ conversation_events（raw DDL；含被压缩迁移的 agent_id/source_kind/trace_id/producer_component）。
        var eventStore = BuildConversationEventStore(connection);
        await eventStore.EnsureTablesAsync(CancellationToken.None);

        // ── workspace_tasks：36 列（原 8 个迁移列已全部在 CREATE TABLE 中）──────────
        await AssertColumnsAsync(db, "workspace_tasks",
            "task_id", "workspace_id", "title", "description", "acceptance_criteria",
            "status", "priority", "execution_window", "preferred_agent_id",
            "task_type", "required_capabilities_json", "required_provider_id", "required_model_id",
            "allow_agent_fallback", "auto_dispatch_enabled", "active_assignment_id",
            "not_before_utc", "due_at_utc", "next_eligible_at_utc", "sort_order",
            "progress_percent", "progress_summary", "blocker_kind", "blocker_reason",
            "failure_code", "failure_reason", "origin", "version",
            "created_by", "updated_by", "created_at_utc", "updated_at_utc",
            "completed_at_utc", "failed_at_utc", "archived_at_utc", "parent_task_id");

        // ── sub_agent_runs：21 列（EF 实体声明；原 3 个迁移列由 EnsureCreated 建列）────
        await AssertColumnsAsync(db, "sub_agent_runs",
            "Id", "run_id", "parent_session_id",
            "parent_turn_id", "parent_command_id", "parent_run_id",
            "sub_session_id", "workspace_id", "agent_instance_id", "template_id", "Status",
            "started_at", "completed_at", "archive_path", "trace_id", "correlation_id",
            "error_message", "task_planning_metadata_json", "total_rounds", "total_tool_calls",
            "total_duration_ms");

        // ── orchestration_node_runs：19 列（outputs_json 已在 CREATE TABLE）──────────
        await AssertColumnsAsync(db, "orchestration_node_runs",
            "run_id", "node_id", "node_kind", "status", "attempt", "max_attempts",
            "claim_id", "lease_owner", "lease_until", "fencing_token",
            "execution_run_id", "sub_session_id", "output_summary", "artifact_reference",
            "outputs_json", "error_message", "started_at", "completed_at", "updated_at");

        // ── conversation_events：21 列（原 4 个迁移列已全部在 CREATE TABLE）──────────
        await AssertColumnsAsync(db, "conversation_events",
            "Id", "conversation_id", "sequence", "event_id", "workspace_id", "turn_id",
            "command_id", "run_id", "message_id", "type", "schema_version", "payload",
            "occurred_at", "committed_at", "correlation_id", "causation_id", "producer_event_id",
            "agent_id", "source_kind", "trace_id", "producer_component");

        // ── 依赖列索引（原「先 ALTER 补列再建索引」的顺序约束，现列已在表中，可直接建）──
        Assert.IsTrue(await IndexExistsAsync(db, "IX_workspace_tasks_workspace_sort"),
            "IX_workspace_tasks_workspace_sort 必须存在（引用 sort_order）");
        Assert.IsTrue(await IndexExistsAsync(db, "IX_workspace_tasks_workspace_parent"),
            "IX_workspace_tasks_workspace_parent 必须存在（引用 parent_task_id）");
        Assert.IsTrue(
            await IndexExistsAsync(db, SubAgentRunSchemaBootstrapper.ParentTurnStatusIndex),
            $"{SubAgentRunSchemaBootstrapper.ParentTurnStatusIndex} 必须存在");
    }

    [TestMethod]
    public async Task FreshDatabase_BootstrappersAreIdempotent_SecondRunKeepsSchemaUnchanged()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var db = new PlatformDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await WorkspaceTaskSchemaBootstrapper.EnsureCreatedAsync(db);
        await AgentOrchestrationSchemaBootstrapper.EnsureCreatedAsync(db);
        await SubAgentRunSchemaBootstrapper.EnsureCreatedAsync(db);
        var eventStore = BuildConversationEventStore(connection);
        await eventStore.EnsureTablesAsync(CancellationToken.None);

        var snapshot = await ReadColumnsAsync(db, "workspace_tasks");

        // 第二轮 bootstrap 不得抛错，也不得改变列集合。
        await WorkspaceTaskSchemaBootstrapper.EnsureCreatedAsync(db);
        await AgentOrchestrationSchemaBootstrapper.EnsureCreatedAsync(db);
        await SubAgentRunSchemaBootstrapper.EnsureCreatedAsync(db);
        await eventStore.EnsureTablesAsync(CancellationToken.None);

        var after = await ReadColumnsAsync(db, "workspace_tasks");
        CollectionAssert.AreEquivalent(snapshot, after, "重复 bootstrap 后 workspace_tasks 列集合必须不变。");
    }

    private static ConversationEventStore BuildConversationEventStore(SqliteConnection connection)
    {
        var services = new ServiceCollection();
        services.AddDbContextFactory<PlatformDbContext>(
            options => options.UseSqlite(connection));
        services.AddScoped(sp =>
            sp.GetRequiredService<IDbContextFactory<PlatformDbContext>>().CreateDbContext());
        var provider = services.BuildServiceProvider();

        return new ConversationEventStore(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new CommittedEventSignal(),
            NullLogger<ConversationEventStore>.Instance);
    }

    /// <summary>
    /// 双向比对：PRAGMA table_info 读出的实际列集合必须与期望集合完全一致（不区分大小写）。
    /// </summary>
    private static async Task AssertColumnsAsync(
        DbContext db,
        string table,
        params string[] expectedColumns)
    {
        var actual = await ReadColumnsAsync(db, table);
        var missing = expectedColumns
            .Except(actual, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var extra = actual
            .Except(expectedColumns, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.IsTrue(
            missing.Count == 0 && extra.Count == 0,
            $"表 {table} 列集合不一致。缺失: [{string.Join(", ", missing)}]；" +
            $"多余: [{string.Join(", ", extra)}]；实际: [{string.Join(", ", actual)}]");
    }

    private static async Task<List<string>> ReadColumnsAsync(DbContext db, string table)
    {
        var columns = new List<string>();
        var connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table})";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
    }

    private static async Task<bool> IndexExistsAsync(DbContext db, string indexName)
    {
        var connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT 1 FROM sqlite_master WHERE type = 'index' AND name = $name LIMIT 1;";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$name";
        parameter.Value = indexName;
        command.Parameters.Add(parameter);
        var value = await command.ExecuteScalarAsync();
        return value is not null && value is not DBNull;
    }
}
