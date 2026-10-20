using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using PuddingCode.Platform;
using PuddingConversationReadModel;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Services.AgentChat;

/// <summary>
/// 统一会话打开/历史/过程分页的有界只读查询（阶段 C3；Plan §5.1–§5.3）。
/// <para>
/// 一次打开只做：最近消息页（键集/有界预览）、受影响 Turn 摘要读取、read_state、checkpoint/head
/// 的一致性读取与 ETag 计算。禁止在 GET 中重放事件、补建摘要、修复运行状态；历史/过程页各自有界。
/// </para>
/// </summary>
public sealed class ConversationOpenQueryService(
    IDbContextFactory<PlatformDbContext> dbFactory,
    ILogger<ConversationOpenQueryService> logger)
{
    private const int PreviewChars = 2048;
    private const int ProcessPayloadFragmentChars = 64 * 1024;
    private const int DefaultRetryAfterMs = 500;
    private const int ContentSegmentDefaultBytes = 32 * 1024;
    private const int ContentSegmentMaxBytes = 128 * 1024;
    private const int ProcessStructuredFieldMaxBytes = 16 * 1024;

    // Normalize the wire envelope inside SQLite before applying preview/byte
    // budgets. Fetching/truncating raw JSON first loses both body and identity.
    private const string EnvelopeSql = """
        CASE WHEN json_valid(Content) THEN
            CASE WHEN json_extract(Content, '$.schema') = 'pudding-message'
                 AND json_type(Content, '$.context.text') = 'text'
            THEN Content END END
        """;
    private static readonly string DisplayContentSql =
        $"COALESCE(json_extract(({EnvelopeSql}), '$.context.text'), Content)";

    private static readonly string[] DisplayableProcessTypes =
    [
        ConversationEventTypes.MessageContentAppended,
        ConversationEventTypes.MessageThinkingSummaryAppended,
        ConversationEventTypes.ToolCallRequested,
        ConversationEventTypes.ToolCallCompleted,
        ConversationEventTypes.ToolCallFailed,
        ConversationEventTypes.SubAgentRunCreated,
        ConversationEventTypes.SubAgentRunStarted,
        ConversationEventTypes.SubAgentRunCompleted,
        ConversationEventTypes.SubAgentRunFailed,
        ConversationEventTypes.SubAgentRunCancelled,
        ConversationEventTypes.SubAgentRunTimedOut,
        ConversationEventTypes.SubAgentRunInterrupted,
        ConversationEventTypes.SubAgentRunBudgetExhausted,
    ];

    /// <summary>Explicit error-details expansion: one terminal row by conversation + turn.</summary>
    public async Task<JsonElement?> GetTurnDiagnosticAsync(
        string conversationId, string turnId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(turnId);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var payload = await db.ConversationEvents.AsNoTracking()
            .Where(e => e.ConversationId == conversationId && e.TurnId == turnId
                && e.Type == ConversationEventTypes.TurnFailed)
            .OrderByDescending(e => e.Sequence)
            .Select(e => e.Payload.Substring(0, 256 * 1024))
            .FirstOrDefaultAsync(ct);
        if (payload is null) return null;
        try
        {
            using var document = JsonDocument.Parse(payload);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new ConversationQueryException("diagnostic_unavailable", "保存的错误诊断不完整，无法读取。");
        }
    }

    public async Task<ConversationOpenSnapshot> OpenAsync(
        string conversationId,
        int? messageLimit = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);

        var limits = ConversationReadModelLimits.Default;
        var limit = limits.NormalizeOpenMessageLimit(messageLimit);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var checkpoint = await ReadCheckpointAsync(db, conversationId, ct);
        var eventHead = await ReadEventHeadAsync(db, conversationId, checkpoint, ct);
        var readState = await db.ConversationReadStates
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.ConversationId == conversationId, ct);

        var history = await ReadHistoryPageCoreAsync(db, conversationId, null, null, limit, limits, ct);

        var turnIds = history.Items
            .Select(item => item.TurnId)
            .Where(turnId => !string.IsNullOrWhiteSpace(turnId))
            .ToHashSet(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(readState?.CurrentTurnId))
            turnIds.Add(readState!.CurrentTurnId!);

        var turnSummaries = await ReadTurnSummariesAsync(db, conversationId, turnIds, ct);
        var visibleTurns = history.Items
            .Select(item => item.TurnId)
            .Where(turnId => !string.IsNullOrWhiteSpace(turnId))
            .Select(turnId => turnSummaries.GetValueOrDefault(turnId!))
            .Where(turn => turn is not null)
            .Cast<ConversationTurnSummaryView>()
            .DistinctBy(turn => turn.TurnId, StringComparer.Ordinal)
            .OrderByDescending(turn => turn.LastSequence)
            .ToArray();

        var activeTurn = !string.IsNullOrWhiteSpace(readState?.CurrentTurnId)
                         && turnSummaries.TryGetValue(readState!.CurrentTurnId!, out var current)
            ? current
            : null;

        var initialization = readState is null
            ? (checkpoint == 0 ? ReadModelInitializationState.Ready : ReadModelInitializationState.Partial)
            : (ReadModelInitializationState)readState.InitializationState;
        var readModelVersion = readState?.ReadModelVersion
                               ?? ConversationReadModelLimits.CurrentReadModelVersion;
        var revision = readState?.Revision ?? 0;
        var runtime = BuildRuntime(readState, conversationId, initialization);

        var syncing = checkpoint == 0 && eventHead > 0;
        var completeness = ResolveCompleteness(initialization, visibleTurns, history.Items.Count);

        var representationTag = ComputeRepresentationTag(
            conversationId, checkpoint, eventHead, readModelVersion, revision, limit, initialization);

        await transaction.CommitAsync(ct);

        logger.LogDebug(
            "[ConversationOpen] open conv={ConversationId} C={Cursor} H={Head} messages={Count} init={Initialization} sync={Sync}",
            conversationId, checkpoint, eventHead, history.Items.Count, initialization, syncing);

        return new ConversationOpenSnapshot(
            conversationId,
            checkpoint,
            eventHead,
            initialization,
            syncing ? ConversationSyncState.Syncing : ConversationSyncState.Ready,
            syncing ? DefaultRetryAfterMs : null,
            representationTag,
            history,
            activeTurn,
            visibleTurns,
            runtime,
            completeness.Completeness,
            completeness.Reason);
    }

    public async Task<ConversationHistoryPage> GetHistoryPageAsync(
        ConversationHistoryQuery query,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentException.ThrowIfNullOrWhiteSpace(query.ConversationId);

        var limits = ConversationReadModelLimits.Default;
        var limit = limits.NormalizeOpenMessageLimit(query.Limit);
        var hasCursor = query.BeforeCreatedAt.HasValue || !string.IsNullOrWhiteSpace(query.BeforeMessageId);
        if (hasCursor && (!query.BeforeCreatedAt.HasValue || string.IsNullOrWhiteSpace(query.BeforeMessageId)))
        {
            throw new ConversationQueryException(
                "invalid_cursor",
                "beforeCreatedAt and beforeMessageId must be provided together.");
        }

        if (hasCursor)
        {
            var cursor = new HistoryPageCursor(
                query.ConversationId,
                DateTimeOffset.FromUnixTimeMilliseconds(query.BeforeCreatedAt!.Value),
                query.BeforeMessageId!,
                ConversationReadModelLimits.HistoryOrderVersion,
                limit);
            var failure = PageCursorContract.ValidateHistory(
                cursor,
                query.ConversationId,
                ConversationReadModelLimits.HistoryOrderVersion,
                limits);
            if (failure != PageCursorFailure.None)
            {
                throw new ConversationQueryException(
                    "invalid_cursor",
                    $"history cursor rejected: {failure}");
            }
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await ReadHistoryPageCoreAsync(
            db,
            query.ConversationId,
            query.BeforeCreatedAt,
            query.BeforeMessageId,
            limit,
            limits,
            ct);
    }

    public async Task<ConversationProcessPage> GetProcessPageAsync(
        ConversationProcessQuery query,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentException.ThrowIfNullOrWhiteSpace(query.ConversationId);

        var limits = ConversationReadModelLimits.Default;
        var limit = limits.NormalizeDetailItemLimit(query.Limit);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var eventHead = await ReadEventHeadAsync(db, query.ConversationId, 0, ct);
        var through = query.ThroughSequence ?? eventHead;
        var before = query.BeforeSequence ?? (through + 1);

        var cursor = new ProcessPageCursor(
            query.ConversationId,
            query.TurnId,
            before,
            through,
            limit);
        var failure = PageCursorContract.ValidateProcess(
            cursor,
            query.ConversationId,
            query.TurnId,
            eventHead,
            limits);
        if (failure != PageCursorFailure.None)
        {
            throw new ConversationQueryException(
                "invalid_cursor",
                $"process cursor rejected: {failure}");
        }

        var (candidates, structuredFields) = await ReadProcessCandidatesAsync(
            db, query.ConversationId, query.TurnId, through, before, limits.DetailPageCandidateScanLimit, ct);
        var windowTruncated = candidates.Count >= limits.DetailPageCandidateScanLimit;
        candidates.Reverse();

        var bounded = ProcessPageBudgeter.Bound(candidates, limit, limits);
        // 空展示页也必须有可前进的扫描边界：即使候选窗口耗尽，也返回最低扫描 sequence 供调用方推进一次。
        var nextBefore = bounded.NextBeforeSequence
                         ?? (candidates.Count > 0 ? candidates[0].Sequence : null);
        var hasMoreBefore = bounded.HasMoreBefore || windowTruncated;

        return new ConversationProcessPage(
            bounded.Items
                .Select(item =>
                {
                    structuredFields.TryGetValue(item.EventId, out var fields);
                    return new ConversationProcessItem(
                        item.Sequence,
                        item.EventId,
                        item.Kind,
                        item.Preview,
                        item.PreviewTruncated,
                        item.OriginalPayloadBytes,
                        fields?.ToolCallId,
                        fields?.Name,
                        fields?.Status,
                        fields?.ExitCode,
                        fields?.Error,
                        fields?.SubAgentId,
                        fields?.RunId,
                        fields?.Arguments,
                        fields?.Output);
                })
                .ToArray(),
            nextBefore,
            hasMoreBefore,
            bounded.ScannedBackThroughSequence,
            bounded.OmittedNonDisplayableCount,
            bounded.Truncation.ToString());
    }

    /// <summary>
    /// 单条消息正文有界分段读取（Plan §5.4）：只按 UTF-8 字节窗口读取 <c>Content</c>，
    /// 不把超大正文整段加载进内存；短消息一次返回全文。
    /// </summary>
    public async Task<ConversationMessageContentPage> GetMessageContentAsync(
        string conversationId,
        string messageId,
        long? offsetBytes = null,
        int? maxBytes = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);

        var offset = offsetBytes ?? 0;
        if (offset < 0)
            throw new ConversationQueryException("invalid_content_offset", "offsetBytes must be >= 0.");
        var limit = Math.Clamp(maxBytes ?? ContentSegmentDefaultBytes, 1, ContentSegmentMaxBytes);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(ct);

        var total = await ReadMessageContentLengthAsync(connection, conversationId, messageId, ct);
        if (total < 0)
            throw new ConversationQueryException("message_not_found", "Message does not exist in this conversation.");
        if (offset > total)
            throw new ConversationQueryException("invalid_content_offset", "offsetBytes is beyond message length.");
        if (offset == total)
        {
            return new ConversationMessageContentPage(
                conversationId, messageId, offset, null, total, string.Empty, false);
        }

        var segmentLength = (int)Math.Min(limit, total - offset);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT substr(CAST(({DisplayContentSql}) AS BLOB), $startByte, $length)
            FROM ChatMessages
            WHERE SessionId = $sessionId AND message_id = $messageId
            LIMIT 1;
            """;
        AddParameter(command, "$startByte", offset + 1);
        AddParameter(command, "$length", segmentLength);
        AddParameter(command, "$sessionId", conversationId);
        AddParameter(command, "$messageId", messageId);

        var raw = await command.ExecuteScalarAsync(ct);
        var bytes = raw as byte[] ?? [];
        var (text, bytesUsed) = DecodeUtf8Prefix(bytes);
        if (bytesUsed <= 0 && bytes.Length > 0)
            throw new ConversationQueryException("invalid_content_offset", "offsetBytes is not a UTF-8 boundary.");
        var nextOffset = offset + bytesUsed < total ? offset + bytesUsed : (long?)null;
        return new ConversationMessageContentPage(
            conversationId,
            messageId,
            offset,
            nextOffset,
            total,
            text,
            nextOffset.HasValue);
    }

    // ── 最近消息页 ──────────────────────────────────────────────────────

    private static async Task<ConversationHistoryPage> ReadHistoryPageCoreAsync(
        PlatformDbContext db,
        string conversationId,
        long? beforeCreatedAt,
        string? beforeMessageId,
        int limit,
        ConversationReadModelLimits limits,
        CancellationToken ct)
    {
        IQueryable<ChatMessageEntity> query = db.ChatMessages
            .AsNoTracking()
            .Where(message => message.SessionId == conversationId)
            .Where(message => message.Content != string.Empty)
            .Where(message => message.Content != RuntimeDispatchMarkers.DuplicateMessagePlaceholder)
            .Where(message => message.Content != RuntimeDispatchMarkers.DuplicateMessagePlaceholderLegacyHyphen);

        if (beforeCreatedAt.HasValue)
        {
            var beforeId = await db.ChatMessages
                .AsNoTracking()
                .Where(message => message.SessionId == conversationId && message.MessageId == beforeMessageId)
                .Select(message => new { message.Id, message.CreatedAt })
                .SingleOrDefaultAsync(ct);
            if (beforeId is null || beforeId.CreatedAt != beforeCreatedAt.Value)
            {
                throw new ConversationQueryException(
                    "cursor_target_missing",
                    "history cursor boundary no longer exists for this conversation.");
            }

            var boundaryCreatedAt = beforeCreatedAt.Value;
            var boundaryId = beforeId.Id;
            query = query.Where(message =>
                message.CreatedAt < boundaryCreatedAt
                || (message.CreatedAt == boundaryCreatedAt && message.Id < boundaryId));
        }

        var ids = await query
            .OrderByDescending(message => message.CreatedAt)
            .ThenByDescending(message => message.Id)
            .Take(limit + 1)
            .Select(message => message.Id)
            .ToListAsync(ct);
        var rows = await ReadHistoryRowsAsync(db, ids, ct);

        var candidates = rows
            .Select(row => new HistoryPageCandidate
            {
                MessageId = row.MessageId,
                Role = row.Role,
                Content = row.Preview,
                CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(row.CreatedAt),
                TurnId = row.TurnId,
                IsDisplayable = true,
            })
            .ToArray();

        var bounded = HistoryPageBudgeter.Bound(candidates, limit, limits);
        var rowsById = rows.ToDictionary(row => row.MessageId, StringComparer.Ordinal);
        var items = new List<ConversationHistoryItem>(bounded.Items.Count);
        foreach (var item in bounded.Items)
        {
            var row = rowsById[item.MessageId];
            var hasMoreContent = row.ContentLength > Encoding.UTF8.GetByteCount(item.Preview);
            items.Add(new ConversationHistoryItem(
                item.MessageId,
                item.Role,
                item.TurnId,
                row.CreatedAt,
                item.Preview,
                hasMoreContent,
                row.ContentLength,
                row.HasContentParts)
            {
                SourceKind = row.SourceKind,
                SourceId = row.SourceId,
                SourceName = row.SourceName,
                MessageType = row.MessageType,
                LlmRole = row.LlmRole,
                ContentParts = row.ContentParts,
            });
        }

        long? nextCreatedAt = null;
        string? nextMessageId = null;
        if (items.Count > 0)
        {
            nextCreatedAt = items[^1].CreatedAt;
            nextMessageId = items[^1].MessageId;
        }

        var hasMore = bounded.HasMore || rows.Count > items.Count;
        return new ConversationHistoryPage(items, nextCreatedAt, nextMessageId, hasMore);
    }

    private static async Task<List<HistoryRow>> ReadHistoryRowsAsync(
        PlatformDbContext db, IReadOnlyList<long> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return [];
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        var parameters = ids.Select((_, i) => $"$id{i}").ToArray();
        for (var i = 0; i < ids.Count; i++) AddParameter(command, parameters[i], ids[i]);
        AddParameter(command, "$previewChars", PreviewChars);
        command.CommandText = $"""
            SELECT message_id, Role, turn_id, CreatedAt,
                   length(CAST(({DisplayContentSql}) AS BLOB)),
                   substr(({DisplayContentSql}), 1, $previewChars),
                   content_parts_json IS NOT NULL,
                   substr(json_extract(({EnvelopeSql}), '$.from.kind'), 1, 32),
                   substr(json_extract(({EnvelopeSql}), '$.from.id'), 1, 256),
                   substr(json_extract(({EnvelopeSql}), '$.from.display_name'), 1, 256),
                   substr(json_extract(({EnvelopeSql}), '$.message_type'), 1, 64),
                   (SELECT json_group_array(json_object('type', 'image',
                       'artifactId', substr(json_extract(value, '$.artifactId'), 1, 512),
                       'detail', substr(json_extract(value, '$.detail'), 1, 32)))
                    FROM (SELECT value FROM json_each(
                        CASE WHEN json_valid(content_parts_json) THEN content_parts_json ELSE 'null' END,
                        '$.parts') WHERE json_extract(value, '$.type') = 'image' LIMIT 32))
            FROM ChatMessages WHERE Id IN ({string.Join(",", parameters)})
            ORDER BY CreatedAt DESC, Id DESC;
            """;
        var rows = new List<HistoryRow>(ids.Count);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            string? Optional(int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
            var llmRole = reader.GetString(1);
            var sourceKind = Optional(7);
            var role = sourceKind is "system" or "agent" or "user" ? sourceKind : llmRole;
            rows.Add(new HistoryRow(reader.GetString(0), role, Optional(2), reader.GetInt64(3),
                reader.GetInt32(4), reader.GetString(5), reader.GetBoolean(6), sourceKind,
                Optional(8), Optional(9), Optional(10), llmRole,
                JsonSerializer.Deserialize<ConversationContentPartView[]>(reader.GetString(11),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web))));
        }
        return rows;
    }

    // ── 过程明细页 ──────────────────────────────────────────────────────

    private static async Task<(
        List<ProcessItemCandidate> Candidates,
        Dictionary<string, ProcessStructuredFields> Structured)> ReadProcessCandidatesAsync(
        PlatformDbContext db,
        string conversationId,
        string? turnId,
        long throughSequence,
        long beforeSequence,
        int scanLimit,
        CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(ct);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT sequence, event_id, type,
                   substr(payload, 1, $fragmentChars) AS payload_fragment,
                   length(payload) AS payload_length
            FROM conversation_events
            WHERE conversation_id = $conversationId
              AND sequence <= $throughSequence
              AND sequence < $beforeSequence
              AND ($turnId IS NULL OR turn_id = $turnId)
            ORDER BY sequence DESC
            LIMIT $scanLimit;
            """;
        AddParameter(command, "$fragmentChars", ProcessPayloadFragmentChars);
        AddParameter(command, "$conversationId", conversationId);
        AddParameter(command, "$throughSequence", throughSequence);
        AddParameter(command, "$beforeSequence", beforeSequence);
        AddParameter(command, "$turnId", turnId is null ? DBNull.Value : turnId);
        AddParameter(command, "$scanLimit", scanLimit);

        var candidates = new List<ProcessItemCandidate>(Math.Min(scanLimit, 128));
        var structured = new Dictionary<string, ProcessStructuredFields>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var sequence = reader.GetInt64(0);
            var eventId = reader.GetString(1);
            var type = reader.GetString(2);
            var payloadFragment = reader.IsDBNull(3) ? string.Empty : reader.GetString(3);
            var payloadLength = reader.IsDBNull(4) ? 0 : Convert.ToInt32(reader.GetInt64(4));
            var fragmentTruncated = payloadLength > ProcessPayloadFragmentChars;

            var displayable = IsDisplayableProcessType(type);
            var payload = displayable
                ? ExtractProcessPayload(type, payloadFragment, fragmentTruncated)
                : new ProcessPayloadResult(string.Empty, EmptyStructuredFields);

            candidates.Add(new ProcessItemCandidate
            {
                Sequence = sequence,
                EventId = eventId,
                Kind = type,
                Preview = payload.Preview,
                PayloadBytes = payloadLength,
                IsDisplayable = displayable,
            });
            if (displayable)
                structured[eventId] = payload.Fields;
        }

        return (candidates, structured);
    }

    private static bool IsDisplayableProcessType(string type)
        => DisplayableProcessTypes.Any(candidate => string.Equals(candidate, type, StringComparison.Ordinal));

    private static ProcessPayloadResult ExtractProcessPayload(
        string type,
        string payloadFragment,
        bool fragmentTruncated)
    {
        JsonDocument? document = null;
        try
        {
            document = JsonDocument.Parse(payloadFragment);
        }
        catch (JsonException)
        {
            // 巨型 payload 的有界片段不是完整 JSON：不把它解析成“完整事实”，只返回有界片段并显式标记截断。
            return new ProcessPayloadResult(
                fragmentTruncated ? payloadFragment : string.Empty,
                EmptyStructuredFields);
        }

        using (document)
        {
            var payload = document.RootElement;
            var preview = type switch
            {
                ConversationEventTypes.MessageContentAppended => ReadString(payload, "delta") ?? ReadString(payload, "text"),
                ConversationEventTypes.MessageThinkingSummaryAppended => ReadString(payload, "delta"),
                ConversationEventTypes.ToolCallRequested => JoinParts(
                    ReadString(payload, "name"), ReadString(payload, "arguments")),
                ConversationEventTypes.ToolCallCompleted or ConversationEventTypes.ToolCallFailed
                    => JoinParts(ReadString(payload, "name"), ReadString(payload, "output"), ReadString(payload, "error")),
                _ => JoinParts(
                    ReadString(payload, "status"),
                    ReadString(payload, "sub_agent_id") ?? ReadString(payload, "subAgentId")),
            };

            var status = ReadString(payload, "status");
            if (string.IsNullOrWhiteSpace(status))
            {
                status = type switch
                {
                    ConversationEventTypes.ToolCallRequested => "requested",
                    ConversationEventTypes.ToolCallCompleted => "completed",
                    ConversationEventTypes.ToolCallFailed => "failed",
                    _ => null,
                };
            }

            var fields = new ProcessStructuredFields
            {
                ToolCallId = ReadString(payload, "toolCallId") ?? ReadString(payload, "tool_call_id"),
                Name = ReadString(payload, "name"),
                Status = status,
                ExitCode = ReadInt(payload, "exitCode") ?? ReadInt(payload, "exit_code"),
                Error = BoundUtf8(
                    ReadString(payload, "error") ?? ReadString(payload, "errorMessage")),
                SubAgentId = ReadString(payload, "sub_agent_id") ?? ReadString(payload, "subAgentId"),
                RunId = ReadString(payload, "runId") ?? ReadString(payload, "run_id"),
                Arguments = BoundUtf8(ReadString(payload, "arguments")),
                Output = BoundUtf8(ReadString(payload, "output")),
            };
            return new ProcessPayloadResult(preview ?? string.Empty, fields);
        }
    }

    private static int? ReadInt(JsonElement payload, string propertyName)
    {
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var parsed) => parsed,
            JsonValueKind.String when int.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null,
        };
    }

    private static string? BoundUtf8(string? value)
    {
        if (string.IsNullOrEmpty(value) || Encoding.UTF8.GetByteCount(value) <= ProcessStructuredFieldMaxBytes)
            return value;

        var chars = value.ToCharArray();
        var bytes = new byte[ProcessStructuredFieldMaxBytes];
        var encoder = Encoding.UTF8.GetEncoder();
        encoder.Convert(chars, 0, chars.Length, bytes, 0, bytes.Length, flush: true,
            out var charsUsed, out _, out _);
        return new string(chars, 0, charsUsed);
    }

    private static async Task<long> ReadMessageContentLengthAsync(
        DbConnection connection,
        string conversationId,
        string messageId,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT length(CAST(({DisplayContentSql}) AS BLOB))
            FROM ChatMessages
            WHERE SessionId = $sessionId AND message_id = $messageId
            LIMIT 1;
            """;
        AddParameter(command, "$sessionId", conversationId);
        AddParameter(command, "$messageId", messageId);
        var raw = await command.ExecuteScalarAsync(ct);
        return raw is null or DBNull ? -1 : Convert.ToInt64(raw);
    }

    /// <summary>只解码完整 UTF-8 前缀；不完整的尾字节留给下一页，绝不产生替换字符。</summary>
    private static (string Text, int BytesUsed) DecodeUtf8Prefix(byte[] bytes)
    {
        if (bytes.Length == 0)
            return (string.Empty, 0);
        if ((bytes[0] & 0xC0) == 0x80)
            return (string.Empty, 0);

        // 从尾部回退到最后一个 lead byte，判断该码点是否被截断。
        var lead = bytes.Length - 1;
        while (lead >= 0 && (bytes[lead] & 0xC0) == 0x80)
            lead--;
        if (lead < 0)
            return (string.Empty, 0);

        var required = bytes[lead] switch
        {
            < 0x80 => 1,
            >= 0xC0 and < 0xE0 => 2,
            >= 0xE0 and < 0xF0 => 3,
            >= 0xF0 and < 0xF8 => 4,
            _ => 0,
        };
        var end = required == 0 || lead + required > bytes.Length
            ? lead
            : bytes.Length;
        if (end <= 0)
            return (string.Empty, 0);

        return (Encoding.UTF8.GetString(bytes, 0, end), end);
    }

    private static readonly ProcessStructuredFields EmptyStructuredFields = new();

    private static string? JoinParts(params string?[] parts)
    {
        var nonEmpty = parts
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .ToArray();
        return nonEmpty.Length == 0 ? null : string.Join(" ", nonEmpty);
    }

    private static string? ReadString(JsonElement payload, string propertyName)
    {
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
    }

    // ── 读模型/游标/一致性 ─────────────────────────────────────────────

    private static async Task<long> ReadCheckpointAsync(
        PlatformDbContext db,
        string conversationId,
        CancellationToken ct)
    {
        var value = await db.ConversationProjectionCheckpoints
            .AsNoTracking()
            .Where(checkpoint => checkpoint.ConversationId == conversationId)
            .Select(checkpoint => (long?)checkpoint.ProjectedThrough)
            .FirstOrDefaultAsync(ct);
        return value ?? 0L;
    }

    private static async Task<long> ReadEventHeadAsync(
        PlatformDbContext db,
        string conversationId,
        long checkpoint,
        CancellationToken ct)
    {
        var head = await db.ConversationHeads
            .AsNoTracking()
            .Where(item => item.ConversationId == conversationId)
            .Select(item => (long?)item.HeadSequence)
            .FirstOrDefaultAsync(ct);

        var eventHead = Math.Max(head ?? 0L, checkpoint);
        if (eventHead == 0)
        {
            var maxSequence = await db.ConversationEvents
                .AsNoTracking()
                .Where(item => item.ConversationId == conversationId)
                .Select(item => (long?)item.Sequence)
                .MaxAsync(ct);
            eventHead = maxSequence ?? 0L;
        }

        return eventHead;
    }

    private static async Task<Dictionary<string, ConversationTurnSummaryView>> ReadTurnSummariesAsync(
        PlatformDbContext db,
        string conversationId,
        IReadOnlyCollection<string> turnIds,
        CancellationToken ct)
    {
        if (turnIds.Count == 0)
            return new Dictionary<string, ConversationTurnSummaryView>(StringComparer.Ordinal);

        var ids = turnIds.ToArray();
        var rows = await db.ConversationTurnSummaries
            .AsNoTracking()
            .Where(row => row.ConversationId == conversationId && ids.Contains(row.TurnId))
            .ToListAsync(ct);

        var result = new Dictionary<string, ConversationTurnSummaryView>(StringComparer.Ordinal);
        foreach (var row in rows)
            result[row.TurnId] = MapTurnSummary(ConversationReadModelProjectionStore.MapTurnRow(row));
        return result;
    }

    private static ConversationTurnSummaryView MapTurnSummary(TurnReadSummary summary) => new(
        summary.TurnId,
        summary.RootRunId,
        summary.UserMessageId,
        summary.AssistantMessageId,
        summary.TerminalState,
        summary.HasAssistantBody,
        summary.Process,
        summary.ActivePreview,
        summary.ActivePreviewTruncated,
        summary.FirstSequence,
        summary.LastSequence,
        summary.Revision);

    private static ConversationRuntimeView BuildRuntime(
        ConversationReadStateEntity? readState,
        string conversationId,
        ReadModelInitializationState initialization)
    {
        if (readState is null)
        {
            return new ConversationRuntimeView(
                false,
                null,
                false,
                Array.Empty<ConversationSubAgentSummaryView>(),
                0,
                initialization,
                ConversationReadModelLimits.CurrentReadModelVersion,
                0);
        }

        var compaction = ConversationReadModelProjectionStore.MapCompaction(readState);
        var subAgentRuns = ConversationReadModelProjectionStore
            .DeserializeSubAgentRuns(readState.SubAgentRunsJson, conversationId)
            .Select(run => new ConversationSubAgentSummaryView(
                run.RunId,
                run.SubAgentId,
                run.ParentRunId,
                run.TurnId,
                run.State,
                run.FirstSequence,
                run.LastSequence))
            .ToArray();

        return new ConversationRuntimeView(
            compaction?.IsRunning ?? false,
            compaction?.RunId,
            compaction?.LastAttemptFailed ?? false,
            subAgentRuns,
            readState.SubAgentRunsOmitted,
            (ReadModelInitializationState)readState.InitializationState,
            readState.ReadModelVersion,
            readState.Revision);
    }

    private static (SummaryCompleteness Completeness, string? Reason) ResolveCompleteness(
        ReadModelInitializationState initialization,
        IReadOnlyList<ConversationTurnSummaryView> visibleTurns,
        int messageCount)
    {
        if (initialization != ReadModelInitializationState.Ready)
            return (SummaryCompleteness.Partial, "baseline_incomplete");

        if (visibleTurns.Count > 0)
        {
            var worst = visibleTurns
                .OrderBy(turn => (int)turn.Process.Completeness)
                .First();
            return worst.Process.Completeness == SummaryCompleteness.Complete
                ? (SummaryCompleteness.Complete, null)
                : (worst.Process.Completeness,
                    worst.Process.IncompleteReason ?? "read_model_partial");
        }

        return messageCount > 0
            ? (SummaryCompleteness.Partial, "read_model_baseline_missing")
            : (SummaryCompleteness.Complete, null);
    }

    private static string ComputeRepresentationTag(
        string conversationId,
        long snapshotCursor,
        long eventHead,
        int readModelVersion,
        int revision,
        int messageLimit,
        ReadModelInitializationState initialization)
    {
        var material = string.Join(
            '|',
            conversationId,
            snapshotCursor,
            eventHead,
            readModelVersion,
            revision,
            messageLimit,
            (int)initialization);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return Convert.ToHexString(hash)[..16];
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private sealed record HistoryRow(
        string MessageId,
        string Role,
        string? TurnId,
        long CreatedAt,
        int ContentLength,
        string Preview,
        bool HasContentParts,
        string? SourceKind,
        string? SourceId,
        string? SourceName,
        string? MessageType,
        string LlmRole,
        IReadOnlyList<ConversationContentPartView>? ContentParts);

    private sealed class ProcessStructuredFields
    {
        public string? ToolCallId { get; init; }
        public string? Name { get; init; }
        public string? Status { get; init; }
        public int? ExitCode { get; init; }
        public string? Error { get; init; }
        public string? SubAgentId { get; init; }
        public string? RunId { get; init; }
        public string? Arguments { get; init; }
        public string? Output { get; init; }
    }

    private sealed record ProcessPayloadResult(string Preview, ProcessStructuredFields Fields);
}
