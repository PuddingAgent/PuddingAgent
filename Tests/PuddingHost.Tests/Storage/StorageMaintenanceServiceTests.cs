using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Storage;
using PuddingCodeIntelligence.Contracts;
using PuddingHost.Storage;

namespace PuddingHost.Tests.Storage;

public sealed class StorageMaintenanceServiceTests : IDisposable
{
    private readonly string _dataRoot = Path.Combine(
        Path.GetTempPath(),
        "PuddingAgent",
        "storage-maintenance-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Preview_And_Execute_Only_Clean_Whitelisted_Derived_Data()
    {
        var paths = PuddingDataPaths.FromRoot(_dataRoot);
        Directory.CreateDirectory(paths.DatabasesRoot);
        Directory.CreateDirectory(Path.Combine(paths.DatabasesRoot, "code-index"));
        var platformPath = Path.Combine(paths.DatabasesRoot, "pudding_platform.db");
        var codeIndexPath = Path.Combine(paths.DatabasesRoot, "code-index", "code_index.db");
        await CreatePlatformDatabaseAsync(platformPath);
        await CreateCodeIndexDatabaseAsync(codeIndexPath);

        using var service = new StorageMaintenanceService(
            paths,
            new IdleCodeIndexScheduler(),
            NullLogger<StorageMaintenanceService>.Instance);

        var analysis = await service.AnalyzeAsync();
        Assert.True(analysis.TotalBytes > 0);
        Assert.True(analysis.Items.Single(item => item.ItemId == "platform.execution-facts").IsProtected);
        Assert.True(analysis.Items.Single(item =>
            item.ItemId == StorageMaintenanceTargetIds.Telemetry).CanClean);
        Assert.Equal(4, analysis.Items.Single(item =>
            item.ItemId == StorageMaintenanceTargetIds.DuplicateIndexes).RowCount);
        Assert.True(analysis.Items.Single(item =>
            item.ItemId == StorageMaintenanceTargetIds.ObsoleteCodeIndexScopes).CanClean);

        var preview = await service.PreviewCleanupAsync(new StorageCleanupPreviewRequest
        {
            TargetIds =
            [
                StorageMaintenanceTargetIds.Telemetry,
                StorageMaintenanceTargetIds.RuntimeActivity,
                StorageMaintenanceTargetIds.DuplicateIndexes,
                StorageMaintenanceTargetIds.ObsoleteCodeIndexScopes,
            ],
            RetentionDays = 14,
            CompactAfterCleanup = false,
        });

        Assert.Equal(13, preview.CandidateRows);
        var result = await service.ExecuteCleanupAsync(preview.PreviewId);
        Assert.Equal(9, result.DeletedRows);
        Assert.Equal(4, result.DroppedIndexes);
        Assert.Equal(1, result.RemovedCodeIndexScopes);

        await using (var connection = await OpenAsync(platformPath))
        {
            Assert.Equal(1, await ScalarAsync(connection, "SELECT COUNT(*) FROM telemetry_metric_events"));
            Assert.Equal(0, await ScalarAsync(connection, "SELECT COUNT(*) FROM context_layer_metric_events"));
            Assert.Equal(1, await ScalarAsync(connection, "SELECT COUNT(*) FROM runtime_activity"));
            Assert.Equal(1, await ScalarAsync(connection, "SELECT COUNT(*) FROM session_event_log"));
            Assert.Equal(1, await ScalarAsync(connection, "SELECT COUNT(*) FROM conversation_events"));
            Assert.Equal(
                0,
                await ScalarAsync(
                    connection,
                    "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name LIKE 'ix_ce_%'"));
            Assert.Equal(
                3,
                await ScalarAsync(
                    connection,
                    "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name LIKE 'IX_conversation_events_%'"));
            Assert.Equal(
                0,
                await ScalarAsync(
                    connection,
                    "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='ix_retention_conversation_events_committed_at'"));
        }

        await using (var connection = await OpenAsync(codeIndexPath))
        {
            Assert.Equal(
                0,
                await ScalarAsync(
                    connection,
                    "SELECT COUNT(*) FROM CodeProjects WHERE ProjectId='stale-scope'"));
            Assert.Equal(
                1,
                await ScalarAsync(
                    connection,
                    "SELECT COUNT(*) FROM CodeProjects WHERE ProjectId='active-scope'"));
            Assert.Equal(
                0,
                await ScalarAsync(
                    connection,
                    "SELECT COUNT(*) FROM CodeReferences WHERE ProjectId='stale-scope'"));
        }
    }

    [Fact]
    public async Task Unsupported_Target_Is_Rejected_Without_Writing()
    {
        var paths = PuddingDataPaths.FromRoot(_dataRoot);
        Directory.CreateDirectory(paths.DatabasesRoot);
        var platformPath = Path.Combine(paths.DatabasesRoot, "pudding_platform.db");
        await CreatePlatformDatabaseAsync(platformPath);
        using var service = new StorageMaintenanceService(
            paths,
            new IdleCodeIndexScheduler(),
            NullLogger<StorageMaintenanceService>.Instance);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.PreviewCleanupAsync(new StorageCleanupPreviewRequest
            {
                TargetIds = ["session_event_log"],
                RetentionDays = 14,
            }));

        await using var connection = await OpenAsync(platformPath);
        Assert.Equal(1, await ScalarAsync(connection, "SELECT COUNT(*) FROM session_event_log"));
    }

