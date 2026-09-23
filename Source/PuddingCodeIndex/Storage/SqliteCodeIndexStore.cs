using Microsoft.Data.Sqlite;
using PuddingCodeIntelligence.Contracts;
using System.Data.Common;
using System.Globalization;

namespace PuddingCodeIntelligence.Storage;

/// <summary>
/// SQLite-backed store for workspace-registered code projects and their semantic graph.
/// </summary>
public sealed class SqliteCodeIndexStore : ICodeIndexStore
{
    private readonly string _databasePath;
    private bool _initialized;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    public SqliteCodeIndexStore(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
            throw new ArgumentException("Database path is required.", nameof(databasePath));

        _databasePath = Path.GetFullPath(databasePath);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
            return;

        await _initLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
                return;

            var directory = Path.GetDirectoryName(_databasePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await ExecuteNonQueryAsync(connection, """
                PRAGMA journal_mode = WAL;
                PRAGMA foreign_keys = ON;

                CREATE TABLE IF NOT EXISTS CodeProjects (
                    WorkspaceId TEXT NOT NULL,
                    ProjectId TEXT NOT NULL,
                    ProjectPath TEXT NOT NULL,
                    Status TEXT NOT NULL,
                    DisplayName TEXT NULL,
                    AddedAtUtc TEXT NULL,
                    UpdatedAtUtc TEXT NULL,
                    StatusMessage TEXT NULL,
                    Source TEXT NULL,
                    ScopeState TEXT NULL,
                    PRIMARY KEY (WorkspaceId, ProjectId)
                );

                CREATE TABLE IF NOT EXISTS CodeFiles (
                    WorkspaceId TEXT NOT NULL,
                    ProjectId TEXT NOT NULL,
                    FilePath TEXT NOT NULL,
                    Language TEXT NULL,
                    LastIndexedAtUtc TEXT NULL,
                    PRIMARY KEY (WorkspaceId, ProjectId, FilePath)
                );

                CREATE TABLE IF NOT EXISTS CodeSymbols (
                    WorkspaceId TEXT NOT NULL,
                    ProjectId TEXT NOT NULL,
                    FilePath TEXT NOT NULL,
                    SymbolId TEXT NOT NULL,
                    Name TEXT NOT NULL,
                    Kind TEXT NOT NULL,
                    StartLine INTEGER NOT NULL,
                    EndLine INTEGER NOT NULL,
                    Signature TEXT NULL,
                    Container TEXT NULL,
                    PRIMARY KEY (WorkspaceId, ProjectId, SymbolId)
                );

                CREATE TABLE IF NOT EXISTS CodeRelations (
                    WorkspaceId TEXT NOT NULL,
                    ProjectId TEXT NOT NULL,
                    SourceSymbolId TEXT NOT NULL,
                    TargetSymbolId TEXT NOT NULL,
                    Kind TEXT NOT NULL,
                    SourceLine INTEGER NULL,
                    SourceFilePath TEXT NULL,
                    CreatedAtUtc TEXT NULL,
                    PRIMARY KEY (WorkspaceId, ProjectId, SourceSymbolId, TargetSymbolId, Kind, SourceLine, SourceFilePath)
                );

                CREATE TABLE IF NOT EXISTS CodeReferences (
                    WorkspaceId TEXT NOT NULL,
                    ProjectId TEXT NOT NULL,
                    SourceSymbolId TEXT NOT NULL,
                    TargetSymbolId TEXT NOT NULL,
                    SourceFilePath TEXT NOT NULL,
                    SourceLine INTEGER NOT NULL,
                    SourceText TEXT NULL,
                    ObservedAtUtc TEXT NULL,
                    PRIMARY KEY (WorkspaceId, ProjectId, SourceSymbolId, TargetSymbolId, SourceFilePath, SourceLine)
                );

                CREATE TABLE IF NOT EXISTS CodeIndexRuns (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    WorkspaceId TEXT NOT NULL,
                    ProjectId TEXT NOT NULL,
                    Status TEXT NOT NULL,
                    Message TEXT NULL,
                    StartedAtUtc TEXT NOT NULL,
                    CompletedAtUtc TEXT NULL
                );

                CREATE INDEX IF NOT EXISTS IX_CodeProjects_Workspace_Status
                    ON CodeProjects (WorkspaceId, Status);
                CREATE INDEX IF NOT EXISTS IX_CodeSymbols_Workspace_Project_Name
                    ON CodeSymbols (WorkspaceId, ProjectId, Name);
                CREATE INDEX IF NOT EXISTS IX_CodeSymbols_Workspace_Name
                    ON CodeSymbols (WorkspaceId, Name);
                CREATE INDEX IF NOT EXISTS IX_CodeSymbols_File
                    ON CodeSymbols (WorkspaceId, ProjectId, FilePath);
                CREATE INDEX IF NOT EXISTS IX_CodeRelations_Source
                    ON CodeRelations (WorkspaceId, ProjectId, SourceSymbolId, Kind);
                CREATE INDEX IF NOT EXISTS IX_CodeRelations_Target
                    ON CodeRelations (WorkspaceId, ProjectId, TargetSymbolId, Kind);
                CREATE INDEX IF NOT EXISTS IX_CodeReferences_Source
                    ON CodeReferences (WorkspaceId, ProjectId, SourceSymbolId);
                CREATE INDEX IF NOT EXISTS IX_CodeReferences_Target
                    ON CodeReferences (WorkspaceId, ProjectId, TargetSymbolId);
                """, cancellationToken).ConfigureAwait(false);

            // Migration: add Source/ScopeState columns for existing databases.
            try
            {
                await ExecuteNonQueryAsync(connection,
                    "ALTER TABLE CodeProjects ADD COLUMN Source TEXT NULL;",
                    cancellationToken).ConfigureAwait(false);
            }
            catch (SqliteException) { /* column already exists */ }
            try
            {
                await ExecuteNonQueryAsync(connection,
                    "ALTER TABLE CodeProjects ADD COLUMN ScopeState TEXT NULL;",
                    cancellationToken).ConfigureAwait(false);
            }
            catch (SqliteException) { /* column already exists */ }

            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task UpsertProjectAsync(
        CodeProjectRecord project,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO CodeProjects (
                WorkspaceId,
                ProjectId,
                ProjectPath,
                Status,
                DisplayName,
                AddedAtUtc,
                UpdatedAtUtc,
                StatusMessage,
                Source,
                ScopeState)
            VALUES (
                $workspaceId,
                $projectId,
                $projectPath,
                $status,
                $displayName,
                $addedAtUtc,
                $updatedAtUtc,
                NULL,
                $source,
                $scopeState)
            ON CONFLICT(WorkspaceId, ProjectId) DO UPDATE SET
                ProjectPath = excluded.ProjectPath,
                Status = excluded.Status,
                DisplayName = excluded.DisplayName,
                AddedAtUtc = COALESCE(CodeProjects.AddedAtUtc, excluded.AddedAtUtc),
                UpdatedAtUtc = excluded.UpdatedAtUtc,
                StatusMessage = NULL,
                Source = excluded.Source,
                ScopeState = excluded.ScopeState;
            """;
        AddProjectParameters(command, project);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveProjectAsync(
        string workspaceId,
        string projectId,
        bool removeIndexedArtifacts = true,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        if (removeIndexedArtifacts)
        {
            await ExecuteNonQueryAsync(connection, transaction, """
                DELETE FROM CodeReferences WHERE WorkspaceId = $workspaceId AND ProjectId = $projectId;
                DELETE FROM CodeRelations WHERE WorkspaceId = $workspaceId AND ProjectId = $projectId;
                DELETE FROM CodeSymbols WHERE WorkspaceId = $workspaceId AND ProjectId = $projectId;
                DELETE FROM CodeFiles WHERE WorkspaceId = $workspaceId AND ProjectId = $projectId;
                DELETE FROM CodeIndexRuns WHERE WorkspaceId = $workspaceId AND ProjectId = $projectId;
                DELETE FROM CodeProjects WHERE WorkspaceId = $workspaceId AND ProjectId = $projectId;
                """,
                cancellationToken,
                ("$workspaceId", workspaceId),
                ("$projectId", projectId)).ConfigureAwait(false);
        }
        else
        {
            await ExecuteNonQueryAsync(connection, transaction, """
                UPDATE CodeProjects
                SET Status = $status,
                    UpdatedAtUtc = $updatedAtUtc
                WHERE WorkspaceId = $workspaceId AND ProjectId = $projectId;
                """,
                cancellationToken,
                ("$workspaceId", workspaceId),
                ("$projectId", projectId),
                ("$status", CodeProjectStatus.Removed.ToString()),
                ("$updatedAtUtc", DateTimeOffset.UtcNow.ToString("O"))).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CodeProjectRecord>> ListProjectsAsync(
        string workspaceId,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        var results = new List<CodeProjectRecord>();
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT WorkspaceId, ProjectId, ProjectPath, Status, DisplayName, AddedAtUtc, UpdatedAtUtc, NULL, Source, ScopeState
            FROM CodeProjects
            WHERE WorkspaceId = $workspaceId AND Status <> $removed
            ORDER BY COALESCE(DisplayName, ProjectId), ProjectId;
            """;
        command.Parameters.AddWithValue("$workspaceId", workspaceId);
        command.Parameters.AddWithValue("$removed", CodeProjectStatus.Removed.ToString());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            results.Add(ReadProject(reader));

        return results;
    }

    public async Task<CodeProjectRecord?> GetProjectAsync(
        string workspaceId,
        string projectId,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT WorkspaceId, ProjectId, ProjectPath, Status, DisplayName, AddedAtUtc, UpdatedAtUtc, NULL, Source, ScopeState
            FROM CodeProjects
            WHERE WorkspaceId = $workspaceId AND ProjectId = $projectId;
            """;
        command.Parameters.AddWithValue("$workspaceId", workspaceId);
        command.Parameters.AddWithValue("$projectId", projectId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadProject(reader)
            : null;
    }

    public async Task UpdateProjectStatusAsync(
        string workspaceId,
        string projectId,
        CodeProjectStatus status,
        string? statusMessage = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection, """
            UPDATE CodeProjects
            SET Status = $status,
                StatusMessage = $statusMessage,
                UpdatedAtUtc = $updatedAtUtc
            WHERE WorkspaceId = $workspaceId AND ProjectId = $projectId;
            """,
            cancellationToken,
            ("$workspaceId", workspaceId),
            ("$projectId", projectId),
            ("$status", status.ToString()),
            ("$statusMessage", (object?)statusMessage ?? DBNull.Value),
            ("$updatedAtUtc", DateTimeOffset.UtcNow.ToString("O"))).ConfigureAwait(false);
    }

    public async Task UpsertFilesAsync(
        string workspaceId,
        string projectId,
        IReadOnlyCollection<CodeFileRecord> files,
        CancellationToken cancellationToken = default)
    {
        if (files.Count == 0)
            return;

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        foreach (var file in files)
        {
            ValidateScope(workspaceId, projectId, file.WorkspaceId, file.ProjectId);
            await ExecuteNonQueryAsync(connection, transaction, """
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
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CodeFileRecord>> ListFilesAsync(
        string workspaceId,
        string projectId,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        var results = new List<CodeFileRecord>();
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT WorkspaceId, ProjectId, FilePath, Language, LastIndexedAtUtc
            FROM CodeFiles
            WHERE WorkspaceId = $workspaceId AND ProjectId = $projectId
            ORDER BY FilePath;
            """;
        command.Parameters.AddWithValue("$workspaceId", workspaceId);
        command.Parameters.AddWithValue("$projectId", projectId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            results.Add(ReadFile(reader));

        return results;
    }

    public async Task UpsertSymbolsAsync(
        string workspaceId,
        string projectId,
        IReadOnlyCollection<CodeSymbolRecord> symbols,
        CancellationToken cancellationToken = default)
    {
        if (symbols.Count == 0)
            return;

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var affectedFiles = symbols.Select(s =>
            {
                ValidateScope(workspaceId, projectId, s.WorkspaceId, s.ProjectId);
                return s.FilePath;
            })
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        foreach (var filePath in affectedFiles)
            await RemoveSymbolGraphForFileAsync(connection, transaction, workspaceId, projectId, filePath, cancellationToken)
                .ConfigureAwait(false);

        foreach (var symbol in symbols)
        {
            await ExecuteNonQueryAsync(connection, transaction, """
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

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CodeSymbolRecord>> SearchSymbolsAsync(
        CodeSymbolSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        var results = new List<CodeSymbolRecord>();
        var limit = Math.Clamp(request.Limit, 1, 500);
        var skip = Math.Max(0, request.Skip);

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT WorkspaceId, ProjectId, FilePath, SymbolId, Name, Kind, StartLine, EndLine, Signature, Container
            FROM CodeSymbols
            WHERE WorkspaceId = $workspaceId
              AND ($projectId IS NULL OR ProjectId = $projectId)
              AND ($kind IS NULL OR Kind = $kind)
              AND ($query = '' OR Name LIKE $likeQuery ESCAPE '\' OR Signature LIKE $likeQuery ESCAPE '\' OR Container LIKE $likeQuery ESCAPE '\')
            ORDER BY
              CASE WHEN Name = $query THEN 0 ELSE 1 END,
              Name,
              SymbolId
            LIMIT {limit} OFFSET {skip};
            """;
        command.Parameters.AddWithValue("$workspaceId", request.WorkspaceId);
        command.Parameters.AddWithValue("$projectId", (object?)request.ProjectId ?? DBNull.Value);
        command.Parameters.AddWithValue("$kind", request.Kind?.ToString() is { } kind ? kind : DBNull.Value);
        command.Parameters.AddWithValue("$query", request.Query ?? string.Empty);
        command.Parameters.AddWithValue("$likeQuery", $"%{EscapeLike(request.Query ?? string.Empty)}%");

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            results.Add(ReadSymbol(reader));

        return results;
    }

    public async Task<CodeSymbolRecord?> GetSymbolAsync(
        string workspaceId,
        string projectId,
        string symbolId,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT WorkspaceId, ProjectId, FilePath, SymbolId, Name, Kind, StartLine, EndLine, Signature, Container
            FROM CodeSymbols
            WHERE WorkspaceId = $workspaceId AND ProjectId = $projectId AND SymbolId = $symbolId;
            """;
        command.Parameters.AddWithValue("$workspaceId", workspaceId);
        command.Parameters.AddWithValue("$projectId", projectId);
        command.Parameters.AddWithValue("$symbolId", symbolId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadSymbol(reader)
            : null;
    }

    public async Task<IReadOnlyList<CodeSymbolRecord>> GetSymbolsByFileAsync(
        string workspaceId,
        string projectId,
        string filePath,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        var results = new List<CodeSymbolRecord>();
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT WorkspaceId, ProjectId, FilePath, SymbolId, Name, Kind, StartLine, EndLine, Signature, Container
            FROM CodeSymbols
            WHERE WorkspaceId = $workspaceId AND ProjectId = $projectId AND FilePath = $filePath
            ORDER BY StartLine, EndLine, Name;
            """;
        command.Parameters.AddWithValue("$workspaceId", workspaceId);
        command.Parameters.AddWithValue("$projectId", projectId);
        command.Parameters.AddWithValue("$filePath", filePath);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            results.Add(ReadSymbol(reader));

        return results;
    }

    public async Task ClearSymbolsForFileAsync(
        string workspaceId,
        string projectId,
        string filePath,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await RemoveSymbolGraphForFileAsync(connection, transaction, workspaceId, projectId, filePath, cancellationToken)
            .ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpsertRelationsAsync(
        string workspaceId,
        string projectId,
        IReadOnlyCollection<CodeRelationRecord> relations,
        CancellationToken cancellationToken = default)
    {
        if (relations.Count == 0)
            return;

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        foreach (var sourceSymbolId in relations.Select(r =>
                 {
                     ValidateScope(workspaceId, projectId, r.WorkspaceId, r.ProjectId);
                     return r.SourceSymbolId;
                 }).Distinct(StringComparer.Ordinal))
        {
            await ExecuteNonQueryAsync(connection, transaction, """
                DELETE FROM CodeRelations
                WHERE WorkspaceId = $workspaceId AND ProjectId = $projectId AND SourceSymbolId = $sourceSymbolId;
                """,
                cancellationToken,
                ("$workspaceId", workspaceId),
                ("$projectId", projectId),
                ("$sourceSymbolId", sourceSymbolId)).ConfigureAwait(false);
        }

        foreach (var relation in relations)
        {
            await ExecuteNonQueryAsync(connection, transaction, """
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
                ("$sourceLine", (object?)relation.SourceLine ?? DBNull.Value),
                ("$sourceFilePath", (object?)relation.SourceFilePath ?? DBNull.Value),
                ("$createdAtUtc", FormatDate(relation.CreatedAtUtc))).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<CodeRelationRecord>> ListRelationsAsync(
        string workspaceId,
        string projectId,
        string symbolId,
        CodeRelationKind? relationKind = null,
        CancellationToken cancellationToken = default) =>
        ListRelationsCoreAsync(workspaceId, projectId, "SourceSymbolId", symbolId, relationKind, cancellationToken);

    public Task<IReadOnlyList<CodeRelationRecord>> ListIncomingRelationsAsync(
        string workspaceId,
        string projectId,
        string symbolId,
        CodeRelationKind? relationKind = null,
        CancellationToken cancellationToken = default) =>
        ListRelationsCoreAsync(workspaceId, projectId, "TargetSymbolId", symbolId, relationKind, cancellationToken);

    public async Task ClearRelationsAsync(
        string workspaceId,
        string projectId,
        string sourceSymbolId,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection, """
            DELETE FROM CodeRelations
            WHERE WorkspaceId = $workspaceId AND ProjectId = $projectId AND SourceSymbolId = $sourceSymbolId;
            """,
            cancellationToken,
            ("$workspaceId", workspaceId),
            ("$projectId", projectId),
            ("$sourceSymbolId", sourceSymbolId)).ConfigureAwait(false);
    }

    public async Task UpsertReferencesAsync(
        string workspaceId,
        string projectId,
        IReadOnlyCollection<CodeReferenceRecord> references,
        CancellationToken cancellationToken = default)
    {
        if (references.Count == 0)
            return;

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        foreach (var sourceSymbolId in references.Select(r =>
                 {
                     ValidateScope(workspaceId, projectId, r.WorkspaceId, r.ProjectId);
                     return r.SourceSymbolId;
                 }).Distinct(StringComparer.Ordinal))
        {
            await ExecuteNonQueryAsync(connection, transaction, """
                DELETE FROM CodeReferences
                WHERE WorkspaceId = $workspaceId AND ProjectId = $projectId AND SourceSymbolId = $sourceSymbolId;
                """,
                cancellationToken,
                ("$workspaceId", workspaceId),
                ("$projectId", projectId),
                ("$sourceSymbolId", sourceSymbolId)).ConfigureAwait(false);
        }

        foreach (var reference in references)
        {
            await ExecuteNonQueryAsync(connection, transaction, """
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

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CodeReferenceRecord>> ListReferencesAsync(
        string workspaceId,
        string projectId,
        string symbolId,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        var results = new List<CodeReferenceRecord>();
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT WorkspaceId, ProjectId, SourceSymbolId, TargetSymbolId, SourceFilePath, SourceLine, SourceText, ObservedAtUtc
            FROM CodeReferences
            WHERE WorkspaceId = $workspaceId
              AND ProjectId = $projectId
              AND (SourceSymbolId = $symbolId OR TargetSymbolId = $symbolId)
            ORDER BY SourceFilePath, SourceLine, SourceSymbolId, TargetSymbolId;
            """;
        command.Parameters.AddWithValue("$workspaceId", workspaceId);
        command.Parameters.AddWithValue("$projectId", projectId);
        command.Parameters.AddWithValue("$symbolId", symbolId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            results.Add(ReadReference(reader));

        return results;
    }

    public async Task ClearReferencesAsync(
        string workspaceId,
        string projectId,
        string sourceSymbolId,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection, """
            DELETE FROM CodeReferences
            WHERE WorkspaceId = $workspaceId AND ProjectId = $projectId AND SourceSymbolId = $sourceSymbolId;
            """,
            cancellationToken,
            ("$workspaceId", workspaceId),
            ("$projectId", projectId),
            ("$sourceSymbolId", sourceSymbolId)).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<CodeRelationRecord>> ListRelationsCoreAsync(
        string workspaceId,
        string projectId,
        string symbolColumn,
        string symbolId,
        CodeRelationKind? relationKind,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        var results = new List<CodeRelationRecord>();
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT WorkspaceId, ProjectId, SourceSymbolId, TargetSymbolId, Kind, SourceLine, SourceFilePath, CreatedAtUtc
            FROM CodeRelations
            WHERE WorkspaceId = $workspaceId
              AND ProjectId = $projectId
              AND {symbolColumn} = $symbolId
              AND ($kind IS NULL OR Kind = $kind)
            ORDER BY SourceFilePath, SourceLine, SourceSymbolId, TargetSymbolId, Kind;
            """;
        command.Parameters.AddWithValue("$workspaceId", workspaceId);
        command.Parameters.AddWithValue("$projectId", projectId);
        command.Parameters.AddWithValue("$symbolId", symbolId);
        command.Parameters.AddWithValue("$kind", relationKind?.ToString() is { } kind ? kind : DBNull.Value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            results.Add(ReadRelation(reader));

        return results;
    }

    private async Task RemoveSymbolGraphForFileAsync(
        SqliteConnection connection,
        DbTransaction transaction,
        string workspaceId,
        string projectId,
        string filePath,
        CancellationToken cancellationToken)
    {
        var symbolIds = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                SELECT SymbolId
                FROM CodeSymbols
                WHERE WorkspaceId = $workspaceId AND ProjectId = $projectId AND FilePath = $filePath;
                """;
            command.Parameters.AddWithValue("$workspaceId", workspaceId);
            command.Parameters.AddWithValue("$projectId", projectId);
            command.Parameters.AddWithValue("$filePath", filePath);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                symbolIds.Add(reader.GetString(0));
        }

        foreach (var symbolId in symbolIds)
        {
            await ExecuteNonQueryAsync(connection, transaction, """
                DELETE FROM CodeReferences
                WHERE WorkspaceId = $workspaceId
                  AND ProjectId = $projectId
                  AND (SourceSymbolId = $symbolId OR TargetSymbolId = $symbolId);
                DELETE FROM CodeRelations
                WHERE WorkspaceId = $workspaceId
                  AND ProjectId = $projectId
                  AND (SourceSymbolId = $symbolId OR TargetSymbolId = $symbolId);
                """,
                cancellationToken,
                ("$workspaceId", workspaceId),
                ("$projectId", projectId),
                ("$symbolId", symbolId)).ConfigureAwait(false);
        }

        await ExecuteNonQueryAsync(connection, transaction, """
            DELETE FROM CodeSymbols
            WHERE WorkspaceId = $workspaceId AND ProjectId = $projectId AND FilePath = $filePath;
            """,
            cancellationToken,
            ("$workspaceId", workspaceId),
            ("$projectId", projectId),
            ("$filePath", filePath)).ConfigureAwait(false);
    }

    private Task EnsureInitializedAsync(CancellationToken cancellationToken) =>
        _initialized ? Task.CompletedTask : InitializeAsync(cancellationToken);

    private SqliteConnection CreateConnection() =>
        new(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Pooling = false,
        }.ToString());

    private static async Task ExecuteNonQueryAsync(
        SqliteConnection connection,
        string commandText,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        AddParameters(command, parameters);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ExecuteNonQueryAsync(
        SqliteConnection connection,
        DbTransaction transaction,
        string commandText,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = commandText;
        AddParameters(command, parameters);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddParameters(SqliteCommand command, params (string Name, object? Value)[] parameters)
    {
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
    }

    private static void AddProjectParameters(SqliteCommand command, CodeProjectRecord project)
    {
        command.Parameters.AddWithValue("$workspaceId", project.WorkspaceId);
        command.Parameters.AddWithValue("$projectId", project.ProjectId);
        command.Parameters.AddWithValue("$projectPath", project.ProjectPath);
        command.Parameters.AddWithValue("$status", project.Status.ToString());
        command.Parameters.AddWithValue("$displayName", (object?)project.DisplayName ?? DBNull.Value);
        command.Parameters.AddWithValue("$addedAtUtc", FormatDate(project.AddedAtUtc));
        command.Parameters.AddWithValue("$updatedAtUtc", FormatDate(project.UpdatedAtUtc));
        command.Parameters.AddWithValue("$source", project.Source?.ToString() ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$scopeState", project.ScopeState?.ToString() ?? (object)DBNull.Value);
    }

    private static CodeProjectRecord ReadProject(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            ParseEnum<CodeProjectStatus>(reader.GetString(3)),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            ReadDate(reader, 5),
            ReadDate(reader, 6),
            Source: ReadEnumOrNull<ScopeSource>(reader, 8),
            ScopeState: ReadEnumOrNull<ScopeState>(reader, 9));

    private static CodeFileRecord ReadFile(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            ReadDate(reader, 4));

    private static CodeSymbolRecord ReadSymbol(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            ParseEnum<CodeSymbolKind>(reader.GetString(5)),
            reader.GetInt32(6),
            reader.GetInt32(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9));

    private static CodeRelationRecord ReadRelation(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            ParseEnum<CodeRelationKind>(reader.GetString(4)),
            reader.IsDBNull(5) ? null : reader.GetInt32(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            ReadDate(reader, 7));

    private static CodeReferenceRecord ReadReference(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetInt32(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            ReadDate(reader, 7));

    private static TEnum ParseEnum<TEnum>(string value)
        where TEnum : struct, Enum =>
        Enum.TryParse<TEnum>(value, ignoreCase: false, out var parsed) ? parsed : default;

    private static TEnum? ReadEnumOrNull<TEnum>(SqliteDataReader reader, int ordinal)
        where TEnum : struct, Enum =>
        reader.IsDBNull(ordinal) ? null : ParseEnum<TEnum>(reader.GetString(ordinal));

    private static DateTimeOffset? ReadDate(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal)
            ? null
            : DateTimeOffset.Parse(reader.GetString(ordinal), CultureInfo.InvariantCulture);

    private static object FormatDate(DateTimeOffset? date) =>
        date?.ToString("O") ?? (object)DBNull.Value;

    private static string EscapeLike(string value) =>
        value
            .Replace(@"\", @"\\", StringComparison.Ordinal)
            .Replace("%", @"\%", StringComparison.Ordinal)
            .Replace("_", @"\_", StringComparison.Ordinal);

    private static void ValidateScope(string expectedWorkspaceId, string expectedProjectId, string actualWorkspaceId, string actualProjectId)
    {
        if (!string.Equals(expectedWorkspaceId, actualWorkspaceId, StringComparison.Ordinal)
            || !string.Equals(expectedProjectId, actualProjectId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Code index record scope does not match the requested workspace/project.");
        }
    }
}
