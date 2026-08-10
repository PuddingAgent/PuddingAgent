using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PuddingCode.Orchestration;
using PuddingPlatform.Data;

namespace PuddingPlatform.Services.Orchestration;

/// <summary>
/// SQLite implementation of the generic orchestration persistence boundary. State projection and
/// append-only events are committed in the same transaction. Signals are emitted after commit only.
/// </summary>
public sealed class SqliteAgentOrchestrationStore(
    IDbContextFactory<PlatformDbContext> dbFactory,
    AgentOrchestrationGraphCompiler compiler,
    IAgentOrchestrationCommittedEventSignal signal,
    TimeProvider timeProvider,
    ILogger<SqliteAgentOrchestrationStore> logger) : IAgentOrchestrationStore
{
    private static readonly JsonSerializerOptions JsonOptions =
        AgentOrchestrationJson.CreateSerializerOptions();

    public async Task<IReadOnlyList<AgentOrchestrationGraphSummary>> ListGraphsAsync(
        string? workspaceId,
        int limit,
        int offset,
        CancellationToken ct = default)
    {
        var graphs = new List<AgentOrchestrationGraphSummary>();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT g.graph_id, g.workspace_id, g.root_session_id, g.created_by_agent_id,
                   g.objective, g.current_revision, g.current_revision_id,
                   (SELECT COUNT(*) FROM orchestration_runs r WHERE r.graph_id = g.graph_id) AS run_count,
                   (SELECT COUNT(*) FROM orchestration_runs r
                    WHERE r.graph_id = g.graph_id AND r.status IN ('Active', 'AwaitingInput')) AS active_run_count,
                   g.created_at, g.updated_at
            FROM orchestration_graphs g
            WHERE (@workspaceId IS NULL OR g.workspace_id = @workspaceId)
            ORDER BY g.updated_at DESC, g.graph_id
            LIMIT @limit OFFSET @offset
            """;
        AddParameter(
            command,
            "@workspaceId",
            string.IsNullOrWhiteSpace(workspaceId) ? null : workspaceId.Trim());
        AddParameter(command, "@limit", Math.Clamp(limit, 1, 500));
        AddParameter(command, "@offset", Math.Max(0, offset));
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            graphs.Add(new AgentOrchestrationGraphSummary
            {
                GraphId = reader.GetString(0),
                WorkspaceId = reader.GetString(1),
                RootSessionId = reader.GetString(2),
                CreatedByAgentId = reader.GetString(3),
                Objective = reader.GetString(4),
                CurrentRevision = reader.GetInt32(5),
                CurrentRevisionId = reader.GetString(6),
                RunCount = checked((int)reader.GetInt64(7)),
                ActiveRunCount = checked((int)reader.GetInt64(8)),
                CreatedAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(9)),
                UpdatedAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(10))
            });
        }

        return graphs.AsReadOnly();
    }

    public async Task<AgentOrchestrationStoreResult<AgentOrchestrationGraphDefinition>> SaveRevisionAsync(
        AgentOrchestrationRevisionWriteRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Definition);

        var compilation = compiler.Compile(request.Definition);
        if (!compilation.Success)
        {
            return Invalid<AgentOrchestrationGraphDefinition>(
                "orchestration.definition_invalid",
                string.Join("; ", compilation.Issues.Select(issue => $"{issue.Code}: {issue.Message}")));
        }

        var definition = compilation.Definition!;
        var definitionJson = JsonSerializer.Serialize(definition, JsonOptions);
        var contentHash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(definitionJson)))
            .ToLowerInvariant();
        var nowMs = UtcNowMs();

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        await connection.OpenAsync(ct);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            ct);

        try
        {
            var existingRevision = await ReadRevisionIdentityAsync(
                connection,
                transaction,
                definition.RevisionId,
                ct);
            if (existingRevision is not null)
            {
                await transaction.RollbackAsync(ct);
                if (string.Equals(existingRevision.Value.ContentHash, contentHash, StringComparison.Ordinal))
                {
                    return new AgentOrchestrationStoreResult<AgentOrchestrationGraphDefinition>
                    {
                        Status = AgentOrchestrationStoreStatus.Unchanged,
                        Value = definition,
                        CurrentVersion = existingRevision.Value.Revision
                    };
                }

                return Conflict<AgentOrchestrationGraphDefinition>(
                    "orchestration.revision_id_conflict",
                    "RevisionId already exists with different content.",
                    existingRevision.Value.Revision);
            }

            var graphHead = await ReadGraphHeadAsync(connection, transaction, definition.GraphId, ct);
            if (graphHead is null)
            {
                if (request.ExpectedCurrentRevision != 0 || definition.Revision != 1)
                {
                    await transaction.RollbackAsync(ct);
                    return Conflict<AgentOrchestrationGraphDefinition>(
                        "orchestration.graph_create_revision_conflict",
                        "Creating a graph requires ExpectedCurrentRevision=0 and Revision=1.",
                        0);
                }

                await ExecuteAsync(
                    connection,
                    transaction,
                    """
                    INSERT INTO orchestration_graphs
                    (graph_id, workspace_id, root_session_id, created_by_agent_id, objective,
                     current_revision, current_revision_id, created_at, updated_at)
                    VALUES
                    (@graphId, @workspaceId, @rootSessionId, @createdByAgentId, @objective,
                     @revision, @revisionId, @createdAt, @updatedAt)
                    """,
                    ct,
                    ("@graphId", definition.GraphId),
                    ("@workspaceId", definition.WorkspaceId),
                    ("@rootSessionId", definition.RootSessionId),
                    ("@createdByAgentId", definition.CreatedByAgentId),
                    ("@objective", definition.Objective),
                    ("@revision", definition.Revision),
                    ("@revisionId", definition.RevisionId),
                    ("@createdAt", nowMs),
                    ("@updatedAt", nowMs));
            }
            else
            {
                if (graphHead.Value.Revision != request.ExpectedCurrentRevision ||
                    definition.Revision != request.ExpectedCurrentRevision + 1 ||
                    !IdEquals(definition.ParentRevisionId, graphHead.Value.RevisionId))
                {
                    await transaction.RollbackAsync(ct);
                    return Conflict<AgentOrchestrationGraphDefinition>(
                        "orchestration.revision_compare_exchange_failed",
                        "Expected revision, next revision, or ParentRevisionId does not match the graph head.",
                        graphHead.Value.Revision);
                }

                var updated = await ExecuteAsync(
                    connection,
                    transaction,
                    """
                    UPDATE orchestration_graphs
                    SET current_revision = @revision,
                        current_revision_id = @revisionId,
                        objective = @objective,
                        updated_at = @updatedAt
                    WHERE graph_id = @graphId
                      AND current_revision = @expectedRevision
                    """,
                    ct,
                    ("@revision", definition.Revision),
                    ("@revisionId", definition.RevisionId),
                    ("@objective", definition.Objective),
                    ("@updatedAt", nowMs),
                    ("@graphId", definition.GraphId),
                    ("@expectedRevision", request.ExpectedCurrentRevision));
                if (updated != 1)
                {
                    await transaction.RollbackAsync(ct);
                    return Conflict<AgentOrchestrationGraphDefinition>(
                        "orchestration.revision_compare_exchange_failed",
                        "The graph head changed before the revision could be committed.",
                        graphHead.Value.Revision);
                }
            }

            await ExecuteAsync(
                connection,
                transaction,
                """
                INSERT INTO orchestration_graph_revisions
                (revision_id, graph_id, revision, parent_revision_id, schema_version,
                 definition_json, content_hash, created_by_agent_id, created_at)
                VALUES
                (@revisionId, @graphId, @revision, @parentRevisionId, @schemaVersion,
                 @definitionJson, @contentHash, @createdByAgentId, @createdAt)
                """,
                ct,
                ("@revisionId", definition.RevisionId),
                ("@graphId", definition.GraphId),
                ("@revision", definition.Revision),
                ("@parentRevisionId", definition.ParentRevisionId),
                ("@schemaVersion", definition.SchemaVersion),
                ("@definitionJson", definitionJson),
                ("@contentHash", contentHash),
                ("@createdByAgentId", definition.CreatedByAgentId),
                ("@createdAt", nowMs));

            await transaction.CommitAsync(ct);
            logger.LogInformation(
                "[AgentOrchestrationStore] Saved graph={GraphId} revision={RevisionId} number={Revision}",
                definition.GraphId,
                definition.RevisionId,
                definition.Revision);
            return new AgentOrchestrationStoreResult<AgentOrchestrationGraphDefinition>
            {
                Status = AgentOrchestrationStoreStatus.Applied,
                Value = definition,
                CurrentVersion = definition.Revision
            };
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }
    }

    public async Task<AgentOrchestrationStoreResult<AgentOrchestrationGraphDeleteReceipt>> DeleteGraphAsync(
        AgentOrchestrationGraphDeleteRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.GraphId))
        {
            return Invalid<AgentOrchestrationGraphDeleteReceipt>(
                "orchestration.graph_id_required",
                "GraphId is required.");
        }
        if (request.ExpectedCurrentRevision < 1)
        {
            return Invalid<AgentOrchestrationGraphDeleteReceipt>(
                "orchestration.graph_delete_revision_invalid",
                "ExpectedCurrentRevision must be positive.");
        }

        var graphId = request.GraphId.Trim();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        await connection.OpenAsync(ct);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            ct);

        try
        {
            var graphHead = await ReadGraphHeadAsync(connection, transaction, graphId, ct);
            if (graphHead is null)
            {
                await transaction.RollbackAsync(ct);
                return NotFound<AgentOrchestrationGraphDeleteReceipt>(
                    "orchestration.graph_not_found",
                    $"Graph '{graphId}' was not found.");
            }
            if (graphHead.Value.Revision != request.ExpectedCurrentRevision)
            {
                await transaction.RollbackAsync(ct);
                return Conflict<AgentOrchestrationGraphDeleteReceipt>(
                    "orchestration.graph_delete_compare_exchange_failed",
                    "The graph head changed before deletion.",
                    graphHead.Value.Revision);
            }

            var runCount = await ExecuteScalarInt64Async(
                connection,
                transaction,
                "SELECT COUNT(*) FROM orchestration_runs WHERE graph_id = @graphId",
                ct,
                ("@graphId", graphId));
            if (runCount > 0)
            {
                await transaction.RollbackAsync(ct);
                return InvalidState<AgentOrchestrationGraphDeleteReceipt>(
                    "orchestration.graph_has_runs",
                    $"Graph '{graphId}' has {runCount} durable run(s) and cannot be deleted.",
                    graphHead.Value.Revision);
            }

            var layoutCount = await ExecuteScalarInt64Async(
                connection,
                transaction,
                "SELECT COUNT(*) FROM orchestration_graph_layouts WHERE graph_id = @graphId",
                ct,
                ("@graphId", graphId));
            var revisionCount = await ExecuteScalarInt64Async(
                connection,
                transaction,
                "SELECT COUNT(*) FROM orchestration_graph_revisions WHERE graph_id = @graphId",
                ct,
                ("@graphId", graphId));

            await ExecuteAsync(
                connection,
                transaction,
                "DELETE FROM orchestration_graph_layouts WHERE graph_id = @graphId",
                ct,
                ("@graphId", graphId));
            await ExecuteAsync(
                connection,
                transaction,
                "DELETE FROM orchestration_graph_revisions WHERE graph_id = @graphId",
                ct,
                ("@graphId", graphId));
            var deleted = await ExecuteAsync(
                connection,
                transaction,
                "DELETE FROM orchestration_graphs WHERE graph_id = @graphId AND current_revision = @expectedRevision",
                ct,
                ("@graphId", graphId),
                ("@expectedRevision", request.ExpectedCurrentRevision));
            if (deleted != 1)
                throw new InvalidOperationException($"Graph '{graphId}' changed while it was being deleted.");

            await transaction.CommitAsync(ct);
            logger.LogInformation(
                "[AgentOrchestrationStore] Deleted graph={GraphId} revision={Revision} revisions={RevisionCount} layouts={LayoutCount}",
                graphId,
                graphHead.Value.Revision,
                revisionCount,
                layoutCount);
            return new AgentOrchestrationStoreResult<AgentOrchestrationGraphDeleteReceipt>
            {
                Status = AgentOrchestrationStoreStatus.Applied,
                Value = new AgentOrchestrationGraphDeleteReceipt
                {
                    GraphId = graphId,
                    PreviousRevision = graphHead.Value.Revision,
                    DeletedRevisionCount = checked((int)revisionCount),
                    DeletedLayoutCount = checked((int)layoutCount)
                },
                CurrentVersion = graphHead.Value.Revision
            };
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }
    }

    public async Task<AgentOrchestrationStoreResult<AgentOrchestrationGraphLayout>> SaveLayoutAsync(
        AgentOrchestrationLayoutWriteRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Layout);

        var layout = NormalizeLayout(request.Layout);
        var validationError = ValidateLayoutShape(layout, request.ExpectedCurrentLayoutRevision);
        if (validationError is not null)
            return Invalid<AgentOrchestrationGraphLayout>(validationError.Value.Code, validationError.Value.Message);

        // The executable revision is immutable. Validate it before acquiring SQLite's
        // serializable write transaction so malformed or stale editor requests do not
        // wait behind unrelated platform writers merely to return a deterministic 4xx.
        AgentOrchestrationGraphDefinition? definition;
        await using (var definitionDb = await dbFactory.CreateDbContextAsync(ct))
        {
            var definitionConnection = (SqliteConnection)definitionDb.Database.GetDbConnection();
            await definitionConnection.OpenAsync(ct);
            definition = await ReadDefinitionByRevisionAsync(
                definitionConnection,
                transaction: null,
                layout.BaseRevisionId,
                ct);
        }

        if (definition is null)
        {
            return NotFound<AgentOrchestrationGraphLayout>(
                "orchestration.layout_base_revision_not_found",
                $"Base revision '{layout.BaseRevisionId}' was not found.");
        }
        if (!IdEquals(definition.GraphId, layout.GraphId))
        {
            return Invalid<AgentOrchestrationGraphLayout>(
                "orchestration.layout_graph_mismatch",
                "Layout GraphId does not match its base revision.");
        }

        var knownNodeIds = definition.Nodes
            .Select(node => node.NodeId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unknownNode = layout.Nodes.FirstOrDefault(node => !knownNodeIds.Contains(node.NodeId));
        if (unknownNode is not null)
        {
            return Invalid<AgentOrchestrationGraphLayout>(
                "orchestration.layout_unknown_node",
                $"Layout node '{unknownNode.NodeId}' does not exist in the base revision.");
        }
        var unknownParent = layout.Nodes.FirstOrDefault(
            node => node.ParentNodeId is not null && !knownNodeIds.Contains(node.ParentNodeId));
        if (unknownParent is not null)
        {
            return Invalid<AgentOrchestrationGraphLayout>(
                "orchestration.layout_unknown_parent",
                $"Layout parent '{unknownParent.ParentNodeId}' does not exist in the base revision.");
        }

        var layoutJson = JsonSerializer.Serialize(layout, JsonOptions);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        await connection.OpenAsync(ct);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            ct);

        try
        {
            var current = await ReadLayoutIdentityAsync(
                connection,
                transaction,
                layout.GraphId,
                layout.BaseRevisionId,
                ct);
            if (current is not null && string.Equals(current.Value.LayoutJson, layoutJson, StringComparison.Ordinal))
            {
                await transaction.RollbackAsync(ct);
                return new AgentOrchestrationStoreResult<AgentOrchestrationGraphLayout>
                {
                    Status = AgentOrchestrationStoreStatus.Unchanged,
                    Value = layout,
                    CurrentVersion = current.Value.LayoutRevision
                };
            }

            var nowMs = UtcNowMs();
            if (current is null)
            {
                if (request.ExpectedCurrentLayoutRevision != 0 || layout.LayoutRevision != 1)
                {
                    await transaction.RollbackAsync(ct);
                    return Conflict<AgentOrchestrationGraphLayout>(
                        "orchestration.layout_create_revision_conflict",
                        "Creating a layout requires ExpectedCurrentLayoutRevision=0 and LayoutRevision=1.",
                        0);
                }

                await ExecuteAsync(
                    connection,
                    transaction,
                    """
                    INSERT INTO orchestration_graph_layouts
                    (graph_id, base_revision_id, layout_revision, layout_json, updated_at)
                    VALUES (@graphId, @baseRevisionId, @layoutRevision, @layoutJson, @updatedAt)
                    """,
                    ct,
                    ("@graphId", layout.GraphId),
                    ("@baseRevisionId", layout.BaseRevisionId),
                    ("@layoutRevision", layout.LayoutRevision),
                    ("@layoutJson", layoutJson),
                    ("@updatedAt", nowMs));
            }
            else
            {
                if (request.ExpectedCurrentLayoutRevision != current.Value.LayoutRevision ||
                    layout.LayoutRevision != current.Value.LayoutRevision + 1)
                {
                    await transaction.RollbackAsync(ct);
                    return Conflict<AgentOrchestrationGraphLayout>(
                        "orchestration.layout_compare_exchange_failed",
                        "Expected layout revision or next LayoutRevision does not match the current layout.",
                        current.Value.LayoutRevision);
                }

                var updated = await ExecuteAsync(
                    connection,
                    transaction,
                    """
                    UPDATE orchestration_graph_layouts
                    SET layout_revision = @layoutRevision,
                        layout_json = @layoutJson,
                        updated_at = @updatedAt
                    WHERE graph_id = @graphId
                      AND base_revision_id = @baseRevisionId
                      AND layout_revision = @expectedRevision
                    """,
                    ct,
                    ("@layoutRevision", layout.LayoutRevision),
                    ("@layoutJson", layoutJson),
                    ("@updatedAt", nowMs),
                    ("@graphId", layout.GraphId),
                    ("@baseRevisionId", layout.BaseRevisionId),
                    ("@expectedRevision", request.ExpectedCurrentLayoutRevision));
                if (updated != 1)
                {
                    await transaction.RollbackAsync(ct);
                    return Conflict<AgentOrchestrationGraphLayout>(
                        "orchestration.layout_compare_exchange_failed",
                        "The layout changed before the update could be committed.",
                        current.Value.LayoutRevision);
                }
            }

            await transaction.CommitAsync(ct);
            logger.LogInformation(
                "[AgentOrchestrationStore] Saved layout graph={GraphId} baseRevision={BaseRevisionId} layoutRevision={LayoutRevision}",
                layout.GraphId,
                layout.BaseRevisionId,
                layout.LayoutRevision);
            return new AgentOrchestrationStoreResult<AgentOrchestrationGraphLayout>
            {
                Status = AgentOrchestrationStoreStatus.Applied,
                Value = layout,
                CurrentVersion = layout.LayoutRevision
            };
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }
    }

    public async Task<AgentOrchestrationGraphDefinition?> GetRevisionAsync(
        string revisionId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(revisionId))
            return null;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        await connection.OpenAsync(ct);
        return await ReadDefinitionByRevisionAsync(connection, null, revisionId.Trim(), ct);
    }

    public async Task<AgentOrchestrationGraphDefinition?> GetLatestRevisionAsync(
        string graphId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(graphId))
            return null;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT r.definition_json
            FROM orchestration_graphs g
            JOIN orchestration_graph_revisions r ON r.revision_id = g.current_revision_id
            WHERE g.graph_id = @graphId
            """;
        AddParameter(command, "@graphId", graphId.Trim());
        var json = await command.ExecuteScalarAsync(ct) as string;
        return DeserializeDefinition(json);
    }

    public async Task<IReadOnlyList<AgentOrchestrationRevisionSummary>> ListRevisionsAsync(
        string graphId,
        int limit,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(graphId))
            return Array.Empty<AgentOrchestrationRevisionSummary>();

        var revisions = new List<AgentOrchestrationRevisionSummary>();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT graph_id, revision_id, revision, parent_revision_id, schema_version,
                   content_hash, created_by_agent_id, created_at
            FROM orchestration_graph_revisions
            WHERE graph_id = @graphId
            ORDER BY revision DESC
            LIMIT @limit
            """;
        AddParameter(command, "@graphId", graphId.Trim());
        AddParameter(command, "@limit", Math.Clamp(limit, 1, 500));
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            revisions.Add(new AgentOrchestrationRevisionSummary
            {
                GraphId = reader.GetString(0),
                RevisionId = reader.GetString(1),
                Revision = reader.GetInt32(2),
                ParentRevisionId = GetNullableString(reader, 3),
                SchemaVersion = reader.GetString(4),
                ContentHash = reader.GetString(5),
                CreatedByAgentId = reader.GetString(6),
                CreatedAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(7))
            });
        }

        return revisions.AsReadOnly();
    }

    public async Task<AgentOrchestrationStoreResult<AgentOrchestrationRunSnapshot>> CreateRunAsync(
        AgentOrchestrationRunCreateRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.RunId) ||
            string.IsNullOrWhiteSpace(request.RevisionId) ||
            string.IsNullOrWhiteSpace(request.RequestedByAgentId))
        {
            return Invalid<AgentOrchestrationRunSnapshot>(
                "orchestration.run_create_invalid",
                "RunId, RevisionId, and RequestedByAgentId are required.");
        }

        var runId = request.RunId.Trim();
        var revisionId = request.RevisionId.Trim();
        var nowMs = UtcNowMs();
        long committedHead = 0;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        await connection.OpenAsync(ct);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            ct);

        try
        {
            var existing = await ReadRunRowAsync(connection, transaction, runId, ct);
            if (existing is not null)
            {
                await transaction.RollbackAsync(ct);
                var snapshot = await GetRunAsync(runId, ct);
                if (IdEquals(existing.RevisionId, revisionId))
                {
                    return new AgentOrchestrationStoreResult<AgentOrchestrationRunSnapshot>
                    {
                        Status = AgentOrchestrationStoreStatus.Unchanged,
                        Value = snapshot,
                        CurrentVersion = existing.Version
                    };
                }

                return Conflict<AgentOrchestrationRunSnapshot>(
                    "orchestration.run_id_conflict",
                    "RunId already exists for another graph revision.",
                    existing.Version);
            }

            var definition = await ReadDefinitionByRevisionAsync(connection, transaction, revisionId, ct);
            if (definition is null)
            {
                await transaction.RollbackAsync(ct);
                return NotFound<AgentOrchestrationRunSnapshot>(
                    "orchestration.revision_not_found",
                    $"Revision '{revisionId}' was not found.");
            }

            committedHead = 1;
            await ExecuteAsync(
                connection,
                transaction,
                """
                INSERT INTO orchestration_runs
                (run_id, graph_id, revision_id, workspace_id, root_session_id,
                 requested_by_agent_id, status, version, head_sequence, max_concurrency,
                 created_at, updated_at)
                VALUES
                (@runId, @graphId, @revisionId, @workspaceId, @rootSessionId,
                 @requestedByAgentId, @status, 1, 1, @maxConcurrency,
                 @createdAt, @updatedAt)
                """,
                ct,
                ("@runId", runId),
                ("@graphId", definition.GraphId),
                ("@revisionId", definition.RevisionId),
                ("@workspaceId", definition.WorkspaceId),
                ("@rootSessionId", definition.RootSessionId),
                ("@requestedByAgentId", request.RequestedByAgentId.Trim()),
                ("@status", AgentOrchestrationRunStatus.Draft.ToString()),
                ("@maxConcurrency", definition.MaxConcurrency),
                ("@createdAt", nowMs),
                ("@updatedAt", nowMs));

            foreach (var node in definition.Nodes)
            {
                await ExecuteAsync(
                    connection,
                    transaction,
                    """
                    INSERT INTO orchestration_node_runs
                    (run_id, node_id, node_kind, status, attempt, max_attempts,
                     fencing_token, updated_at)
                    VALUES
                    (@runId, @nodeId, @nodeKind, @status, 0, @maxAttempts, 0, @updatedAt)
                    """,
                    ct,
                    ("@runId", runId),
                    ("@nodeId", node.NodeId),
                    ("@nodeKind", node.Kind.ToString()),
                    ("@status", AgentOrchestrationNodeRunStatus.Pending.ToString()),
                    ("@maxAttempts", node.MaxAttempts),
                    ("@updatedAt", nowMs));
            }

            var runRow = new RunRow(
                runId,
                definition.GraphId,
                definition.RevisionId,
                definition.WorkspaceId,
                definition.RootSessionId,
                request.RequestedByAgentId.Trim(),
                AgentOrchestrationRunStatus.Draft.ToString(),
                1,
                committedHead,
                definition.MaxConcurrency,
                nowMs,
                null,
                nowMs,
                null,
                null);
            await AppendEventAsync(
                connection,
                transaction,
                runRow,
                committedHead,
                AgentOrchestrationEventTypes.RunCreated,
                nodeId: null,
                executionRunId: null,
                subSessionId: null,
                summary: "Orchestration run created in Draft state.",
                artifactReference: null,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["requestedByAgentId"] = request.RequestedByAgentId.Trim()
                },
                nowMs,
                ct);

            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }

        signal.Signal(runId, committedHead);
        logger.LogInformation(
            "[AgentOrchestrationStore] Created run={RunId} revision={RevisionId} head={Head}",
            runId,
            revisionId,
            committedHead);
        var created = await GetRunAsync(runId, ct)
            ?? throw new InvalidOperationException($"Committed orchestration run '{runId}' could not be reloaded.");
        return new AgentOrchestrationStoreResult<AgentOrchestrationRunSnapshot>
        {
            Status = AgentOrchestrationStoreStatus.Applied,
            Value = created,
            CurrentVersion = created.Version
        };
    }

    public async Task<AgentOrchestrationStoreResult<AgentOrchestrationRunSnapshot>> ActivateRunAsync(
        AgentOrchestrationRunActivationRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.RunId))
            return Invalid<AgentOrchestrationRunSnapshot>("orchestration.run_id_required", "RunId is required.");

        var runId = request.RunId.Trim();
        long committedHead;
        long committedVersion;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        await connection.OpenAsync(ct);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            ct);

        try
        {
            var run = await ReadRunRowAsync(connection, transaction, runId, ct);
            if (run is null)
            {
                await transaction.RollbackAsync(ct);
                return NotFound<AgentOrchestrationRunSnapshot>(
                    "orchestration.run_not_found",
                    $"Run '{runId}' was not found.");
            }

            if (ParseRunStatus(run.Status) == AgentOrchestrationRunStatus.Active)
            {
                await transaction.RollbackAsync(ct);
                return new AgentOrchestrationStoreResult<AgentOrchestrationRunSnapshot>
                {
                    Status = AgentOrchestrationStoreStatus.Unchanged,
                    Value = await GetRunAsync(runId, ct),
                    CurrentVersion = run.Version
                };
            }

            if (ParseRunStatus(run.Status) != AgentOrchestrationRunStatus.Draft)
            {
                await transaction.RollbackAsync(ct);
                return InvalidState<AgentOrchestrationRunSnapshot>(
                    "orchestration.run_not_draft",
                    $"Run '{runId}' is '{run.Status}', not Draft.",
                    run.Version);
            }

            if (run.Version != request.ExpectedVersion)
            {
                await transaction.RollbackAsync(ct);
                return Conflict<AgentOrchestrationRunSnapshot>(
                    "orchestration.run_version_conflict",
                    "ExpectedVersion does not match the durable run version.",
                    run.Version);
            }

            var definition = await ReadDefinitionByRevisionAsync(
                connection,
                transaction,
                run.RevisionId,
                ct) ?? throw new InvalidOperationException($"Revision '{run.RevisionId}' is missing for run '{runId}'.");
            var nodesWithIncomingEdges = definition.Edges
                .Select(edge => edge.ToNodeId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var rootNodeIds = definition.Nodes
                .Where(node => !nodesWithIncomingEdges.Contains(node.NodeId))
                .Select(node => node.NodeId)
                .Order(StringComparer.Ordinal)
                .ToArray();
            if (rootNodeIds.Length == 0)
                throw new InvalidOperationException($"Run '{runId}' has no root nodes after DAG validation.");

            var nowMs = UtcNowMs();
            var nextSequence = run.HeadSequence;
            await AppendEventAsync(
                connection,
                transaction,
                run,
                ++nextSequence,
                AgentOrchestrationEventTypes.RunActivated,
                null,
                null,
                null,
                "Orchestration run activated.",
                null,
                EmptyAttributes(),
                nowMs,
                ct);

            foreach (var nodeId in rootNodeIds)
            {
                var updated = await ExecuteAsync(
                    connection,
                    transaction,
                    """
                    UPDATE orchestration_node_runs
                    SET status = @ready, updated_at = @updatedAt
                    WHERE run_id = @runId AND node_id = @nodeId AND status = @pending
                    """,
                    ct,
                    ("@ready", AgentOrchestrationNodeRunStatus.Ready.ToString()),
                    ("@updatedAt", nowMs),
                    ("@runId", runId),
                    ("@nodeId", nodeId),
                    ("@pending", AgentOrchestrationNodeRunStatus.Pending.ToString()));
                if (updated != 1)
                    throw new InvalidOperationException($"Root node '{nodeId}' did not transition Pending -> Ready.");

                await AppendEventAsync(
                    connection,
                    transaction,
                    run,
                    ++nextSequence,
                    AgentOrchestrationEventTypes.NodeReady,
                    nodeId,
                    null,
                    null,
                    "Root node is ready.",
                    null,
                    EmptyAttributes(),
                    nowMs,
                    ct);
            }

            committedVersion = run.Version + 1;
            committedHead = nextSequence;
            var runUpdated = await ExecuteAsync(
                connection,
                transaction,
                """
                UPDATE orchestration_runs
                SET status = @active,
                    version = @version,
                    head_sequence = @headSequence,
                    activated_at = @activatedAt,
                    updated_at = @updatedAt
                WHERE run_id = @runId AND status = @draft AND version = @expectedVersion
                """,
                ct,
                ("@active", AgentOrchestrationRunStatus.Active.ToString()),
                ("@version", committedVersion),
                ("@headSequence", committedHead),
                ("@activatedAt", nowMs),
                ("@updatedAt", nowMs),
                ("@runId", runId),
                ("@draft", AgentOrchestrationRunStatus.Draft.ToString()),
                ("@expectedVersion", request.ExpectedVersion));
            if (runUpdated != 1)
                throw new InvalidOperationException($"Run '{runId}' activation compare-and-swap failed.");

            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }

        signal.Signal(runId, committedHead);
        logger.LogInformation(
            "[AgentOrchestrationStore] Activated run={RunId} version={Version} head={Head}",
            runId,
            committedVersion,
            committedHead);
        var snapshot = await GetRunAsync(runId, ct)
            ?? throw new InvalidOperationException($"Activated run '{runId}' could not be reloaded.");
        return new AgentOrchestrationStoreResult<AgentOrchestrationRunSnapshot>
        {
            Status = AgentOrchestrationStoreStatus.Applied,
            Value = snapshot,
            CurrentVersion = committedVersion
        };
    }

    public async Task<AgentOrchestrationRunSnapshot?> GetRunAsync(
        string runId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(runId))
            return null;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        await connection.OpenAsync(ct);
        var run = await ReadRunRowAsync(connection, null, runId.Trim(), ct);
        if (run is null)
            return null;

        var nodes = new List<AgentOrchestrationNodeRunSnapshot>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT node_id, node_kind, status, attempt, max_attempts, claim_id,
                   lease_owner, lease_until, fencing_token, execution_run_id,
                   sub_session_id, output_summary, artifact_reference, error_message,
                   started_at, completed_at, updated_at
            FROM orchestration_node_runs
            WHERE run_id = @runId
            ORDER BY node_id
            """;
        AddParameter(command, "@runId", run.RunId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            nodes.Add(new AgentOrchestrationNodeRunSnapshot
            {
                NodeId = reader.GetString(0),
                Kind = ParseNodeKind(reader.GetString(1)),
                Status = ParseNodeRunStatus(reader.GetString(2)),
                Attempt = reader.GetInt32(3),
                MaxAttempts = reader.GetInt32(4),
                ClaimId = GetNullableString(reader, 5),
                LeaseOwner = GetNullableString(reader, 6),
                LeaseExpiresAtUtc = FromUnixMsNullable(GetNullableInt64(reader, 7)),
                FencingToken = reader.GetInt64(8),
                ExecutionRunId = GetNullableString(reader, 9),
                SubSessionId = GetNullableString(reader, 10),
                OutputSummary = GetNullableString(reader, 11),
                ArtifactReference = GetNullableString(reader, 12),
                ErrorMessage = GetNullableString(reader, 13),
                StartedAtUtc = FromUnixMsNullable(GetNullableInt64(reader, 14)),
                CompletedAtUtc = FromUnixMsNullable(GetNullableInt64(reader, 15)),
                UpdatedAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(16))
            });
        }

        return ToSnapshot(run, nodes.AsReadOnly());
    }

    public async Task<IReadOnlyList<AgentOrchestrationRunSummary>> ListRunsAsync(
        string? workspaceId,
        string? graphId,
        AgentOrchestrationRunStatus? status,
        int limit,
        int offset,
        CancellationToken ct = default)
    {
        var runs = new List<AgentOrchestrationRunSummary>();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT run_id, graph_id, revision_id, workspace_id, root_session_id,
                   requested_by_agent_id, status, version, head_sequence, max_concurrency,
                   created_at, activated_at, updated_at, completed_at, error_message
            FROM orchestration_runs
            WHERE (@workspaceId IS NULL OR workspace_id = @workspaceId)
              AND (@graphId IS NULL OR graph_id = @graphId)
              AND (@status IS NULL OR status = @status)
            ORDER BY updated_at DESC, run_id
            LIMIT @limit OFFSET @offset
            """;
        AddParameter(
            command,
            "@workspaceId",
            string.IsNullOrWhiteSpace(workspaceId) ? null : workspaceId.Trim());
        AddParameter(command, "@graphId", string.IsNullOrWhiteSpace(graphId) ? null : graphId.Trim());
        AddParameter(command, "@status", status?.ToString());
        AddParameter(command, "@limit", Math.Clamp(limit, 1, 500));
        AddParameter(command, "@offset", Math.Max(0, offset));
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            runs.Add(new AgentOrchestrationRunSummary
            {
                RunId = reader.GetString(0),
                GraphId = reader.GetString(1),
                RevisionId = reader.GetString(2),
                WorkspaceId = reader.GetString(3),
                RootSessionId = reader.GetString(4),
                RequestedByAgentId = reader.GetString(5),
                Status = ParseRunStatus(reader.GetString(6)),
                Version = reader.GetInt64(7),
                HeadSequence = reader.GetInt64(8),
                MaxConcurrency = reader.GetInt32(9),
                CreatedAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(10)),
                ActivatedAtUtc = FromUnixMsNullable(GetNullableInt64(reader, 11)),
                UpdatedAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(12)),
                CompletedAtUtc = FromUnixMsNullable(GetNullableInt64(reader, 13)),
                ErrorMessage = GetNullableString(reader, 14)
            });
        }

        return runs.AsReadOnly();
    }

    public async Task<AgentOrchestrationGraphLayout?> GetLayoutAsync(
        string graphId,
        string baseRevisionId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(graphId) || string.IsNullOrWhiteSpace(baseRevisionId))
            return null;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT layout_json
            FROM orchestration_graph_layouts
            WHERE graph_id = @graphId AND base_revision_id = @baseRevisionId
            """;
        AddParameter(command, "@graphId", graphId.Trim());
        AddParameter(command, "@baseRevisionId", baseRevisionId.Trim());
        var json = await command.ExecuteScalarAsync(ct) as string;
        return string.IsNullOrWhiteSpace(json)
            ? null
            : JsonSerializer.Deserialize<AgentOrchestrationGraphLayout>(json, JsonOptions);
    }

    public async Task<IReadOnlyList<AgentOrchestrationRunEvent>> GetEventsAfterAsync(
        string runId,
        long afterSequence,
        int limit,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(runId))
            return Array.Empty<AgentOrchestrationRunEvent>();

        var events = new List<AgentOrchestrationRunEvent>();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT event_id, run_id, graph_id, revision_id, sequence, event_type,
                   node_id, execution_run_id, sub_session_id, summary,
                   artifact_reference, attributes_json, recorded_at
            FROM orchestration_run_events
            WHERE run_id = @runId AND sequence > @afterSequence
            ORDER BY sequence
            LIMIT @limit
            """;
        AddParameter(command, "@runId", runId.Trim());
        AddParameter(command, "@afterSequence", Math.Max(0, afterSequence));
        AddParameter(command, "@limit", Math.Clamp(limit, 1, 1000));
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            events.Add(new AgentOrchestrationRunEvent
            {
                EventId = reader.GetString(0),
                RunId = reader.GetString(1),
                GraphId = reader.GetString(2),
                RevisionId = reader.GetString(3),
                Sequence = reader.GetInt64(4),
                EventType = reader.GetString(5),
                NodeId = GetNullableString(reader, 6),
                ExecutionRunId = GetNullableString(reader, 7),
                SubSessionId = GetNullableString(reader, 8),
                Summary = GetNullableString(reader, 9),
                ArtifactReference = GetNullableString(reader, 10),
                Attributes = DeserializeAttributes(reader.GetString(11)),
                RecordedAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(12))
            });
        }

        return events.AsReadOnly();
    }

    public async Task<AgentOrchestrationStoreResult<AgentOrchestrationNodeClaim>> TryClaimNextReadyNodeAsync(
        AgentOrchestrationNodeClaimRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.RunId) || string.IsNullOrWhiteSpace(request.WorkerId))
            return Invalid<AgentOrchestrationNodeClaim>("orchestration.claim_invalid", "RunId and WorkerId are required.");
        if (request.LeaseDuration <= TimeSpan.Zero)
            return Invalid<AgentOrchestrationNodeClaim>("orchestration.claim_lease_invalid", "LeaseDuration must be positive.");

        var runId = request.RunId.Trim();
        var workerId = request.WorkerId.Trim();
        var nowMs = UtcNowMs();
        var leaseUntilMs = nowMs + (long)request.LeaseDuration.TotalMilliseconds;
        long? committedHead = null;
        AgentOrchestrationNodeClaim? claim = null;
        AgentOrchestrationStoreResult<AgentOrchestrationNodeClaim>? earlyResult = null;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        await connection.OpenAsync(ct);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            ct);

        try
        {
            var run = await ReadRunRowAsync(connection, transaction, runId, ct);
            if (run is null)
            {
                await transaction.RollbackAsync(ct);
                return NotFound<AgentOrchestrationNodeClaim>("orchestration.run_not_found", $"Run '{runId}' was not found.");
            }
            if (ParseRunStatus(run.Status) != AgentOrchestrationRunStatus.Active)
            {
                await transaction.RollbackAsync(ct);
                return InvalidState<AgentOrchestrationNodeClaim>(
                    "orchestration.run_not_active",
                    $"Run '{runId}' is not Active.",
                    run.Version);
            }
            if (request.ExpectedRunVersion is not null && run.Version != request.ExpectedRunVersion.Value)
            {
                await transaction.RollbackAsync(ct);
                return Conflict<AgentOrchestrationNodeClaim>(
                    "orchestration.run_version_conflict",
                    "ExpectedRunVersion does not match the durable run version.",
                    run.Version);
            }

            var nextSequence = run.HeadSequence;
            var mutationCount = await ReclaimExpiredClaimsAsync(
                connection,
                transaction,
                run,
                nowMs,
                sequence => nextSequence = sequence,
                ct);

            var activeCount = await ExecuteScalarInt64Async(
                connection,
                transaction,
                """
                SELECT COUNT(*)
                FROM orchestration_node_runs
                WHERE run_id = @runId
                  AND status IN (@claimed, @running)
                  AND lease_until IS NOT NULL
                  AND lease_until >= @nowMs
                """,
                ct,
                ("@runId", runId),
                ("@claimed", AgentOrchestrationNodeRunStatus.Claimed.ToString()),
                ("@running", AgentOrchestrationNodeRunStatus.Running.ToString()),
                ("@nowMs", nowMs));

            if (activeCount < run.MaxConcurrency)
            {
                var ready = await ReadReadyNodeAsync(connection, transaction, runId, ct);
                if (ready is not null)
                {
                    var claimId = $"claim_{Guid.NewGuid():N}";
                    var attempt = ready.Value.Attempt + 1;
                    var fencingToken = ready.Value.FencingToken + 1;
                    var updated = await ExecuteAsync(
                        connection,
                        transaction,
                        """
                        UPDATE orchestration_node_runs
                        SET status = @claimed,
                            attempt = @attempt,
                            claim_id = @claimId,
                            lease_owner = @workerId,
                            lease_until = @leaseUntil,
                            fencing_token = @fencingToken,
                            execution_run_id = NULL,
                            sub_session_id = NULL,
                            output_summary = NULL,
                            artifact_reference = NULL,
                            error_message = NULL,
                            started_at = NULL,
                            completed_at = NULL,
                            updated_at = @updatedAt
                        WHERE run_id = @runId AND node_id = @nodeId AND status = @ready
                        """,
                        ct,
                        ("@claimed", AgentOrchestrationNodeRunStatus.Claimed.ToString()),
                        ("@attempt", attempt),
                        ("@claimId", claimId),
                        ("@workerId", workerId),
                        ("@leaseUntil", leaseUntilMs),
                        ("@fencingToken", fencingToken),
                        ("@updatedAt", nowMs),
                        ("@runId", runId),
                        ("@nodeId", ready.Value.NodeId),
                        ("@ready", AgentOrchestrationNodeRunStatus.Ready.ToString()));
                    if (updated != 1)
                        throw new InvalidOperationException($"Ready node '{ready.Value.NodeId}' claim compare-and-swap failed.");

                    await AppendEventAsync(
                        connection,
                        transaction,
                        run,
                        ++nextSequence,
                        AgentOrchestrationEventTypes.NodeClaimed,
                        ready.Value.NodeId,
                        null,
                        null,
                        $"Node claimed by worker '{workerId}'.",
                        null,
                        new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["claimId"] = claimId,
                            ["workerId"] = workerId,
                            ["attempt"] = attempt.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            ["fencingToken"] = fencingToken.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        },
                        nowMs,
                        ct);
                    mutationCount++;
                    claim = new AgentOrchestrationNodeClaim
                    {
                        RunId = runId,
                        NodeId = ready.Value.NodeId,
                        ClaimId = claimId,
                        WorkerId = workerId,
                        Attempt = attempt,
                        FencingToken = fencingToken,
                        LeaseExpiresAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(leaseUntilMs),
                        RunVersion = run.Version + 1
                    };
                }
            }

            if (mutationCount == 0)
            {
                await transaction.RollbackAsync(ct);
                return new AgentOrchestrationStoreResult<AgentOrchestrationNodeClaim>
                {
                    Status = AgentOrchestrationStoreStatus.NoWork,
                    ErrorCode = activeCount >= run.MaxConcurrency
                        ? "orchestration.concurrency_limit_reached"
                        : "orchestration.no_ready_node",
                    CurrentVersion = run.Version
                };
            }

            var newVersion = run.Version + 1;
            var runUpdated = await ExecuteAsync(
                connection,
                transaction,
                """
                UPDATE orchestration_runs
                SET version = @version, head_sequence = @headSequence, updated_at = @updatedAt
                WHERE run_id = @runId AND version = @expectedVersion AND status = @active
                """,
                ct,
                ("@version", newVersion),
                ("@headSequence", nextSequence),
                ("@updatedAt", nowMs),
                ("@runId", runId),
                ("@expectedVersion", run.Version),
                ("@active", AgentOrchestrationRunStatus.Active.ToString()));
            if (runUpdated != 1)
                throw new InvalidOperationException($"Run '{runId}' changed while committing a node claim.");

            committedHead = nextSequence;
            await transaction.CommitAsync(ct);
            if (claim is null)
            {
                earlyResult = new AgentOrchestrationStoreResult<AgentOrchestrationNodeClaim>
                {
                    Status = AgentOrchestrationStoreStatus.NoWork,
                    ErrorCode = "orchestration.no_ready_node_after_recovery",
                    CurrentVersion = newVersion
                };
            }
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }

        if (committedHead is not null)
            signal.Signal(runId, committedHead.Value);
        if (claim is not null)
        {
            logger.LogInformation(
                "[AgentOrchestrationStore] Claimed run={RunId} node={NodeId} claim={ClaimId} fence={Fence}",
                claim.RunId,
                claim.NodeId,
                claim.ClaimId,
                claim.FencingToken);
        }
        if (earlyResult is not null)
            return earlyResult;
        return new AgentOrchestrationStoreResult<AgentOrchestrationNodeClaim>
        {
            Status = AgentOrchestrationStoreStatus.Applied,
            Value = claim,
            CurrentVersion = claim!.RunVersion
        };
    }

    public async Task<AgentOrchestrationStoreResult<AgentOrchestrationNodeClaim>> RenewClaimAsync(
        AgentOrchestrationClaimRenewalRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.LeaseDuration <= TimeSpan.Zero)
            return Invalid<AgentOrchestrationNodeClaim>("orchestration.claim_lease_invalid", "LeaseDuration must be positive.");

        var nowMs = UtcNowMs();
        var leaseUntilMs = nowMs + (long)request.LeaseDuration.TotalMilliseconds;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        await connection.OpenAsync(ct);
        var updated = await ExecuteAsync(
            connection,
            null,
            """
            UPDATE orchestration_node_runs
            SET lease_until = @leaseUntil, updated_at = @updatedAt
            WHERE run_id = @runId AND node_id = @nodeId
              AND claim_id = @claimId AND lease_owner = @workerId
              AND fencing_token = @fencingToken
              AND status IN (@claimed, @running)
              AND lease_until IS NOT NULL AND lease_until >= @nowMs
            """,
            ct,
            ("@leaseUntil", leaseUntilMs),
            ("@updatedAt", nowMs),
            ("@runId", request.RunId),
            ("@nodeId", request.NodeId),
            ("@claimId", request.ClaimId),
            ("@workerId", request.WorkerId),
            ("@fencingToken", request.FencingToken),
            ("@claimed", AgentOrchestrationNodeRunStatus.Claimed.ToString()),
            ("@running", AgentOrchestrationNodeRunStatus.Running.ToString()),
            ("@nowMs", nowMs));
        if (updated != 1)
        {
            return Conflict<AgentOrchestrationNodeClaim>(
                "orchestration.claim_fence_rejected",
                "The claim is stale, expired, or no longer owns the node.",
                null);
        }

        var state = await ReadClaimProjectionAsync(connection, null, request.RunId, request.NodeId, ct)
            ?? throw new InvalidOperationException("Renewed claim projection could not be reloaded.");
        return new AgentOrchestrationStoreResult<AgentOrchestrationNodeClaim>
        {
            Status = AgentOrchestrationStoreStatus.Applied,
            Value = new AgentOrchestrationNodeClaim
            {
                RunId = request.RunId,
                NodeId = request.NodeId,
                ClaimId = request.ClaimId,
                WorkerId = request.WorkerId,
                Attempt = state.Attempt,
                FencingToken = request.FencingToken,
                LeaseExpiresAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(leaseUntilMs),
                RunVersion = state.RunVersion
            },
            CurrentVersion = state.RunVersion
        };
    }

    public Task<AgentOrchestrationStoreResult<AgentOrchestrationRunSnapshot>> MarkNodeRunningAsync(
        AgentOrchestrationNodeStartRequest request,
        CancellationToken ct = default)
        => CommitNodeMutationAsync(
            request.RunId,
            request.NodeId,
            request.ClaimId,
            request.WorkerId,
            request.FencingToken,
            AgentOrchestrationNodeRunStatus.Running,
            AgentOrchestrationEventTypes.NodeStarted,
            request.ExecutionRunId,
            request.SubSessionId,
            summary: "Node execution started.",
            artifactReference: null,
            errorMessage: null,
            succeeded: null,
            ct);

    public Task<AgentOrchestrationStoreResult<AgentOrchestrationRunSnapshot>> CommitNodeTerminalAsync(
        AgentOrchestrationNodeTerminalRequest request,
        CancellationToken ct = default)
        => CommitNodeMutationAsync(
            request.RunId,
            request.NodeId,
            request.ClaimId,
            request.WorkerId,
            request.FencingToken,
            request.Succeeded
                ? AgentOrchestrationNodeRunStatus.Completed
                : AgentOrchestrationNodeRunStatus.Failed,
            request.Succeeded
                ? AgentOrchestrationEventTypes.NodeCompleted
                : AgentOrchestrationEventTypes.NodeFailed,
            executionRunId: null,
            subSessionId: null,
            request.Summary,
            request.ArtifactReference,
            request.ErrorMessage,
            request.Succeeded,
            ct);

    private async Task<AgentOrchestrationStoreResult<AgentOrchestrationRunSnapshot>> CommitNodeMutationAsync(
        string runId,
        string nodeId,
        string claimId,
        string workerId,
        long fencingToken,
        AgentOrchestrationNodeRunStatus targetStatus,
        string eventType,
        string? executionRunId,
        string? subSessionId,
        string? summary,
        string? artifactReference,
        string? errorMessage,
        bool? succeeded,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(runId) || string.IsNullOrWhiteSpace(nodeId) ||
            string.IsNullOrWhiteSpace(claimId) || string.IsNullOrWhiteSpace(workerId))
        {
            return Invalid<AgentOrchestrationRunSnapshot>(
                "orchestration.node_mutation_invalid",
                "RunId, NodeId, ClaimId, and WorkerId are required.");
        }
        if (targetStatus == AgentOrchestrationNodeRunStatus.Running && string.IsNullOrWhiteSpace(executionRunId))
        {
            return Invalid<AgentOrchestrationRunSnapshot>(
                "orchestration.execution_run_id_required",
                "ExecutionRunId is required when a node starts running.");
        }

        var nowMs = UtcNowMs();
        long committedHead;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        await connection.OpenAsync(ct);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            ct);

        try
        {
            var run = await ReadRunRowAsync(connection, transaction, runId.Trim(), ct);
            if (run is null)
            {
                await transaction.RollbackAsync(ct);
                return NotFound<AgentOrchestrationRunSnapshot>("orchestration.run_not_found", $"Run '{runId}' was not found.");
            }
            if (ParseRunStatus(run.Status) != AgentOrchestrationRunStatus.Active)
            {
                await transaction.RollbackAsync(ct);
                return InvalidState<AgentOrchestrationRunSnapshot>(
                    "orchestration.run_not_active",
                    $"Run '{runId}' is not Active.",
                    run.Version);
            }

            var node = await ReadNodeClaimStateAsync(connection, transaction, runId, nodeId, ct);
            if (node is null)
            {
                await transaction.RollbackAsync(ct);
                return NotFound<AgentOrchestrationRunSnapshot>(
                    "orchestration.node_not_found",
                    $"Node '{nodeId}' was not found in run '{runId}'.");
            }

            var currentStatus = ParseNodeRunStatus(node.Value.Status);
            if (currentStatus == targetStatus &&
                IdEquals(node.Value.ClaimId, claimId) &&
                node.Value.FencingToken == fencingToken)
            {
                await transaction.RollbackAsync(ct);
                return new AgentOrchestrationStoreResult<AgentOrchestrationRunSnapshot>
                {
                    Status = AgentOrchestrationStoreStatus.Unchanged,
                    Value = await GetRunAsync(runId, ct),
                    CurrentVersion = run.Version
                };
            }

            var allowedStatus = targetStatus == AgentOrchestrationNodeRunStatus.Running
                ? currentStatus == AgentOrchestrationNodeRunStatus.Claimed
                : currentStatus is AgentOrchestrationNodeRunStatus.Claimed or AgentOrchestrationNodeRunStatus.Running;
            var ownsClaim = allowedStatus &&
                            IdEquals(node.Value.ClaimId, claimId) &&
                            IdEquals(node.Value.LeaseOwner, workerId) &&
                            node.Value.FencingToken == fencingToken &&
                            node.Value.LeaseUntil is not null &&
                            node.Value.LeaseUntil.Value >= nowMs;
            if (!ownsClaim)
            {
                await transaction.RollbackAsync(ct);
                return Conflict<AgentOrchestrationRunSnapshot>(
                    "orchestration.claim_fence_rejected",
                    "The claim is stale, expired, or no longer owns the node.",
                    run.Version);
            }

            var isTerminal = targetStatus is AgentOrchestrationNodeRunStatus.Completed or AgentOrchestrationNodeRunStatus.Failed;
            var updated = await ExecuteAsync(
                connection,
                transaction,
                isTerminal
                    ? """
                      UPDATE orchestration_node_runs
                      SET status = @status,
                          lease_until = NULL,
                          output_summary = @summary,
                          artifact_reference = @artifactReference,
                          error_message = @errorMessage,
                          completed_at = @completedAt,
                          updated_at = @updatedAt
                      WHERE run_id = @runId AND node_id = @nodeId
                        AND claim_id = @claimId AND lease_owner = @workerId
                        AND fencing_token = @fencingToken
                        AND status IN (@claimed, @running)
                        AND lease_until IS NOT NULL AND lease_until >= @nowMs
                      """
                    : """
                      UPDATE orchestration_node_runs
                      SET status = @status,
                          execution_run_id = @executionRunId,
                          sub_session_id = @subSessionId,
                          started_at = COALESCE(started_at, @startedAt),
                          updated_at = @updatedAt
                      WHERE run_id = @runId AND node_id = @nodeId
                        AND claim_id = @claimId AND lease_owner = @workerId
                        AND fencing_token = @fencingToken
                        AND status = @claimed
                        AND lease_until IS NOT NULL AND lease_until >= @nowMs
                      """,
                ct,
                ("@status", targetStatus.ToString()),
                ("@summary", summary),
                ("@artifactReference", artifactReference),
                ("@errorMessage", errorMessage),
                ("@completedAt", isTerminal ? nowMs : null),
                ("@executionRunId", executionRunId),
                ("@subSessionId", subSessionId),
                ("@startedAt", nowMs),
                ("@updatedAt", nowMs),
                ("@runId", runId.Trim()),
                ("@nodeId", nodeId.Trim()),
                ("@claimId", claimId.Trim()),
                ("@workerId", workerId.Trim()),
                ("@fencingToken", fencingToken),
                ("@claimed", AgentOrchestrationNodeRunStatus.Claimed.ToString()),
                ("@running", AgentOrchestrationNodeRunStatus.Running.ToString()),
                ("@nowMs", nowMs));
            if (updated != 1)
                throw new InvalidOperationException($"Node '{nodeId}' fenced mutation affected {updated} rows.");

            var eventExecutionRunId = executionRunId ?? node.Value.ExecutionRunId;
            var eventSubSessionId = subSessionId ?? node.Value.SubSessionId;
            committedHead = run.HeadSequence + 1;
            var attributes = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["claimId"] = claimId.Trim(),
                ["fencingToken"] = fencingToken.ToString(System.Globalization.CultureInfo.InvariantCulture)
            };
            if (succeeded is not null)
                attributes["succeeded"] = succeeded.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            await AppendEventAsync(
                connection,
                transaction,
                run,
                committedHead,
                eventType,
                nodeId.Trim(),
                eventExecutionRunId,
                eventSubSessionId,
                summary ?? errorMessage,
                artifactReference,
                attributes,
                nowMs,
                ct);

            var newVersion = run.Version + 1;
            var runUpdated = await ExecuteAsync(
                connection,
                transaction,
                """
                UPDATE orchestration_runs
                SET version = @version, head_sequence = @headSequence, updated_at = @updatedAt
                WHERE run_id = @runId AND version = @expectedVersion AND status = @active
                """,
                ct,
                ("@version", newVersion),
                ("@headSequence", committedHead),
                ("@updatedAt", nowMs),
                ("@runId", runId.Trim()),
                ("@expectedVersion", run.Version),
                ("@active", AgentOrchestrationRunStatus.Active.ToString()));
            if (runUpdated != 1)
                throw new InvalidOperationException($"Run '{runId}' changed while committing node '{nodeId}'.");

            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }

        signal.Signal(runId.Trim(), committedHead);
        var snapshot = await GetRunAsync(runId, ct)
            ?? throw new InvalidOperationException($"Updated run '{runId}' could not be reloaded.");
        return new AgentOrchestrationStoreResult<AgentOrchestrationRunSnapshot>
        {
            Status = AgentOrchestrationStoreStatus.Applied,
            Value = snapshot,
            CurrentVersion = snapshot.Version
        };
    }

    private async Task<int> ReclaimExpiredClaimsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RunRow run,
        long nowMs,
        Action<long> updateSequence,
        CancellationToken ct)
    {
        var expired = new List<ExpiredClaim>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT node_id, status, attempt, max_attempts, claim_id,
                       lease_owner, fencing_token, execution_run_id, sub_session_id
                FROM orchestration_node_runs
                WHERE run_id = @runId
                  AND status IN (@claimed, @running)
                  AND lease_until IS NOT NULL
                  AND lease_until < @nowMs
                ORDER BY node_id
                """;
            AddParameter(command, "@runId", run.RunId);
            AddParameter(command, "@claimed", AgentOrchestrationNodeRunStatus.Claimed.ToString());
            AddParameter(command, "@running", AgentOrchestrationNodeRunStatus.Running.ToString());
            AddParameter(command, "@nowMs", nowMs);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                expired.Add(new ExpiredClaim(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetInt32(2),
                    reader.GetInt32(3),
                    GetNullableString(reader, 4),
                    GetNullableString(reader, 5),
                    reader.GetInt64(6),
                    GetNullableString(reader, 7),
                    GetNullableString(reader, 8)));
            }
        }

        var sequence = run.HeadSequence;
        foreach (var item in expired)
        {
            var retryable = item.Attempt < item.MaxAttempts;
            var resultingStatus = retryable
                ? AgentOrchestrationNodeRunStatus.Ready
                : AgentOrchestrationNodeRunStatus.Failed;
            var updated = await ExecuteAsync(
                connection,
                transaction,
                """
                UPDATE orchestration_node_runs
                SET status = @status,
                    claim_id = NULL,
                    lease_owner = NULL,
                    lease_until = NULL,
                    execution_run_id = NULL,
                    sub_session_id = NULL,
                    error_message = @errorMessage,
                    completed_at = @completedAt,
                    updated_at = @updatedAt
                WHERE run_id = @runId AND node_id = @nodeId
                  AND fencing_token = @fencingToken
                  AND status IN (@claimed, @running)
                  AND lease_until IS NOT NULL AND lease_until < @nowMs
                """,
                ct,
                ("@status", resultingStatus.ToString()),
                ("@errorMessage", retryable ? null : "Claim lease expired and MaxAttempts was exhausted."),
                ("@completedAt", retryable ? null : nowMs),
                ("@updatedAt", nowMs),
                ("@runId", run.RunId),
                ("@nodeId", item.NodeId),
                ("@fencingToken", item.FencingToken),
                ("@claimed", AgentOrchestrationNodeRunStatus.Claimed.ToString()),
                ("@running", AgentOrchestrationNodeRunStatus.Running.ToString()),
                ("@nowMs", nowMs));
            if (updated != 1)
                throw new InvalidOperationException($"Expired claim for node '{item.NodeId}' changed during recovery.");

            await AppendEventAsync(
                connection,
                transaction,
                run,
                ++sequence,
                AgentOrchestrationEventTypes.NodeClaimExpired,
                item.NodeId,
                item.ExecutionRunId,
                item.SubSessionId,
                retryable
                    ? "Expired claim returned to Ready."
                    : "Expired claim exhausted MaxAttempts.",
                null,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["claimId"] = item.ClaimId ?? string.Empty,
                    ["leaseOwner"] = item.LeaseOwner ?? string.Empty,
                    ["attempt"] = item.Attempt.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["retryable"] = retryable.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["resultingStatus"] = resultingStatus.ToString()
                },
                nowMs,
                ct);
        }

        updateSequence(sequence);
        return expired.Count;
    }

    private static async Task AppendEventAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RunRow run,
        long sequence,
        string eventType,
        string? nodeId,
        string? executionRunId,
        string? subSessionId,
        string? summary,
        string? artifactReference,
        IReadOnlyDictionary<string, string> attributes,
        long nowMs,
        CancellationToken ct)
    {
        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO orchestration_run_events
            (event_id, run_id, graph_id, revision_id, sequence, event_type,
             node_id, execution_run_id, sub_session_id, summary,
             artifact_reference, attributes_json, recorded_at)
            VALUES
            (@eventId, @runId, @graphId, @revisionId, @sequence, @eventType,
             @nodeId, @executionRunId, @subSessionId, @summary,
             @artifactReference, @attributesJson, @recordedAt)
            """,
            ct,
            ("@eventId", $"orevt_{Guid.NewGuid():N}"),
            ("@runId", run.RunId),
            ("@graphId", run.GraphId),
            ("@revisionId", run.RevisionId),
            ("@sequence", sequence),
            ("@eventType", eventType),
            ("@nodeId", nodeId),
            ("@executionRunId", executionRunId),
            ("@subSessionId", subSessionId),
            ("@summary", summary),
            ("@artifactReference", artifactReference),
            ("@attributesJson", JsonSerializer.Serialize(attributes, JsonOptions)),
            ("@recordedAt", nowMs));
    }

    private static AgentOrchestrationGraphLayout NormalizeLayout(AgentOrchestrationGraphLayout layout)
        => layout with
        {
            GraphId = layout.GraphId?.Trim() ?? string.Empty,
            BaseRevisionId = layout.BaseRevisionId?.Trim() ?? string.Empty,
            Viewport = layout.Viewport ?? new AgentOrchestrationViewport(),
            Nodes = (layout.Nodes ?? Array.Empty<AgentOrchestrationNodeLayout>())
                .Select(node => node with
                {
                    NodeId = node.NodeId?.Trim() ?? string.Empty,
                    ParentNodeId = string.IsNullOrWhiteSpace(node.ParentNodeId)
                        ? null
                        : node.ParentNodeId.Trim()
                })
                .ToArray()
        };

    private static (string Code, string Message)? ValidateLayoutShape(
        AgentOrchestrationGraphLayout layout,
        int expectedCurrentLayoutRevision)
    {
        if (string.IsNullOrWhiteSpace(layout.GraphId) || string.IsNullOrWhiteSpace(layout.BaseRevisionId))
            return ("orchestration.layout_identity_required", "GraphId and BaseRevisionId are required.");
        if (expectedCurrentLayoutRevision < 0 || layout.LayoutRevision < 1)
            return ("orchestration.layout_revision_invalid", "Layout revisions must be non-negative and LayoutRevision must be at least 1.");
        if (!double.IsFinite(layout.Viewport.X) ||
            !double.IsFinite(layout.Viewport.Y) ||
            !double.IsFinite(layout.Viewport.Zoom) ||
            layout.Viewport.Zoom is < 0.05 or > 8)
        {
            return ("orchestration.layout_viewport_invalid", "Viewport coordinates must be finite and zoom must be between 0.05 and 8.");
        }

        var nodeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in layout.Nodes)
        {
            if (string.IsNullOrWhiteSpace(node.NodeId) || !nodeIds.Add(node.NodeId))
                return ("orchestration.layout_node_duplicate", "Layout node IDs must be non-empty and unique.");
            if (!double.IsFinite(node.X) || !double.IsFinite(node.Y))
                return ("orchestration.layout_node_position_invalid", $"Layout node '{node.NodeId}' has a non-finite position.");
            if (node.Width is { } width && (!double.IsFinite(width) || width <= 0) ||
                node.Height is { } height && (!double.IsFinite(height) || height <= 0))
            {
                return ("orchestration.layout_node_size_invalid", $"Layout node '{node.NodeId}' has an invalid size.");
            }
            if (IdEquals(node.NodeId, node.ParentNodeId))
                return ("orchestration.layout_parent_self_reference", $"Layout node '{node.NodeId}' cannot parent itself.");
        }

        var parents = layout.Nodes
            .Where(node => node.ParentNodeId is not null)
            .ToDictionary(node => node.NodeId, node => node.ParentNodeId!, StringComparer.OrdinalIgnoreCase);
        foreach (var node in layout.Nodes)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { node.NodeId };
            var current = node.NodeId;
            while (parents.TryGetValue(current, out var parent))
            {
                if (!seen.Add(parent))
                    return ("orchestration.layout_parent_cycle", $"Layout parent hierarchy contains a cycle at '{parent}'.");
                current = parent;
            }
        }

        return null;
    }

    private static async Task<(int LayoutRevision, string LayoutJson)?> ReadLayoutIdentityAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string graphId,
        string baseRevisionId,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT layout_revision, layout_json
            FROM orchestration_graph_layouts
            WHERE graph_id = @graphId AND base_revision_id = @baseRevisionId
            """;
        AddParameter(command, "@graphId", graphId);
        AddParameter(command, "@baseRevisionId", baseRevisionId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? (reader.GetInt32(0), reader.GetString(1))
            : null;
    }

    private static async Task<AgentOrchestrationGraphDefinition?> ReadDefinitionByRevisionAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string revisionId,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT definition_json FROM orchestration_graph_revisions WHERE revision_id = @revisionId";
        AddParameter(command, "@revisionId", revisionId);
        return DeserializeDefinition(await command.ExecuteScalarAsync(ct) as string);
    }

    private static AgentOrchestrationGraphDefinition? DeserializeDefinition(string? json)
        => string.IsNullOrWhiteSpace(json)
            ? null
            : JsonSerializer.Deserialize<AgentOrchestrationGraphDefinition>(json, JsonOptions)
              ?? throw new InvalidDataException("Stored orchestration graph definition is null after deserialization.");

    private static async Task<(string ContentHash, int Revision)?> ReadRevisionIdentityAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string revisionId,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT content_hash, revision FROM orchestration_graph_revisions WHERE revision_id = @revisionId";
        AddParameter(command, "@revisionId", revisionId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? (reader.GetString(0), reader.GetInt32(1))
            : null;
    }

    private static async Task<(int Revision, string RevisionId)?> ReadGraphHeadAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string graphId,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT current_revision, current_revision_id FROM orchestration_graphs WHERE graph_id = @graphId";
        AddParameter(command, "@graphId", graphId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? (reader.GetInt32(0), reader.GetString(1))
            : null;
    }

    private static async Task<RunRow?> ReadRunRowAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string runId,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT run_id, graph_id, revision_id, workspace_id, root_session_id,
                   requested_by_agent_id, status, version, head_sequence, max_concurrency,
                   created_at, activated_at, updated_at, completed_at, error_message
            FROM orchestration_runs
            WHERE run_id = @runId
            """;
        AddParameter(command, "@runId", runId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;
        return new RunRow(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetInt64(7),
            reader.GetInt64(8),
            reader.GetInt32(9),
            reader.GetInt64(10),
            GetNullableInt64(reader, 11),
            reader.GetInt64(12),
            GetNullableInt64(reader, 13),
            GetNullableString(reader, 14));
    }

    private static async Task<(string NodeId, int Attempt, long FencingToken)?> ReadReadyNodeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT node_id, attempt, fencing_token
            FROM orchestration_node_runs
            WHERE run_id = @runId AND status = @ready AND attempt < max_attempts
            ORDER BY node_id
            LIMIT 1
            """;
        AddParameter(command, "@runId", runId);
        AddParameter(command, "@ready", AgentOrchestrationNodeRunStatus.Ready.ToString());
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? (reader.GetString(0), reader.GetInt32(1), reader.GetInt64(2))
            : null;
    }

    private static async Task<NodeClaimState?> ReadNodeClaimStateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId,
        string nodeId,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT status, claim_id, lease_owner, lease_until, fencing_token,
                   execution_run_id, sub_session_id
            FROM orchestration_node_runs
            WHERE run_id = @runId AND node_id = @nodeId
            """;
        AddParameter(command, "@runId", runId.Trim());
        AddParameter(command, "@nodeId", nodeId.Trim());
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;
        return new NodeClaimState(
            reader.GetString(0),
            GetNullableString(reader, 1),
            GetNullableString(reader, 2),
            GetNullableInt64(reader, 3),
            reader.GetInt64(4),
            GetNullableString(reader, 5),
            GetNullableString(reader, 6));
    }

    private static async Task<(int Attempt, long RunVersion)?> ReadClaimProjectionAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string runId,
        string nodeId,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT n.attempt, r.version
            FROM orchestration_node_runs n
            JOIN orchestration_runs r ON r.run_id = n.run_id
            WHERE n.run_id = @runId AND n.node_id = @nodeId
            """;
        AddParameter(command, "@runId", runId);
        AddParameter(command, "@nodeId", nodeId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? (reader.GetInt32(0), reader.GetInt64(1))
            : null;
    }

    private static AgentOrchestrationRunSnapshot ToSnapshot(
        RunRow run,
        IReadOnlyList<AgentOrchestrationNodeRunSnapshot> nodes)
        => new()
        {
            RunId = run.RunId,
            GraphId = run.GraphId,
            RevisionId = run.RevisionId,
            WorkspaceId = run.WorkspaceId,
            RootSessionId = run.RootSessionId,
            RequestedByAgentId = run.RequestedByAgentId,
            Status = ParseRunStatus(run.Status),
            Version = run.Version,
            HeadSequence = run.HeadSequence,
            MaxConcurrency = run.MaxConcurrency,
            Nodes = nodes,
            CreatedAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(run.CreatedAt),
            ActivatedAtUtc = FromUnixMsNullable(run.ActivatedAt),
            UpdatedAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(run.UpdatedAt),
            CompletedAtUtc = FromUnixMsNullable(run.CompletedAt),
            ErrorMessage = run.ErrorMessage
        };

    private static async Task<int> ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        CancellationToken ct,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters)
            AddParameter(command, parameter.Name, parameter.Value);
        return await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<long> ExecuteScalarInt64Async(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken ct,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters)
            AddParameter(command, parameter.Name, parameter.Value);
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct));
    }

    private static void AddParameter(SqliteCommand command, string name, object? value)
        => command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private long UtcNowMs() => timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

    private static IReadOnlyDictionary<string, string> EmptyAttributes()
        => new Dictionary<string, string>(StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, string> DeserializeAttributes(string json)
        => JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOptions)
           ?? new Dictionary<string, string>(StringComparer.Ordinal);

    private static AgentOrchestrationRunStatus ParseRunStatus(string value)
        => Enum.TryParse<AgentOrchestrationRunStatus>(value, ignoreCase: true, out var parsed)
            ? parsed
            : throw new InvalidDataException($"Unknown orchestration run status '{value}'.");

    private static AgentOrchestrationNodeRunStatus ParseNodeRunStatus(string value)
        => Enum.TryParse<AgentOrchestrationNodeRunStatus>(value, ignoreCase: true, out var parsed)
            ? parsed
            : throw new InvalidDataException($"Unknown orchestration node status '{value}'.");

    private static AgentOrchestrationNodeKind ParseNodeKind(string value)
        => Enum.TryParse<AgentOrchestrationNodeKind>(value, ignoreCase: true, out var parsed)
            ? parsed
            : throw new InvalidDataException($"Unknown orchestration node kind '{value}'.");

    private static string? GetNullableString(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static long? GetNullableInt64(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    private static DateTimeOffset? FromUnixMsNullable(long? value)
        => value is null ? null : DateTimeOffset.FromUnixTimeMilliseconds(value.Value);

    private static bool IdEquals(string? left, string? right)
        => !string.IsNullOrWhiteSpace(left) &&
           !string.IsNullOrWhiteSpace(right) &&
           string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

    private static AgentOrchestrationStoreResult<T> Invalid<T>(string code, string message)
        where T : class
        => new()
        {
            Status = AgentOrchestrationStoreStatus.InvalidState,
            ErrorCode = code,
            ErrorMessage = message
        };

    private static AgentOrchestrationStoreResult<T> InvalidState<T>(
        string code,
        string message,
        long? currentVersion)
        where T : class
        => new()
        {
            Status = AgentOrchestrationStoreStatus.InvalidState,
            ErrorCode = code,
            ErrorMessage = message,
            CurrentVersion = currentVersion
        };

    private static AgentOrchestrationStoreResult<T> Conflict<T>(
        string code,
        string message,
        long? currentVersion)
        where T : class
        => new()
        {
            Status = AgentOrchestrationStoreStatus.Conflict,
            ErrorCode = code,
            ErrorMessage = message,
            CurrentVersion = currentVersion
        };

    private static AgentOrchestrationStoreResult<T> NotFound<T>(string code, string message)
        where T : class
        => new()
        {
            Status = AgentOrchestrationStoreStatus.NotFound,
            ErrorCode = code,
            ErrorMessage = message
        };

    private sealed record RunRow(
        string RunId,
        string GraphId,
        string RevisionId,
        string WorkspaceId,
        string RootSessionId,
        string RequestedByAgentId,
        string Status,
        long Version,
        long HeadSequence,
        int MaxConcurrency,
        long CreatedAt,
        long? ActivatedAt,
        long UpdatedAt,
        long? CompletedAt,
        string? ErrorMessage);

    private readonly record struct ExpiredClaim(
        string NodeId,
        string Status,
        int Attempt,
        int MaxAttempts,
        string? ClaimId,
        string? LeaseOwner,
        long FencingToken,
        string? ExecutionRunId,
        string? SubSessionId);

    private readonly record struct NodeClaimState(
        string Status,
        string? ClaimId,
        string? LeaseOwner,
        long? LeaseUntil,
        long FencingToken,
        string? ExecutionRunId,
        string? SubSessionId);
}
