using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Storage;
using PuddingCodeIntelligence.Contracts;
using PuddingHost.Storage;
using PuddingPlatform.Services;
using PuddingPlatform.Services.StorageManagement;

namespace PuddingHost.Tests.Storage;

/// <summary>
/// ADR-076 存储管理：目录保护门禁、策略 CAS、快照合并、小批执行器与
/// 作业 cursor 续行、Preview 保护与证据归档先行的定向测试。
/// 全部使用系统 Temp 隔离 DataRoot，不触碰 D:\data。
/// </summary>
public sealed class StorageManagementAdministrationTests : IDisposable
{
    private readonly string _dataRoot = Path.Combine(
        Path.GetTempPath(),
        "PuddingAgent",
        "storage-admin-tests",
        Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dataRoot))
                Directory.Delete(_dataRoot, recursive: true);
        }
        catch (IOException)
        {
            // Windows 句柄延迟释放时忽略清理失败。
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // ─── 目录保护门禁 ─────────────────────────────────────────────

    [Fact]
    public void Catalog_Never_Maps_Protected_Tables()
    {
        var protectedTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "chat_messages",
            "session_event_log",
            "llm_gateway_usage_events",
            "token_usage_events",
            "token_usage_stats",
            "workspace_tasks",
            "app_users",
        };

        foreach (var definition in StorageDataClassCatalog.Definitions)
        {
            foreach (var mapping in definition.Tables)
            {
                Assert.False(
                    protectedTables.Contains(mapping.Table),
                    $"目录目标 {definition.TargetId} 映射了受保护表 {mapping.Table}");
            }
        }

        // Evidence 目标永不进入人工选择器；UserData 不存在于清理目录。
        var evidence = StorageDataClassCatalog.Require(StorageAdminTargetIds.ConversationEventsEvidence);
        Assert.False(evidence.ManualCleanupAllowed);
        Assert.False(evidence.ShowInManualSelector);
        Assert.True(evidence.Tables.Single().ArchiveBeforeDelete);
    }

    [Fact]
    public void Catalog_DefaultAutomatic_Disabled_Without_Rollup()
    {
        // 首期未实现长期聚合：telemetry-raw / context-layer-raw 默认自动清理必须关闭。
        Assert.True(StorageDataClassCatalog
            .Require(StorageAdminTargetIds.TelemetryRaw)
            .RequiresRollupBeforeAutomatic);
        Assert.True(StorageDataClassCatalog
            .Require(StorageAdminTargetIds.ContextLayerRaw)
            .RequiresRollupBeforeAutomatic);
    }

    [Fact]
    public void TranslateLegacyTarget_Maps_Old_Endpoint_Ids()
    {
        var translated = StorageDataClassCatalog
            .TranslateLegacyTarget(StorageAdminTargetIds.LegacyTelemetry)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(
            new[] { StorageAdminTargetIds.ContextLayerRaw, StorageAdminTargetIds.TelemetryRaw },
            translated);

        Assert.Equal(
            [StorageAdminTargetIds.RedundantIndexes],
            StorageDataClassCatalog.TranslateLegacyTarget(StorageAdminTargetIds.LegacyDuplicateIndexes));
    }

    // ─── 策略（system.json CAS + fail closed）────────────────────

    [Fact]
    public async Task Policy_Update_Requires_Expected_Revision()
    {
        var paths = CreatePaths();
        Directory.CreateDirectory(paths.ConfigRoot);
        await File.WriteAllTextAsync(
            paths.SystemConfigFile("system.json"),
            """{"storageManagement":{"policyRevision":5}}""");
        var service = CreatePolicyService(paths);

        var conflict = await Assert.ThrowsAsync<StorageMaintenanceCoordinator.StorageAdminException>(
            () => service.UpdateAsync(new StorageRetentionPolicyUpdateRequest
            {
                ExpectedRevision = 4,
                Targets = [new StorageRetentionPolicyTargetUpdateDto
                {
                    TargetId = StorageAdminTargetIds.DebugPayload,
                    RetentionDays = 10,
                }],
            }));
        Assert.Equal(StorageAdminErrorCodes.PolicyConflict, conflict.ErrorCode);

        var updated = await service.UpdateAsync(new StorageRetentionPolicyUpdateRequest
        {
            ExpectedRevision = 5,
            Targets = [new StorageRetentionPolicyTargetUpdateDto
            {
                TargetId = StorageAdminTargetIds.DebugPayload,
                RetentionDays = 10,
            }],
        });
        Assert.Equal(6, updated.PolicyRevision);

        var effective = updated.Targets.Single(t => t.TargetId == StorageAdminTargetIds.DebugPayload);
        Assert.True(effective.Enabled);
        Assert.Equal(10, effective.RetentionDays);

        // JsonNode 写保留其它节。
        var raw = await File.ReadAllTextAsync(paths.SystemConfigFile("system.json"));
        Assert.Contains("\"policyRevision\":6", raw);
    }

    [Fact]
    public async Task Policy_Rejects_Zero_And_Out_Of_Range_Days()
    {
        var paths = CreatePaths();
        var service = CreatePolicyService(paths);

        await Assert.ThrowsAsync<StorageMaintenanceCoordinator.StorageAdminException>(
            () => service.UpdateAsync(new StorageRetentionPolicyUpdateRequest
            {
                ExpectedRevision = 0,
                Targets = [new StorageRetentionPolicyTargetUpdateDto
                {
                    TargetId = StorageAdminTargetIds.DebugPayload,
                    RetentionDays = 0,
                }],
            }));

        await Assert.ThrowsAsync<StorageMaintenanceCoordinator.StorageAdminException>(
            () => service.UpdateAsync(new StorageRetentionPolicyUpdateRequest
            {
                ExpectedRevision = 0,
                Targets = [new StorageRetentionPolicyTargetUpdateDto
                {
                    TargetId = StorageAdminTargetIds.DebugPayload,
                    RetentionDays = 99999,
                }],
            }));
    }

    [Fact]
    public async Task Policy_Missing_File_Uses_Safe_Defaults_And_Fails_Closed_On_Garbage()
    {
        var paths = CreatePaths();
        var service = CreatePolicyService(paths);

        var defaults = await service.GetEffectivePolicyAsync();
        Assert.True(defaults.AutomaticCleanupEnabled);
        Assert.True(defaults.Targets.Single(t => t.TargetId == StorageAdminTargetIds.DebugPayload).Enabled);
        // 聚合未实现 → 原始遥测自动清理默认关闭（fail-safe，ADR-076 §4.3）。
        Assert.False(defaults.Targets.Single(t => t.TargetId == StorageAdminTargetIds.TelemetryRaw).Enabled);
        Assert.Empty(defaults.Warnings);

        Directory.CreateDirectory(paths.ConfigRoot);
        await File.WriteAllTextAsync(
            paths.SystemConfigFile("system.json"),
            """{"storageManagement":{"automaticCleanup":{"targets":{"diagnostics.debug-payload":{"retentionDays":0}}}}}""");
        service.InvalidateCache();
        var suspended = await service.GetEffectivePolicyAsync();
        var target = suspended.Targets.Single(t => t.TargetId == StorageAdminTargetIds.DebugPayload);
        Assert.False(target.Enabled);
        Assert.True(target.Suspended);
        Assert.Contains(suspended.Warnings, w => w.Contains("diagnostics.debug-payload"));
    }

    // ─── 快照合并 ─────────────────────────────────────────────────

    [Fact]
    public void Snapshot_Merge_Keeps_Previous_Estimate_When_Slice_Fails()
    {
        var store = new StorageInventorySnapshotStore(
            CreatePaths(), NullLogger<StorageInventorySnapshotStore>.Instance);

        store.MergeSnapshot(null, [Class(StorageAdminTargetIds.TelemetryRaw, 123_456)], isRefreshing: true);
        // 采样失败（Unavailable 且无新值）不得清空上一份有效估算。
        store.MergeSnapshot(null, [new StorageInventoryClassDto
        {
            TargetId = StorageAdminTargetIds.TelemetryRaw,
            DisplayName = "遥测",
            EstimateState = StorageEstimateState.Unavailable,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        }], isRefreshing: false);

        var snapshot = store.Current;
        Assert.Equal(123_456, snapshot.Classes.Single().EstimatedBytes);
        Assert.False(snapshot.IsRefreshing);
    }

    // ─── 执行器（小批 / 清字段 / cursor）─────────────────────────

    [Fact]
    public async Task Executor_ClearField_Keeps_Rows_And_Clears_Only_Old_Payloads()
    {
        var paths = CreatePaths();
        var platformPath = await CreatePlatformDatabaseAsync(paths, seedConversationEvents: 0);
        var cutoff = DateTimeOffset.UtcNow.AddDays(-7);
        var old = cutoff.AddDays(-1).ToString("O");
        var recent = DateTimeOffset.UtcNow.AddDays(-1).ToString("O");
        await ExecuteAsync(platformPath,
            $"INSERT INTO telemetry_metric_events (metric_id, occurred_at_utc, debug_json) VALUES " +
            $"('m-old', '{old}', '{{\"big\":1}}'), ('m-new', '{recent}', '{{\"big\":2}}')");

        var job = NewJob([(StorageAdminTargetIds.DebugPayload, StorageCleanupKind.ClearField)]);
        var executor = CreateExecutor(paths);
        var result = await executor.ExecuteRoundAsync(job, CancellationToken.None);

        Assert.True(result.AllActionsComplete);
        Assert.Equal(1, job.ClearedRows);
        Assert.Equal(0, job.DeletedRows);
        Assert.Equal(2L, await ScalarAsync(platformPath, "SELECT COUNT(*) FROM telemetry_metric_events"));
        Assert.Equal(
            DBNull.Value,
            await ScalarAsync(platformPath, "SELECT debug_json FROM telemetry_metric_events WHERE metric_id='m-old'"));
        Assert.NotEqual(
            DBNull.Value,
            await ScalarAsync(platformPath, "SELECT debug_json FROM telemetry_metric_events WHERE metric_id='m-new'"));
    }

    [Fact]
    public async Task Executor_Respects_Batch_Cap_And_Resumes_From_Cursor()
    {
        var paths = CreatePaths();
        var platformPath = await CreatePlatformDatabaseAsync(paths, seedConversationEvents: 0);
        var cutoff = DateTimeOffset.UtcNow.AddDays(-30);
        var old = cutoff.AddDays(-1).ToString("O");
        for (var i = 0; i < 250; i++)
        {
            await ExecuteAsync(platformPath,
                $"INSERT INTO runtime_activity (activity_id, trace_id, correlation_id, component, operation, status, started_at_utc, severity) " +
                $"VALUES ('a{i}', 't', 'c', 'comp', 'op', 'Succeeded', '{old}', 'Information')");
        }

        var job = NewJob(
            [(StorageAdminTargetIds.RuntimeActivity, StorageCleanupKind.DeleteRows)],
            budget: new StorageCleanupBudget
            {
                BatchSize = 50,
                BatchDelayMs = 0,
                MaxBatchesPerTargetPerRound = 2,
            });
        var executor = CreateExecutor(paths);

        var round1 = await executor.ExecuteRoundAsync(job, CancellationToken.None);
        Assert.False(round1.AllActionsComplete);
        Assert.Equal(100, job.DeletedRows);

        // cursor 持久化 → 作业存储重载 → 续行不回退。
        var jobStore = new StorageMaintenanceJobStore(paths, NullLogger<StorageMaintenanceJobStore>.Instance);
        await jobStore.PersistAsync(job);
        await jobStore.LoadAsync();
        var restored = jobStore.Get(job.JobId);
        Assert.NotNull(restored);
        Assert.Equal(job.Cursors[StorageAdminTargetIds.RuntimeActivity], restored!.Cursors[StorageAdminTargetIds.RuntimeActivity]);

        var round2 = await executor.ExecuteRoundAsync(restored, CancellationToken.None);
        Assert.Equal(200, restored.DeletedRows);
        var round3 = await executor.ExecuteRoundAsync(restored, CancellationToken.None);
        Assert.True(round3.AllActionsComplete);
        Assert.Equal(250, restored.DeletedRows);
        Assert.Equal(0L, await ScalarAsync(platformPath, "SELECT COUNT(*) FROM runtime_activity"));
    }

    // ─── 协调器（保护 + 归档先行 + 单 writer 队列）───────────────

    [Fact]
    public async Task Coordinator_Preview_Rejects_Protected_And_Unknown_Targets()
    {
        var paths = CreatePaths();
        using var coordinator = CreateCoordinator(paths, out _);

        var snapshot = new StorageInventorySnapshotDto
        {
            SnapshotId = Guid.NewGuid(),
            Revision = 1,
            SchemaVersion = 1,
            CapturedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            Databases = [],
            Classes = [],
            IsRefreshing = false,
            Warnings = [],
        };

        var protectedTarget = await Assert.ThrowsAsync<StorageMaintenanceCoordinator.StorageAdminException>(
            () => coordinator.CreatePreviewAsync(new StorageCleanupPreviewRequestDto
            {
                TargetIds = [StorageAdminTargetIds.ConversationEventsEvidence],
                OlderThanDays = 30,
            }, 0, snapshot));
        Assert.Equal(StorageAdminErrorCodes.TargetProtected, protectedTarget.ErrorCode);

        var unknown = await Assert.ThrowsAsync<StorageMaintenanceCoordinator.StorageAdminException>(
            () => coordinator.CreatePreviewAsync(new StorageCleanupPreviewRequestDto
            {
                TargetIds = ["chat_messages"],
                OlderThanDays = 30,
            }, 0, snapshot));
        Assert.Equal(StorageAdminErrorCodes.TargetUnknown, unknown.ErrorCode);
    }

    [Fact]
    public async Task Coordinator_Automatic_Evidence_Job_Archives_Before_Delete()
    {
        var paths = CreatePaths();
        var platformPath = await CreatePlatformDatabaseAsync(paths, seedConversationEvents: 3);
        using var coordinator = CreateCoordinator(paths, out var jobStore);

        await coordinator.StartAsync(CancellationToken.None);
        try
        {
            var cutoff = DateTimeOffset.UtcNow.AddDays(-30);
            var job = await coordinator.SubmitAutomaticAsync(
                [StorageAdminTargetIds.ConversationEventsEvidence], cutoff);
            var terminal = await coordinator.WaitForNextCheckpointAsync(
                job.JobId, TimeSpan.FromSeconds(30));
            for (var i = 0; i < 20 && terminal.Status is StorageCleanupJobStatus.Queued or StorageCleanupJobStatus.Running; i++)
            {
                terminal = await coordinator.WaitForNextCheckpointAsync(job.JobId, TimeSpan.FromSeconds(5));
            }

            Assert.True(terminal.Status is StorageCleanupJobStatus.Completed or StorageCleanupJobStatus.Partial,
                $"status={terminal.Status}");
            Assert.Equal(3, terminal.DeletedRows);
            Assert.Equal(0L, await ScalarAsync(platformPath, "SELECT COUNT(*) FROM conversation_events"));

            var archiveDir = Path.Combine(paths.RetentionArchiveRoot, DateTimeOffset.UtcNow.UtcDateTime.ToString("yyyy-MM-dd"));
            var archiveFile = Path.Combine(archiveDir, "conversation_events.jsonl");
            Assert.True(File.Exists(archiveFile), "证据必须先归档后删除");
            var lines = await File.ReadAllLinesAsync(archiveFile);
            Assert.Equal(3, lines.Count(line => !string.IsNullOrWhiteSpace(line)));

            // durable 作业事实在 DataRoot，不在 platform.db。
            var durable = jobStore.Get(job.JobId);
            Assert.NotNull(durable);
        }
        finally
        {
            await coordinator.StopAsync(CancellationToken.None);
        }
    }

    // ─── helpers ──────────────────────────────────────────────────

    private PuddingDataPaths CreatePaths()
    {
        var paths = PuddingDataPaths.FromRoot(_dataRoot);
        Directory.CreateDirectory(paths.DatabasesRoot);
        return paths;
    }

    private static StorageRetentionPolicyService CreatePolicyService(PuddingDataPaths paths) =>
        new(paths, NullLogger<StorageRetentionPolicyService>.Instance);

    private StorageCleanupExecutor CreateExecutor(PuddingDataPaths paths) =>
        new(
            paths,
            new RetentionArchiveWriter(paths, NullLogger<RetentionArchiveWriter>.Instance),
            [],
            NullLogger<StorageCleanupExecutor>.Instance);

    private StorageMaintenanceCoordinator CreateCoordinator(PuddingDataPaths paths, out StorageMaintenanceJobStore jobStore)
    {
        jobStore = new StorageMaintenanceJobStore(paths, NullLogger<StorageMaintenanceJobStore>.Instance);
        var executor = new StorageCleanupExecutor(
            paths,
            new RetentionArchiveWriter(paths, NullLogger<RetentionArchiveWriter>.Instance),
            new IStorageDerivedTargetHandler[]
            {
                new CodeIndexScopeCleanupHandler(paths, new IdleCodeIndexScheduler(), NullLogger<CodeIndexScopeCleanupHandler>.Instance),
                new RedundantIndexCleanupHandler(paths, NullLogger<RedundantIndexCleanupHandler>.Instance),
            },
            NullLogger<StorageCleanupExecutor>.Instance);
        return new StorageMaintenanceCoordinator(
            jobStore,
            executor,
            [],
            paths,
            NullLogger<StorageMaintenanceCoordinator>.Instance);
    }

    private static StorageCleanupJob NewJob(
        IReadOnlyList<(string TargetId, StorageCleanupKind Kind)> actions,
        StorageCleanupBudget? budget = null)
    {
        return new StorageCleanupJob
        {
            JobId = Guid.NewGuid(),
            Trigger = "manual",
            CutoffUtc = DateTimeOffset.UtcNow.AddDays(-7),
            TargetIds = [.. actions.Select(a => a.TargetId)],
            Actions = [.. actions.Select(a => new StorageCleanupAction
            {
                TargetId = a.TargetId,
                Kind = a.Kind,
                DatabaseFile = StorageDataClassCatalog.PlatformDatabaseFile,
                Table = a.TargetId switch
                {
                    StorageAdminTargetIds.DebugPayload => "telemetry_metric_events",
                    StorageAdminTargetIds.RuntimeActivity => "runtime_activity",
                    _ => "telemetry_metric_events",
                },
                TimestampColumn = a.TargetId switch
                {
                    StorageAdminTargetIds.RuntimeActivity => "started_at_utc",
                    _ => "occurred_at_utc",
                },
                ClearColumns = a.Kind == StorageCleanupKind.ClearField ? ["debug_json"] : null,
            })],
            Budget = budget ?? new StorageCleanupBudget { BatchDelayMs = 0, MaxBatchesPerTargetPerRound = 200 },
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
    }

    private static StorageInventoryClassDto Class(string targetId, long bytes) => new()
    {
        TargetId = targetId,
        DisplayName = targetId,
        EstimatedBytes = bytes,
        EstimatedRows = 10,
        EstimateState = StorageEstimateState.Updated,
        UpdatedAtUtc = DateTimeOffset.UtcNow,
    };

    private async Task<string> CreatePlatformDatabaseAsync(PuddingDataPaths paths, int seedConversationEvents)
    {
        var platformPath = Path.Combine(paths.DatabasesRoot, "pudding_platform.db");
        await ExecuteAsync(platformPath, """
            CREATE TABLE telemetry_metric_events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                metric_id TEXT NOT NULL,
                occurred_at_utc TEXT NOT NULL,
                debug_json TEXT
            );
            CREATE INDEX IX_telemetry_metric_events_occurred_at_utc
                ON telemetry_metric_events (occurred_at_utc);
            CREATE TABLE runtime_activity (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                activity_id TEXT NOT NULL,
                trace_id TEXT NOT NULL,
                correlation_id TEXT NOT NULL,
                component TEXT NOT NULL,
                operation TEXT NOT NULL,
                status TEXT NOT NULL,
                started_at_utc TEXT NOT NULL,
                severity TEXT NOT NULL,
                metadata_json TEXT
            );
            CREATE INDEX IX_runtime_activity_started_at_utc
                ON runtime_activity (started_at_utc);
            CREATE TABLE conversation_events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                conversation_id TEXT NOT NULL,
                sequence INTEGER NOT NULL,
                event_id TEXT NOT NULL,
                workspace_id TEXT NOT NULL,
                turn_id TEXT NOT NULL,
                type TEXT NOT NULL,
                schema_version INTEGER NOT NULL DEFAULT 1,
                payload TEXT NOT NULL,
                occurred_at TEXT NOT NULL,
                committed_at TEXT NOT NULL
            );
            CREATE INDEX IX_conversation_events_committed_at
                ON conversation_events (committed_at);
            """);

        for (var i = 0; i < seedConversationEvents; i++)
        {
            var committed = DateTimeOffset.UtcNow.AddDays(-40).ToString("O");
            await ExecuteAsync(platformPath,
                $"INSERT INTO conversation_events (conversation_id, sequence, event_id, workspace_id, turn_id, type, payload, occurred_at, committed_at) " +
                $"VALUES ('c', {i}, 'e{i}', 'w', 't', 'user_message', '{{}}', '{committed}', '{committed}')");
        }

        return platformPath;
    }

    private static async Task ExecuteAsync(string databasePath, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<object> ScalarAsync(string databasePath, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync() ?? DBNull.Value;
    }

    private sealed class IdleCodeIndexScheduler : ICodeIndexScheduler
    {
        public void Enqueue(string workspaceId, string scopeId) { }
        public int GetQueueDepth(string workspaceId) => 0;
        public bool IsIndexing(string workspaceId, string scopeId) => false;
    }
}