    [Fact]
    public async Task Execute_With_Compaction_Checkpoints_And_Vacuums_Modified_Database()
    {
        var paths = PuddingDataPaths.FromRoot(_dataRoot);
        Directory.CreateDirectory(paths.DatabasesRoot);
        var platformPath = Path.Combine(paths.DatabasesRoot, "pudding_platform.db");
        await CreatePlatformDatabaseAsync(platformPath);
        using var service = new StorageMaintenanceService(
            paths,
            new IdleCodeIndexScheduler(),
            NullLogger<StorageMaintenanceService>.Instance);
        var preview = await service.PreviewCleanupAsync(new StorageCleanupPreviewRequest
        {
            TargetIds = [StorageMaintenanceTargetIds.Telemetry],
            RetentionDays = 14,
            CompactAfterCleanup = true,
        });

        var result = await service.ExecuteCleanupAsync(preview.PreviewId);

        Assert.Contains("Pudding 平台数据库", result.CompactedDatabases);
        Assert.Equal(2, result.DeletedRows);
        await using var connection = await OpenAsync(platformPath);
        Assert.Equal(1, await ScalarAsync(connection, "SELECT COUNT(*) FROM telemetry_metric_events"));
        Assert.Equal(0, await ScalarAsync(connection, "SELECT COUNT(*) FROM context_layer_metric_events"));
    }

    [Fact]
    public async Task Execute_Skips_Code_Index_Scope_That_Became_Fresh_After_Preview()
    {
        var paths = PuddingDataPaths.FromRoot(_dataRoot);
        Directory.CreateDirectory(Path.Combine(paths.DatabasesRoot, "code-index"));
        var codeIndexPath = Path.Combine(paths.DatabasesRoot, "code-index", "code_index.db");
        await CreateCodeIndexDatabaseAsync(codeIndexPath);
        using var service = new StorageMaintenanceService(
            paths,
            new IdleCodeIndexScheduler(),
            NullLogger<StorageMaintenanceService>.Instance);
        var preview = await service.PreviewCleanupAsync(new StorageCleanupPreviewRequest
        {
            TargetIds = [StorageMaintenanceTargetIds.ObsoleteCodeIndexScopes],
            RetentionDays = 14,
            CompactAfterCleanup = false,
        });

        await using (var connection = await OpenAsync(codeIndexPath))
        {
            await ExecuteAsync(
                connection,
                "UPDATE CodeProjects SET UpdatedAtUtc = $fresh WHERE ProjectId = 'stale-scope'",
                ("$fresh", DateTimeOffset.UtcNow.ToString("O")));
        }

        var result = await service.ExecuteCleanupAsync(preview.PreviewId);

        Assert.Equal(0, result.RemovedCodeIndexScopes);
        Assert.Contains(result.Warnings, warning => warning.Contains("状态已变化", StringComparison.Ordinal));
        await using var verification = await OpenAsync(codeIndexPath);
        Assert.Equal(
            1,
            await ScalarAsync(
                verification,
                "SELECT COUNT(*) FROM CodeProjects WHERE ProjectId='stale-scope'"));
    }

