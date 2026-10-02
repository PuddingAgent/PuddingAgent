using Microsoft.Data.Sqlite;
using PuddingCodeIndex.Contracts;
using PuddingCodeIndex.Services;
using System.Data.Common;
using System.Globalization;

namespace PuddingCodeIndex.Storage;

/// <summary>
/// 原子文件替换（D4，2026-10-02）：一个批次的索引结果与源指纹在**同一个事务**里提交。
/// <para>
/// 所有权与依赖的规则（容易写错，故写在这里）：
/// <list type="bullet">
///   <item><description><b>出边/引用按所有权重建</b>：所有权 = 行的 <c>SourceFilePath</c> 属于该文件，
///     或行的来源符号属于该文件的旧符号集。</description></item>
///   <item><description><b>入边按目标存亡决定</b>：指向该文件**仍然存在**的符号的入边必须保留
///     （它们属于别的文件，本文件无权删除）；**只有**指向消失符号的入边才删除，并把那些来源文件
///     报告为需要重新绑定。</description></item>
///   <item><description>因此「删掉所有入边、只写回本文件出边」是**错误**实现：那会静默打断跨文件引用。</description></item>
/// </list>
/// </para>
/// <para>
/// SQLite 的参数上限用分块规避：<c>IN (…)</c> 列表每块不超过
/// <see cref="SymbolIdChunkSize"/> 个参数。
/// </para>
/// </summary>
public sealed partial class SqliteCodeIndexStore
{
    /// <summary>单个 <c>IN (…)</c> 语句里符号 id 的块大小（避开 SQLite 参数上限）。</summary>
    internal const int SymbolIdChunkSize = 128;

