using Microsoft.Data.Sqlite;
using PuddingCodeIndex.Contracts;
using PuddingCodeIndex.Services;
using System.Data.Common;
using System.Globalization;

namespace PuddingCodeIndex.Storage;

/// <summary>
/// 源维护状态的持久化（D2，2026-10-02 高磁盘读取修复）：持久 manifest（源指纹 + 各消费者水位）、
/// 维护账本（世代/版本/扫描水位/脏标记）与待重试路径。
/// <para>
/// 与索引结果分开的是**用途**而不是数据库：manifest/账本回答「要不要处理、处理到哪一版」，
/// 索引表回答「解析出了什么」。两者最终要在同一个事务里提交（后续阶段的 ReplaceFilesAsync），
/// 因此它们放在同一个 SQLite 库里。
/// </para>
/// <para>
/// 路径比较沿用组件身份（Windows 不区分大小写），时间戳统一 ISO-8601（与既有表一致）。
/// </para>
/// </summary>
public sealed partial class SqliteCodeIndexStore
{
    /// <summary>建表（幂等）：manifest、消费者水位、账本、待重试。</summary>
    private static async Task EnsureSourceMaintenanceSchemaAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await ExecuteNonQueryAsync(connection, """
            CREATE TABLE IF NOT EXISTS CodeSourceManifest (
                WorkspaceId TEXT NOT NULL,
                ProjectId TEXT NOT NULL,
                FilePath TEXT NOT NULL,
                LastWriteTimeUtc TEXT NULL,
                Length INTEGER NULL,
                ContentHash TEXT NULL,
                Complete INTEGER NOT NULL DEFAULT 1,
                UpdatedAtUtc TEXT NOT NULL,
                PRIMARY KEY (WorkspaceId, ProjectId, FilePath)
            );

            CREATE TABLE IF NOT EXISTS CodeSourceAppliedVersions (
                WorkspaceId TEXT NOT NULL,
                ProjectId TEXT NOT NULL,
                FilePath TEXT NOT NULL,
                ProviderId TEXT NOT NULL,
                ParserPolicyFingerprint TEXT NOT NULL,
                SemanticInputFingerprint TEXT NOT NULL,
                AppliedVersion INTEGER NOT NULL,
                PRIMARY KEY (WorkspaceId, ProjectId, FilePath, ProviderId)
            );

            CREATE TABLE IF NOT EXISTS CodeIndexMaintenanceLedger (
                WorkspaceId TEXT NOT NULL,
                ProjectId TEXT NOT NULL,
                Epoch INTEGER NOT NULL,
                DesiredVersion INTEGER NOT NULL,
                CommittedVersion INTEGER NOT NULL,
                ScanWatermarkUtc TEXT NULL,
                DirtyAgain INTEGER NOT NULL DEFAULT 0,
                UpdatedAtUtc TEXT NOT NULL,
                PRIMARY KEY (WorkspaceId, ProjectId)
            );

            CREATE TABLE IF NOT EXISTS CodeIndexMaintenanceConsumerWatermarks (
                WorkspaceId TEXT NOT NULL,
                ProjectId TEXT NOT NULL,
                ProviderId TEXT NOT NULL,
                AppliedVersion INTEGER NOT NULL,
                PRIMARY KEY (WorkspaceId, ProjectId, ProviderId)
            );

            CREATE TABLE IF NOT EXISTS CodeIndexMaintenanceRetries (
                WorkspaceId TEXT NOT NULL,
                ProjectId TEXT NOT NULL,
                FilePath TEXT NOT NULL,
                Reason TEXT NOT NULL,
                Attempts INTEGER NOT NULL,
                FirstFailedAtUtc TEXT NOT NULL,
                NextAttemptAtUtc TEXT NOT NULL,
                PRIMARY KEY (WorkspaceId, ProjectId, FilePath)
            );

            CREATE INDEX IF NOT EXISTS IX_CodeSourceManifest_Workspace_Project
                ON CodeSourceManifest (WorkspaceId, ProjectId);
            """, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<CodeSourceMaintenanceSnapshot> LoadSourceMaintenanceAsync(
        string workspaceId,
        string projectId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var manifest = await LoadManifestAsync(connection, workspaceId, projectId, cancellationToken)
            .ConfigureAwait(false);
        var ledger = await LoadLedgerAsync(connection, workspaceId, projectId, cancellationToken)
            .ConfigureAwait(false);

        return new CodeSourceMaintenanceSnapshot(manifest, ledger);
    }

    /// <inheritdoc />
    public async Task<int> SaveSourceManifestAsync(
        string workspaceId,
        string projectId,
        IReadOnlyCollection<CodeSourceEntry> entries,
        IReadOnlyCollection<string> removedFilePaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(removedFilePaths);

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        if (entries.Count == 0 && removedFilePaths.Count == 0)
            return 0;

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var updatedAtUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        var affected = 0;

        foreach (var entry in entries)
        {
            if (entry is null || string.IsNullOrWhiteSpace(entry.FilePath))
                continue;

            affected += await ExecuteNonQueryCountAsync(connection, transaction, """
                INSERT INTO CodeSourceManifest (
                    WorkspaceId, ProjectId, FilePath, LastWriteTimeUtc, Length, ContentHash, Complete, UpdatedAtUtc)
                VALUES ($workspaceId, $projectId, $filePath, $lastWriteTimeUtc, $length, $contentHash, $complete, $updatedAtUtc)
                ON CONFLICT(WorkspaceId, ProjectId, FilePath) DO UPDATE SET
                    LastWriteTimeUtc = excluded.LastWriteTimeUtc,
                    Length = excluded.Length,
                    ContentHash = excluded.ContentHash,
                    Complete = excluded.Complete,
                    UpdatedAtUtc = excluded.UpdatedAtUtc;
                """,
                cancellationToken,
                ("$workspaceId", workspaceId),
                ("$projectId", projectId),
                ("$filePath", entry.FilePath),
                ("$lastWriteTimeUtc", FormatDate(entry.Fingerprint?.LastWriteTimeUtc)),
                ("$length", entry.Fingerprint?.Length),
                ("$contentHash", entry.Fingerprint?.ContentHash),
                ("$complete", entry.Complete ? 1 : 0),
                ("$updatedAtUtc", updatedAtUtc)).ConfigureAwait(false);

            // 消费者水位整体替换：一个消费者被移除（不再注册）时它的旧水位不得留下。
            await ExecuteNonQueryCountAsync(connection, transaction, """
                DELETE FROM CodeSourceAppliedVersions
                WHERE WorkspaceId = $workspaceId AND ProjectId = $projectId AND FilePath = $filePath;
                """,
                cancellationToken,
                ("$workspaceId", workspaceId),
                ("$projectId", projectId),
                ("$filePath", entry.FilePath)).ConfigureAwait(false);

            foreach (var applied in entry.AppliedVersions ?? [])
            {
                if (applied is null || string.IsNullOrWhiteSpace(applied.ProviderId))
                    continue;

                await ExecuteNonQueryCountAsync(connection, transaction, """
                    INSERT INTO CodeSourceAppliedVersions (
                        WorkspaceId, ProjectId, FilePath, ProviderId,
                        ParserPolicyFingerprint, SemanticInputFingerprint, AppliedVersion)
                    VALUES ($workspaceId, $projectId, $filePath, $providerId, $policy, $semantic, $appliedVersion);
                    """,
                    cancellationToken,
                    ("$workspaceId", workspaceId),
                    ("$projectId", projectId),
                    ("$filePath", entry.FilePath),
                    ("$providerId", applied.ProviderId),
                    ("$policy", applied.ParserPolicyFingerprint),
                    ("$semantic", applied.SemanticInputFingerprint),
                    ("$appliedVersion", applied.AppliedVersion)).ConfigureAwait(false);
            }
        }

        foreach (var removedPath in removedFilePaths)
        {
            if (string.IsNullOrWhiteSpace(removedPath))
                continue;

            // 删除路径必须连消费者水位一起删，否则同名新文件会继承旧文件的「已应用」状态。
            await ExecuteNonQueryCountAsync(connection, transaction, """
                DELETE FROM CodeSourceAppliedVersions
                WHERE WorkspaceId = $workspaceId AND ProjectId = $projectId AND FilePath = $filePath;

                DELETE FROM CodeSourceManifest
                WHERE WorkspaceId = $workspaceId AND ProjectId = $projectId AND FilePath = $filePath;
                """,
                cancellationToken,
                ("$workspaceId", workspaceId),
                ("$projectId", projectId),
                ("$filePath", removedPath)).ConfigureAwait(false);

            affected++;
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return affected;
    }

    /// <inheritdoc />
    public async Task<bool> SaveMaintenanceLedgerAsync(
        string workspaceId,
        string projectId,
        CodeSourceMaintenanceLedgerState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentNullException.ThrowIfNull(state);

        if (!string.Equals(state.WorkspaceId, workspaceId, StringComparison.Ordinal)
            || !string.Equals(state.ScopeId, projectId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Maintenance ledger scope does not match the requested workspace/project.");
        }

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // 单调性守卫：账本只前进。迟到的写入（旧世代，或同世代的旧期望版本）被拒绝，存储保持原值。
        var stored = await ReadLedgerVersionsAsync(connection, transaction, workspaceId, projectId, cancellationToken)
            .ConfigureAwait(false);

        if (stored is { } current)
        {
            if (current.Epoch > state.Epoch)
                return false;

            if (current.Epoch == state.Epoch && state.DesiredVersion < current.DesiredVersion)
                return false;
        }

        await ExecuteNonQueryCountAsync(connection, transaction, """
            INSERT INTO CodeIndexMaintenanceLedger (
                WorkspaceId, ProjectId, Epoch, DesiredVersion, CommittedVersion, ScanWatermarkUtc, DirtyAgain, UpdatedAtUtc)
            VALUES ($workspaceId, $projectId, $epoch, $desiredVersion, $committedVersion, $scanWatermarkUtc, $dirtyAgain, $updatedAtUtc)
            ON CONFLICT(WorkspaceId, ProjectId) DO UPDATE SET
                Epoch = excluded.Epoch,
                DesiredVersion = excluded.DesiredVersion,
                CommittedVersion = excluded.CommittedVersion,
                ScanWatermarkUtc = excluded.ScanWatermarkUtc,
                DirtyAgain = excluded.DirtyAgain,
                UpdatedAtUtc = excluded.UpdatedAtUtc;
            """,
            cancellationToken,
            ("$workspaceId", workspaceId),
            ("$projectId", projectId),
            ("$epoch", state.Epoch),
            ("$desiredVersion", state.DesiredVersion),
            ("$committedVersion", state.CommittedVersion),
            ("$scanWatermarkUtc", FormatDate(state.ScanWatermarkUtc)),
            ("$dirtyAgain", state.DirtyAgain ? 1 : 0),
            ("$updatedAtUtc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture))).ConfigureAwait(false);

        // 消费者水位与待重试集合都整体替换：一次保存就是一次完整快照，不留上一次的残留行。
        await ExecuteNonQueryCountAsync(connection, transaction, """
            DELETE FROM CodeIndexMaintenanceConsumerWatermarks
            WHERE WorkspaceId = $workspaceId AND ProjectId = $projectId;

            DELETE FROM CodeIndexMaintenanceRetries
            WHERE WorkspaceId = $workspaceId AND ProjectId = $projectId;
            """,
            cancellationToken,
            ("$workspaceId", workspaceId),
            ("$projectId", projectId)).ConfigureAwait(false);

        foreach (var (providerId, appliedVersion) in state.ConsumerAppliedVersions
                     ?? new Dictionary<string, long>())
        {
            if (string.IsNullOrWhiteSpace(providerId))
                continue;

            await ExecuteNonQueryCountAsync(connection, transaction, """
                INSERT INTO CodeIndexMaintenanceConsumerWatermarks (
                    WorkspaceId, ProjectId, ProviderId, AppliedVersion)
                VALUES ($workspaceId, $projectId, $providerId, $appliedVersion);
                """,
                cancellationToken,
                ("$workspaceId", workspaceId),
                ("$projectId", projectId),
                ("$providerId", providerId),
                ("$appliedVersion", appliedVersion)).ConfigureAwait(false);
        }

        foreach (var (filePath, retry) in state.PendingRetries ?? new Dictionary<string, CodeSourceRetry>())
        {
            if (string.IsNullOrWhiteSpace(filePath) || retry is null)
                continue;

            await ExecuteNonQueryCountAsync(connection, transaction, """
                INSERT INTO CodeIndexMaintenanceRetries (
                    WorkspaceId, ProjectId, FilePath, Reason, Attempts, FirstFailedAtUtc, NextAttemptAtUtc)
                VALUES ($workspaceId, $projectId, $filePath, $reason, $attempts, $firstFailedAtUtc, $nextAttemptAtUtc);
                """,
                cancellationToken,
                ("$workspaceId", workspaceId),
                ("$projectId", projectId),
                ("$filePath", filePath),
                ("$reason", retry.Reason ?? string.Empty),
                ("$attempts", retry.Attempts),
                ("$firstFailedAtUtc", retry.FirstFailedAtUtc.ToString("O", CultureInfo.InvariantCulture)),
                ("$nextAttemptAtUtc", retry.NextAttemptAtUtc.ToString("O", CultureInfo.InvariantCulture))).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static async Task<Dictionary<string, CodeSourceEntry>> LoadManifestAsync(
        SqliteConnection connection,
        string workspaceId,
        string projectId,
        CancellationToken cancellationToken)
    {
        var fingerprints = new Dictionary<string, (DateTimeOffset? LastWriteTimeUtc, long? Length, string? ContentHash, bool Complete)>(
            CodePathIdentity.PathComparer);
        var order = new List<string>();

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT FilePath, LastWriteTimeUtc, Length, ContentHash, Complete
                FROM CodeSourceManifest
                WHERE WorkspaceId = $workspaceId AND ProjectId = $projectId;
                """;
            command.Parameters.AddWithValue("$workspaceId", workspaceId);
            command.Parameters.AddWithValue("$projectId", projectId);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var filePath = reader.GetString(0);
                fingerprints[filePath] = (
                    ReadDate(reader, 1),
                    reader.IsDBNull(2) ? null : reader.GetInt64(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.GetInt32(4) != 0);
                order.Add(filePath);
            }
        }

        var applied = new Dictionary<string, List<AppliedFileVersion>>(CodePathIdentity.PathComparer);

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT FilePath, ProviderId, ParserPolicyFingerprint, SemanticInputFingerprint, AppliedVersion
                FROM CodeSourceAppliedVersions
                WHERE WorkspaceId = $workspaceId AND ProjectId = $projectId;
                """;
            command.Parameters.AddWithValue("$workspaceId", workspaceId);
            command.Parameters.AddWithValue("$projectId", projectId);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var filePath = reader.GetString(0);
                if (!applied.TryGetValue(filePath, out var list))
                {
                    list = [];
                    applied[filePath] = list;
                }

                list.Add(new AppliedFileVersion(
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetInt64(4)));
            }
        }

        var manifest = new Dictionary<string, CodeSourceEntry>(CodePathIdentity.PathComparer);

        foreach (var filePath in order)
        {
            var (lastWriteTimeUtc, length, contentHash, complete) = fingerprints[filePath];

            SourceFingerprint? fingerprint = lastWriteTimeUtc is { } written && length is { } size
                ? new SourceFingerprint(written, size, contentHash ?? string.Empty)
                : null;

            manifest[filePath] = new CodeSourceEntry(
                filePath,
                fingerprint,
                applied.TryGetValue(filePath, out var versions) ? versions : [],
                complete);
        }

        return manifest;
    }

    private static async Task<CodeSourceMaintenanceLedgerState> LoadLedgerAsync(
        SqliteConnection connection,
        string workspaceId,
        string projectId,
        CancellationToken cancellationToken)
    {
        long epoch = 0;
        long desiredVersion = 0;
        long committedVersion = 0;
        DateTimeOffset? scanWatermarkUtc = null;
        var dirtyAgain = false;

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT Epoch, DesiredVersion, CommittedVersion, ScanWatermarkUtc, DirtyAgain
                FROM CodeIndexMaintenanceLedger
                WHERE WorkspaceId = $workspaceId AND ProjectId = $projectId;
                """;
            command.Parameters.AddWithValue("$workspaceId", workspaceId);
            command.Parameters.AddWithValue("$projectId", projectId);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                epoch = reader.GetInt64(0);
                desiredVersion = reader.GetInt64(1);
                committedVersion = reader.GetInt64(2);
                scanWatermarkUtc = ReadDate(reader, 3);
                dirtyAgain = reader.GetInt32(4) != 0;
            }
        }

        var consumerWatermarks = new Dictionary<string, long>(StringComparer.Ordinal);

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT ProviderId, AppliedVersion
                FROM CodeIndexMaintenanceConsumerWatermarks
                WHERE WorkspaceId = $workspaceId AND ProjectId = $projectId;
                """;
            command.Parameters.AddWithValue("$workspaceId", workspaceId);
            command.Parameters.AddWithValue("$projectId", projectId);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                consumerWatermarks[reader.GetString(0)] = reader.GetInt64(1);
        }

        var retries = new Dictionary<string, CodeSourceRetry>(CodePathIdentity.PathComparer);

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT FilePath, Reason, Attempts, FirstFailedAtUtc, NextAttemptAtUtc
                FROM CodeIndexMaintenanceRetries
                WHERE WorkspaceId = $workspaceId AND ProjectId = $projectId;
                """;
            command.Parameters.AddWithValue("$workspaceId", workspaceId);
            command.Parameters.AddWithValue("$projectId", projectId);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var filePath = reader.GetString(0);
                retries[filePath] = new CodeSourceRetry(
                    filePath,
                    reader.GetString(1),
                    reader.GetInt32(2),
                    ReadDate(reader, 3) ?? DateTimeOffset.MinValue,
                    ReadDate(reader, 4) ?? DateTimeOffset.MinValue);
            }
        }

        return new CodeSourceMaintenanceLedgerState(
            workspaceId,
            projectId,
            epoch,
            desiredVersion,
            committedVersion,
            consumerWatermarks,
            retries,
            scanWatermarkUtc,
            dirtyAgain);
    }

    /// <summary>读账本的单调守卫字段（世代与期望版本）；没有记录时返回 null。</summary>
    private static async Task<(long Epoch, long DesiredVersion)?> ReadLedgerVersionsAsync(
        SqliteConnection connection,
        DbTransaction transaction,
        string workspaceId,
        string projectId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            SELECT Epoch, DesiredVersion
            FROM CodeIndexMaintenanceLedger
            WHERE WorkspaceId = $workspaceId AND ProjectId = $projectId;
            """;
        command.Parameters.AddWithValue("$workspaceId", workspaceId);
        command.Parameters.AddWithValue("$projectId", projectId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;

        return (reader.GetInt64(0), reader.GetInt64(1));
    }
}