    private static async Task CreatePlatformDatabaseAsync(string path)
    {
        await using var connection = await OpenAsync(path);
        await ExecuteAsync(connection, """
            CREATE TABLE telemetry_metric_events (Id INTEGER PRIMARY KEY, occurred_at_utc TEXT NOT NULL);
            CREATE TABLE context_layer_metric_events (Id INTEGER PRIMARY KEY, occurred_at_utc TEXT NOT NULL);
            CREATE TABLE runtime_activity (Id INTEGER PRIMARY KEY, started_at_utc TEXT NOT NULL);
            CREATE TABLE session_event_log (Id INTEGER PRIMARY KEY, recorded_at TEXT NOT NULL);
            CREATE TABLE conversation_events (
                Id INTEGER PRIMARY KEY,
                conversation_id TEXT NOT NULL,
                sequence INTEGER NOT NULL,
                event_id TEXT NOT NULL,
                turn_id TEXT NOT NULL,
                type TEXT NOT NULL,
                committed_at TEXT NOT NULL);

            CREATE UNIQUE INDEX IX_conversation_events_conversation_id_sequence
                ON conversation_events(conversation_id, sequence);
            CREATE UNIQUE INDEX IX_conversation_events_event_id
                ON conversation_events(event_id);
            CREATE INDEX IX_conversation_events_turn_id_type
                ON conversation_events(turn_id, type);
            CREATE UNIQUE INDEX ix_ce_seq ON conversation_events(conversation_id, sequence);
            CREATE UNIQUE INDEX ix_ce_eid ON conversation_events(event_id);
            CREATE INDEX ix_ce_turn ON conversation_events(turn_id, type);
            CREATE INDEX ix_retention_conversation_events_committed_at
                ON conversation_events(committed_at);
            """);

        var old = DateTimeOffset.UtcNow.AddDays(-30).ToString("O");
        var fresh = DateTimeOffset.UtcNow.AddDays(-1).ToString("O");
        await ExecuteAsync(connection, """
            INSERT INTO telemetry_metric_events VALUES (1, $old), (2, $fresh);
            INSERT INTO context_layer_metric_events VALUES (1, $old);
            INSERT INTO runtime_activity VALUES (1, $old), (2, $fresh);
            INSERT INTO session_event_log VALUES (1, $old);
            INSERT INTO conversation_events
                VALUES (1, 'conversation-1', 1, 'event-1', 'turn-1', 'delta', $old);
            """, ("$old", old), ("$fresh", fresh));
    }

    private static async Task CreateCodeIndexDatabaseAsync(string path)
    {
        await using var connection = await OpenAsync(path);
        await ExecuteAsync(connection, """
            CREATE TABLE CodeProjects (
                WorkspaceId TEXT NOT NULL,
                ProjectId TEXT NOT NULL,
                ProjectPath TEXT,
                Status TEXT NOT NULL,
                DisplayName TEXT,
                AddedAtUtc TEXT,
                UpdatedAtUtc TEXT,
                Source TEXT,
                ScopeState TEXT,
                PRIMARY KEY (WorkspaceId, ProjectId));
            CREATE TABLE CodeFiles (WorkspaceId TEXT, ProjectId TEXT, Value TEXT);
            CREATE TABLE CodeSymbols (WorkspaceId TEXT, ProjectId TEXT, Value TEXT);
            CREATE TABLE CodeRelations (WorkspaceId TEXT, ProjectId TEXT, Value TEXT);
            CREATE TABLE CodeReferences (WorkspaceId TEXT, ProjectId TEXT, Value TEXT);
            CREATE TABLE CodeIndexRuns (WorkspaceId TEXT, ProjectId TEXT, Value TEXT);
            """);
        var stale = DateTimeOffset.UtcNow.AddDays(-3).ToString("O");
        var fresh = DateTimeOffset.UtcNow.ToString("O");
        await ExecuteAsync(connection, """
            INSERT INTO CodeProjects VALUES
                ('default', 'stale-scope', 'C:\stale', 'Registering', 'stale', $stale, $stale, NULL, NULL),
                ('default', 'active-scope', 'C:\active', 'Active', 'active', $fresh, $fresh, 'Auto', 'Active');
            INSERT INTO CodeFiles VALUES ('default', 'stale-scope', 'x');
            INSERT INTO CodeSymbols VALUES ('default', 'stale-scope', 'x');
            INSERT INTO CodeRelations VALUES ('default', 'stale-scope', 'x');
            INSERT INTO CodeReferences VALUES ('default', 'stale-scope', 'x');
            INSERT INTO CodeIndexRuns VALUES ('default', 'stale-scope', 'x');
            INSERT INTO CodeReferences VALUES ('default', 'active-scope', 'x');
            """, ("$stale", stale), ("$fresh", fresh));
    }

    private static async Task<SqliteConnection> OpenAsync(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        await connection.OpenAsync();
        return connection;
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataRoot))
            Directory.Delete(_dataRoot, recursive: true);
    }

    private sealed class IdleCodeIndexScheduler : ICodeIndexScheduler
    {
        public void Enqueue(string workspaceId, string scopeId) { }
        public int GetQueueDepth(string workspaceId) => 0;
        public bool IsIndexing(string workspaceId, string scopeId) => false;
    }
}