    /// <inheritdoc />
    public async Task<CodeSourceFileReplacementResult> ReplaceFilesAsync(
        string workspaceId,
        string projectId,
        IReadOnlyCollection<CodeSourceFileReplacement> replacements,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentNullException.ThrowIfNull(replacements);

        if (replacements.Count == 0)
            return new CodeSourceFileReplacementResult(0, [], []);

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        // 先做全部校验（在打开事务之前）：路径与 scope 不一致是调用方错误，不能留下半个批次。
        var batch = new List<(CodeSourceFileReplacement Replacement, string FilePath)>(replacements.Count);
        var batchPaths = new HashSet<string>(CodePathIdentity.PathComparer);

        foreach (var replacement in replacements)
        {
            if (replacement is null || replacement.File is null || replacement.Source is null)
                throw new ArgumentException("A replacement requires both a file record and its source entry.", nameof(replacements));

            var filePath = replacement.File.FilePath;
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("A replacement requires a non-empty file path.", nameof(replacements));

            if (!string.Equals(filePath, replacement.Source.FilePath, CodePathIdentity.PathComparison))
            {
                throw new InvalidOperationException(
                    "The replacement's source entry must describe the same file as its file record.");
            }

            ValidateScope(workspaceId, projectId, replacement.File.WorkspaceId, replacement.File.ProjectId);

            foreach (var symbol in replacement.Symbols ?? [])
                ValidateScope(workspaceId, projectId, symbol.WorkspaceId, symbol.ProjectId);
            foreach (var reference in replacement.References ?? [])
                ValidateScope(workspaceId, projectId, reference.WorkspaceId, reference.ProjectId);
            foreach (var relation in replacement.Relations ?? [])
                ValidateScope(workspaceId, projectId, relation.WorkspaceId, relation.ProjectId);

            if (!batchPaths.Add(filePath))
                throw new InvalidOperationException($"The batch replaces the same file twice: {filePath}");

            batch.Add((replacement, filePath));
        }

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var removedSymbolIds = new List<string>();
        var invalidatedDependents = new HashSet<string>(CodePathIdentity.PathComparer);

        foreach (var (replacement, filePath) in batch)
        {
            var oldSymbolIds = await ReadSymbolIdsForFileAsync(connection, transaction, workspaceId, projectId, filePath, cancellationToken)
                .ConfigureAwait(false);

            var newSymbolIds = (replacement.Symbols ?? [])
                .Select(symbol => symbol.SymbolId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToHashSet(StringComparer.Ordinal);

            var vanishedSymbolIds = oldSymbolIds
                .Where(id => !newSymbolIds.Contains(id))
                .ToArray();

            if (vanishedSymbolIds.Length > 0)
            {
                removedSymbolIds.AddRange(vanishedSymbolIds);

                // 先记录依赖方（它们失去了入边），再删除；本批次内的文件正在被重建，不算「失效依赖」。
                foreach (var chunk in Chunk(vanishedSymbolIds))
                {
                    foreach (var dependentPath in await ReadDependentSourcePathsAsync(
                                 connection, transaction, workspaceId, projectId, chunk, filePath, cancellationToken)
                             .ConfigureAwait(false))
                    {
                        invalidatedDependents.Add(dependentPath);
                    }

                    await DeleteIncomingRowsForSymbolsAsync(
                        connection, transaction, workspaceId, projectId, chunk, filePath, cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            // 本文件拥有的行全量重建（所有权：SourceFilePath 属于本文件，或来源符号属于本文件的旧符号集）。
            foreach (var chunk in Chunk(oldSymbolIds))
            {
                await ExecuteNonQueryCountAsync(connection, transaction, """
                    DELETE FROM CodeReferences
                    WHERE WorkspaceId = $workspaceId AND ProjectId = $projectId
                      AND SourceSymbolId IN (SELECT value FROM json_each($symbolIds));
                    DELETE FROM CodeRelations
                    WHERE WorkspaceId = $workspaceId AND ProjectId = $projectId
                      AND SourceSymbolId IN (SELECT value FROM json_each($symbolIds));
                    """,
                    cancellationToken,
                    ("$workspaceId", workspaceId),
                    ("$projectId", projectId),
                    ("$symbolIds", ToJsonArray(chunk))).ConfigureAwait(false);
            }

            await ExecuteNonQueryCountAsync(connection, transaction, """
                DELETE FROM CodeReferences
                WHERE WorkspaceId = $workspaceId AND ProjectId = $projectId AND SourceFilePath = $filePath;
                DELETE FROM CodeRelations
                WHERE WorkspaceId = $workspaceId AND ProjectId = $projectId AND SourceFilePath = $filePath;
                DELETE FROM CodeSymbols
                WHERE WorkspaceId = $workspaceId AND ProjectId = $projectId AND FilePath = $filePath;
                """,
                cancellationToken,
                ("$workspaceId", workspaceId),
                ("$projectId", projectId),
                ("$filePath", filePath)).ConfigureAwait(false);

            await WriteFileRowAsync(connection, transaction, replacement.File, cancellationToken).ConfigureAwait(false);
            await WriteSymbolsAsync(connection, transaction, replacement.Symbols, cancellationToken).ConfigureAwait(false);
            await WriteReferencesAsync(connection, transaction, replacement.References, cancellationToken).ConfigureAwait(false);
            await WriteRelationsAsync(connection, transaction, replacement.Relations, cancellationToken).ConfigureAwait(false);

            // manifest 行与该文件各消费者水位：与索引结果同一事务，失败一起回滚。
            await WriteSourceManifestRowAsync(
                connection, transaction, workspaceId, projectId, replacement.Source, cancellationToken)
                .ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        foreach (var path in batchPaths)
            invalidatedDependents.Remove(path);

        return new CodeSourceFileReplacementResult(
            batch.Count,
            invalidatedDependents.OrderBy(path => path, CodePathIdentity.PathComparer).ToArray(),
            removedSymbolIds.Distinct(StringComparer.Ordinal).ToArray());
    }

    private static async Task<List<string>> ReadSymbolIdsForFileAsync(
        SqliteConnection connection,
        DbTransaction transaction,
        string workspaceId,
        string projectId,
        string filePath,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            SELECT SymbolId
            FROM CodeSymbols
            WHERE WorkspaceId = $workspaceId AND ProjectId = $projectId AND FilePath = $filePath;
            """;
        command.Parameters.AddWithValue("$workspaceId", workspaceId);
        command.Parameters.AddWithValue("$projectId", projectId);
        command.Parameters.AddWithValue("$filePath", filePath);

        var symbolIds = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            symbolIds.Add(reader.GetString(0));

        return symbolIds;
    }

    /// <summary>其他文件指向这些符号的入边来源（本文件自己的行不算依赖方）。</summary>
    private static async Task<List<string>> ReadDependentSourcePathsAsync(
        SqliteConnection connection,
        DbTransaction transaction,
        string workspaceId,
        string projectId,
        IReadOnlyList<string> symbolIds,
        string ownerFilePath,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            SELECT DISTINCT SourceFilePath FROM CodeReferences
            WHERE WorkspaceId = $workspaceId AND ProjectId = $projectId
              AND TargetSymbolId IN (SELECT value FROM json_each($symbolIds))
              AND SourceFilePath IS NOT NULL AND SourceFilePath <> $ownerFilePath
            UNION
            SELECT DISTINCT SourceFilePath FROM CodeRelations
            WHERE WorkspaceId = $workspaceId AND ProjectId = $projectId
              AND TargetSymbolId IN (SELECT value FROM json_each($symbolIds))
              AND SourceFilePath IS NOT NULL AND SourceFilePath <> $ownerFilePath;
            """;
        command.Parameters.AddWithValue("$workspaceId", workspaceId);
        command.Parameters.AddWithValue("$projectId", projectId);
        command.Parameters.AddWithValue("$symbolIds", ToJsonArray(symbolIds));
        command.Parameters.AddWithValue("$ownerFilePath", ownerFilePath);

        var paths = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!reader.IsDBNull(0))
                paths.Add(reader.GetString(0));
        }

        return paths;
    }

    /// <summary>删除其他文件指向消失符号的入边（本文件拥有的行由所有权删除负责）。</summary>
    private static Task DeleteIncomingRowsForSymbolsAsync(
        SqliteConnection connection,
        DbTransaction transaction,
        string workspaceId,
        string projectId,
        IReadOnlyList<string> vanishedSymbolIds,
        string ownerFilePath,
        CancellationToken cancellationToken) =>
        ExecuteNonQueryCountAsync(connection, transaction, """
            DELETE FROM CodeReferences
            WHERE WorkspaceId = $workspaceId AND ProjectId = $projectId
              AND TargetSymbolId IN (SELECT value FROM json_each($symbolIds))
              AND (SourceFilePath IS NULL OR SourceFilePath <> $ownerFilePath);
            DELETE FROM CodeRelations
            WHERE WorkspaceId = $workspaceId AND ProjectId = $projectId
              AND TargetSymbolId IN (SELECT value FROM json_each($symbolIds))
              AND (SourceFilePath IS NULL OR SourceFilePath <> $ownerFilePath);
            """,
            cancellationToken,
            ("$workspaceId", workspaceId),
            ("$projectId", projectId),
            ("$symbolIds", ToJsonArray(vanishedSymbolIds)),
            ("$ownerFilePath", ownerFilePath));

    private static async Task WriteFileRowAsync(
        SqliteConnection connection,
        DbTransaction transaction,
        CodeFileRecord file,
        CancellationToken cancellationToken) =>
        await ExecuteNonQueryCountAsync(connection, transaction, """
            INSERT INTO CodeFiles (WorkspaceId, ProjectId, FilePath, Language, LastIndexedAtUtc)
            VALUES ($workspaceId, $projectId, $filePath, $language, $lastIndexedAtUtc)
            ON CONFLICT(WorkspaceId, ProjectId, FilePath) DO UPDATE SET
                Language = excluded.Language,
                LastIndexedAtUtc = excluded.LastIndexedAtUtc;
            """,
            cancellationToken,
            ("$workspaceId", file.WorkspaceId),
            ("$projectId", file.ProjectId),
            ("$filePath", file.FilePath),
            ("$language", (object?)file.Language ?? DBNull.Value),
            ("$lastIndexedAtUtc", FormatDate(file.LastIndexedAtUtc))).ConfigureAwait(false);

    private static async Task WriteSymbolsAsync(
        SqliteConnection connection,
        DbTransaction transaction,
        IReadOnlyList<CodeSymbolRecord>? symbols,
        CancellationToken cancellationToken)
    {
        foreach (var symbol in symbols ?? [])
        {
            await ExecuteNonQueryCountAsync(connection, transaction, """
                INSERT INTO CodeSymbols (
                    WorkspaceId, ProjectId, FilePath, SymbolId, Name, Kind, StartLine, EndLine, Signature, Container)
                VALUES (
                    $workspaceId, $projectId, $filePath, $symbolId, $name, $kind, $startLine, $endLine, $signature, $container)
                ON CONFLICT(WorkspaceId, ProjectId, SymbolId) DO UPDATE SET
                    FilePath = excluded.FilePath,
                    Name = excluded.Name,
                    Kind = excluded.Kind,
                    StartLine = excluded.StartLine,
                    EndLine = excluded.EndLine,
                    Signature = excluded.Signature,
                    Container = excluded.Container;
                """,
                cancellationToken,
                ("$workspaceId", symbol.WorkspaceId),
                ("$projectId", symbol.ProjectId),
                ("$filePath", symbol.FilePath),
                ("$symbolId", symbol.SymbolId),
                ("$name", symbol.Name),
                ("$kind", symbol.Kind.ToString()),
                ("$startLine", symbol.StartLine),
                ("$endLine", symbol.EndLine),
                ("$signature", (object?)symbol.Signature ?? DBNull.Value),
                ("$container", (object?)symbol.Container ?? DBNull.Value)).ConfigureAwait(false);
        }
    }

    private static async Task WriteReferencesAsync(
        SqliteConnection connection,
        DbTransaction transaction,
        IReadOnlyList<CodeReferenceRecord>? references,
        CancellationToken cancellationToken)
    {
        foreach (var reference in references ?? [])
        {
            await ExecuteNonQueryCountAsync(connection, transaction, """
                INSERT OR REPLACE INTO CodeReferences (
                    WorkspaceId, ProjectId, SourceSymbolId, TargetSymbolId, SourceFilePath, SourceLine, SourceText, ObservedAtUtc)
                VALUES (
                    $workspaceId, $projectId, $sourceSymbolId, $targetSymbolId, $sourceFilePath, $sourceLine, $sourceText, $observedAtUtc);
                """,
                cancellationToken,
                ("$workspaceId", reference.WorkspaceId),
                ("$projectId", reference.ProjectId),
                ("$sourceSymbolId", reference.SourceSymbolId),
                ("$targetSymbolId", reference.TargetSymbolId),
                ("$sourceFilePath", reference.SourceFilePath),
                ("$sourceLine", reference.SourceLine),
                ("$sourceText", (object?)reference.SourceText ?? DBNull.Value),
                ("$observedAtUtc", FormatDate(reference.ObservedAtUtc))).ConfigureAwait(false);
        }
    }

    private static async Task WriteRelationsAsync(
        SqliteConnection connection,
        DbTransaction transaction,
        IReadOnlyList<CodeRelationRecord>? relations,
        CancellationToken cancellationToken)
    {
        foreach (var relation in relations ?? [])
        {
            await ExecuteNonQueryCountAsync(connection, transaction, """
                INSERT OR REPLACE INTO CodeRelations (
                    WorkspaceId, ProjectId, SourceSymbolId, TargetSymbolId, Kind, SourceLine, SourceFilePath, CreatedAtUtc)
                VALUES (
                    $workspaceId, $projectId, $sourceSymbolId, $targetSymbolId, $kind, $sourceLine, $sourceFilePath, $createdAtUtc);
                """,
                cancellationToken,
                ("$workspaceId", relation.WorkspaceId),
                ("$projectId", relation.ProjectId),
                ("$sourceSymbolId", relation.SourceSymbolId),
                ("$targetSymbolId", relation.TargetSymbolId),
                ("$kind", relation.Kind.ToString()),
                ("$sourceLine", relation.SourceLine),
                ("$sourceFilePath", (object?)relation.SourceFilePath ?? DBNull.Value),
                ("$createdAtUtc", FormatDate(relation.CreatedAtUtc))).ConfigureAwait(false);
        }
    }

    private static async Task WriteSourceManifestRowAsync(
        SqliteConnection connection,
        DbTransaction transaction,
        string workspaceId,
        string projectId,
        CodeSourceEntry source,
        CancellationToken cancellationToken)
    {
        await ExecuteNonQueryCountAsync(connection, transaction, """
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
            ("$filePath", source.FilePath),
            ("$lastWriteTimeUtc", FormatDate(source.Fingerprint?.LastWriteTimeUtc)),
            ("$length", source.Fingerprint?.Length),
            ("$contentHash", source.Fingerprint?.ContentHash),
            ("$complete", source.Complete ? 1 : 0),
            ("$updatedAtUtc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture))).ConfigureAwait(false);

        await ExecuteNonQueryCountAsync(connection, transaction, """
            DELETE FROM CodeSourceAppliedVersions
            WHERE WorkspaceId = $workspaceId AND ProjectId = $projectId AND FilePath = $filePath;
            """,
            cancellationToken,
            ("$workspaceId", workspaceId),
            ("$projectId", projectId),
            ("$filePath", source.FilePath)).ConfigureAwait(false);

        foreach (var applied in source.AppliedVersions ?? [])
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
                ("$filePath", source.FilePath),
                ("$providerId", applied.ProviderId),
                ("$policy", applied.ParserPolicyFingerprint),
                ("$semantic", applied.SemanticInputFingerprint),
                ("$appliedVersion", applied.AppliedVersion)).ConfigureAwait(false);
        }
    }

    /// <summary>把 id 列表分块（避开 SQLite 的参数上限）。</summary>
    private static IEnumerable<IReadOnlyList<string>> Chunk(IReadOnlyList<string> values)
    {
        for (var offset = 0; offset < values.Count; offset += SymbolIdChunkSize)
        {
            var take = Math.Min(SymbolIdChunkSize, values.Count - offset);
            var chunk = new string[take];
            for (var index = 0; index < take; index++)
                chunk[index] = values[offset + index];

            yield return chunk;
        }
    }

    /// <summary>把 id 列表编码成 JSON 数组参数（用 <c>json_each</c> 展开，避免拼接 SQL）。</summary>
    private static string ToJsonArray(IReadOnlyList<string> values) =>
        System.Text.Json.JsonSerializer.Serialize(values);
}
