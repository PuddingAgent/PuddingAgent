using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PuddingConversationReadModel;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Services.AgentChat;

/// <summary>
/// 读模型持久化适配（阶段 C2）：在调用方持有的 <see cref="PlatformDbContext"/> 与事务内
/// 加载/写入 <c>conversation_turn_summaries</c>、<c>conversation_read_state</c> 与
/// <c>conversation_projection_checkpoints</c>。
/// <para>
/// 归约规则在纯组件；本类不执行 SaveChanges/Commit，也不做全量事件重放。加载只取本批受影响的
/// Turn 摘要，避免为每个 200 事件批次读取整个会话历史。
/// </para>
/// </summary>
public sealed class ConversationReadModelProjectionStore(ILogger<ConversationReadModelProjectionStore> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// 为下一批归约加载状态。旧库没有 read_state 时：
    /// checkpoint == 0 视为新会话（Ready）；checkpoint &gt; 0 说明已有旧投影但没有摘要基线，按 Partial 处理。
    /// </summary>
    public async Task<ConversationReadModelState> LoadForBatchAsync(
        PlatformDbContext db,
        string conversationId,
        long projectedThrough,
        IReadOnlyCollection<string> affectedTurnIds,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);

        var readState = await db.ConversationReadStates
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.ConversationId == conversationId, ct);

        if (readState is null)
        {
            var initialization = projectedThrough == 0
                ? ReadModelInitializationState.Ready
                : ReadModelInitializationState.Partial;
            if (initialization == ReadModelInitializationState.Partial)
            {
                logger.LogInformation(
                    "[ConversationReadModel] Legacy projection without read state conv={ConversationId} checkpoint={Checkpoint}; loading partial baseline",
                    conversationId,
                    projectedThrough);
            }

            return ConversationReadModelReducer.CreateEmpty(conversationId, initialization) with
            {
                ProjectedThroughSequence = projectedThrough,
            };
        }

        var turnIds = affectedTurnIds
            .Where(turnId => !string.IsNullOrWhiteSpace(turnId))
            .ToHashSet(StringComparer.Ordinal);
        // 事件缺少 TurnId 时归约器会回退到 currentTurnId；把当前 Turn 一并载入，避免把它重建为 partial 摘要。
        if (!string.IsNullOrWhiteSpace(readState.CurrentTurnId))
            turnIds.Add(readState.CurrentTurnId);
        var turnIdArray = turnIds.ToArray();
        var turnRows = turnIdArray.Length == 0
            ? new List<ConversationTurnSummaryEntity>()
            : await db.ConversationTurnSummaries
                .AsNoTracking()
                .Where(row => row.ConversationId == conversationId && turnIdArray.Contains(row.TurnId))
                .ToListAsync(ct);

        var turns = new Dictionary<string, TurnReadSummary>(StringComparer.Ordinal);
        foreach (var row in turnRows)
            turns[row.TurnId] = MapTurnRow(row);

        return new ConversationReadModelState
        {
            ConversationId = conversationId,
            ProjectedThroughSequence = projectedThrough,
            ProjectedThroughEventId = null,
            Initialization = (ReadModelInitializationState)readState.InitializationState,
            Turns = turns,
            CurrentTurnId = EmptyToNull(readState.CurrentTurnId),
            CurrentRootRunId = EmptyToNull(readState.CurrentRootRunId),
            CurrentRootTerminalState = (TurnTerminalState)readState.CurrentRootTerminalState,
            Compaction = MapCompaction(readState),
            SubAgentRuns = DeserializeSubAgentRuns(readState.SubAgentRunsJson, conversationId),
            SubAgentRunsOmitted = readState.SubAgentRunsOmitted,
            ReadModelVersion = readState.ReadModelVersion,
            Revision = readState.Revision,
        };
    }

    /// <summary>
    /// 将归约后的状态写入读模型行，并推进同一事务内的 projection checkpoint。
    /// 调用方负责 <c>SaveChangesAsync</c> 与事务提交/回滚。
    /// </summary>
    public async Task PersistAsync(
        PlatformDbContext db,
        ConversationReadModelState state,
        IReadOnlyCollection<string> touchedTurnIds,
        long checkpointSequence,
        DateTimeOffset now,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(state.ConversationId);

        var nowText = now.ToString("O");
        var readState = await db.ConversationReadStates
            .SingleOrDefaultAsync(item => item.ConversationId == state.ConversationId, ct);
        if (readState is null)
        {
            readState = new ConversationReadStateEntity { ConversationId = state.ConversationId };
            db.ConversationReadStates.Add(readState);
        }

        readState.InitializationState = (int)state.Initialization;
        readState.CurrentTurnId = state.CurrentTurnId;
        readState.CurrentRootRunId = state.CurrentRootRunId;
        readState.CurrentRootTerminalState = (int)state.CurrentRootTerminalState;
        readState.CompactionRunning = state.Compaction?.IsRunning ?? false;
        readState.CompactionRunId = state.Compaction?.RunId;
        readState.CompactionStartedSequence = state.Compaction?.StartedSequence ?? 0;
        readState.CompactionCompletedSequence = state.Compaction?.CompletedSequence;
        readState.CompactionLastAttemptFailed = state.Compaction?.LastAttemptFailed ?? false;
        readState.SubAgentRunsJson = SerializeSubAgentRuns(state.SubAgentRuns);
        readState.SubAgentRunsOmitted = state.SubAgentRunsOmitted;
        readState.ReadModelVersion = state.ReadModelVersion;
        readState.Revision = state.Revision;
        readState.UpdatedAt = nowText;

        var touched = touchedTurnIds
            .Where(turnId => !string.IsNullOrWhiteSpace(turnId))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (touched.Length > 0)
        {
            var existingRows = await db.ConversationTurnSummaries
                .Where(row => row.ConversationId == state.ConversationId && touched.Contains(row.TurnId))
                .ToDictionaryAsync(row => row.TurnId, StringComparer.Ordinal, ct);

            foreach (var turnId in touched)
            {
                if (!state.Turns.TryGetValue(turnId, out var turn))
                    continue;

                if (!existingRows.TryGetValue(turnId, out var row))
                {
                    row = new ConversationTurnSummaryEntity
                    {
                        ConversationId = state.ConversationId,
                        TurnId = turnId,
                    };
                    db.ConversationTurnSummaries.Add(row);
                }

                MapTurn(turn, row, nowText);
            }
        }

        var checkpoint = await db.ConversationProjectionCheckpoints
            .SingleOrDefaultAsync(item => item.ConversationId == state.ConversationId, ct);
        if (checkpoint is null)
        {
            checkpoint = new ConversationProjectionCheckpointEntity
            {
                ConversationId = state.ConversationId,
            };
            db.ConversationProjectionCheckpoints.Add(checkpoint);
        }

        checkpoint.ProjectedThrough = checkpointSequence;
        checkpoint.UpdatedAt = nowText;
    }

    internal static TurnReadSummary MapTurnRow(ConversationTurnSummaryEntity row) => new()
    {
        TurnId = row.TurnId,
        RootRunId = EmptyToNull(row.RootRunId),
        UserMessageId = EmptyToNull(row.UserMessageId),
        AssistantMessageId = EmptyToNull(row.AssistantMessageId),
        TerminalState = (TurnTerminalState)row.TerminalState,
        HasAssistantBody = row.HasAssistantBody,
        Process = new ProcessSummary
        {
            ContentAppendCount = row.ContentAppendCount,
            ThinkingAppendCount = row.ThinkingAppendCount,
            ToolCallRequestedCount = row.ToolCallRequestedCount,
            ToolCallCompletedCount = row.ToolCallCompletedCount,
            ToolCallFailedCount = row.ToolCallFailedCount,
            SubAgentRunCount = row.SubAgentRunCount,
            Completeness = (SummaryCompleteness)row.ProcessCompleteness,
            IncompleteReason = EmptyToNull(row.IncompleteReason),
            Revision = row.SummaryRevision,
        },
        ActivePreview = row.ActivePreview,
        ActivePreviewTruncated = row.ActivePreviewTruncated,
        FirstSequence = row.FirstSequence,
        LastSequence = row.LastSequence,
        Revision = row.SummaryRevision,
    };

    internal static void MapTurn(TurnReadSummary turn, ConversationTurnSummaryEntity row, string nowText)
    {
        row.RootRunId = turn.RootRunId;
        row.UserMessageId = turn.UserMessageId;
        row.AssistantMessageId = turn.AssistantMessageId;
        row.TerminalState = (int)turn.TerminalState;
        row.HasAssistantBody = turn.HasAssistantBody;
        row.ContentAppendCount = turn.Process.ContentAppendCount;
        row.ThinkingAppendCount = turn.Process.ThinkingAppendCount;
        row.ToolCallRequestedCount = turn.Process.ToolCallRequestedCount;
        row.ToolCallCompletedCount = turn.Process.ToolCallCompletedCount;
        row.ToolCallFailedCount = turn.Process.ToolCallFailedCount;
        row.SubAgentRunCount = turn.Process.SubAgentRunCount;
        row.ActivePreview = turn.ActivePreview;
        row.ActivePreviewTruncated = turn.ActivePreviewTruncated;
        row.FirstSequence = turn.FirstSequence;
        row.LastSequence = turn.LastSequence;
        row.SummaryRevision = turn.Revision;
        row.ProcessCompleteness = (int)turn.Process.Completeness;
        row.IncompleteReason = turn.Process.IncompleteReason;
        row.ReadModelVersion = ConversationReadModelLimits.CurrentReadModelVersion;
        row.UpdatedAt = nowText;
    }

    internal static CompactionReadState? MapCompaction(ConversationReadStateEntity row)
    {
        var hasState = row.CompactionRunning
            || row.CompactionStartedSequence > 0
            || row.CompactionCompletedSequence.HasValue
            || row.CompactionLastAttemptFailed
            || !string.IsNullOrWhiteSpace(row.CompactionRunId);
        if (!hasState)
            return null;

        return new CompactionReadState
        {
            IsRunning = row.CompactionRunning,
            RunId = EmptyToNull(row.CompactionRunId),
            StartedSequence = row.CompactionStartedSequence,
            CompletedSequence = row.CompactionCompletedSequence,
            LastAttemptFailed = row.CompactionLastAttemptFailed,
        };
    }

    internal static string SerializeSubAgentRuns(IReadOnlyList<SubAgentRunSummary> runs)
    {
        if (runs.Count == 0)
            return "[]";

        var rows = runs.Select(run => new StoredSubAgentRun(
            run.RunId,
            run.SubAgentId,
            run.ParentRunId,
            run.TurnId,
            (int)run.State,
            run.FirstSequence,
            run.LastSequence)).ToArray();
        return JsonSerializer.Serialize(rows, JsonOptions);
    }

    internal static IReadOnlyList<SubAgentRunSummary> DeserializeSubAgentRuns(
        string? json,
        string conversationId)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "[]")
            return Array.Empty<SubAgentRunSummary>();

        try
        {
            var rows = JsonSerializer.Deserialize<StoredSubAgentRun[]>(json, JsonOptions);
            if (rows is null)
                return Array.Empty<SubAgentRunSummary>();

            return rows
                .Where(row => !string.IsNullOrWhiteSpace(row.RunId))
                .Select(row => new SubAgentRunSummary
                {
                    RunId = row.RunId!,
                    SubAgentId = row.SubAgentId,
                    ParentRunId = row.ParentRunId,
                    TurnId = row.TurnId,
                    State = (SubAgentRunLifecycleState)row.State,
                    FirstSequence = row.FirstSequence,
                    LastSequence = row.LastSequence,
                })
                .ToArray();
        }
        catch (JsonException ex)
        {
            // 读模型持久化损坏时 fail closed：不伪造摘要，按空摘要返回并保留明确日志。
            throw new InvalidOperationException(
                $"Corrupted conversation_read_state.sub_agent_runs_json for conversation '{conversationId}'.",
                ex);
        }
    }

    private static string? EmptyToNull(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;

    private sealed record StoredSubAgentRun(
        string? RunId,
        string? SubAgentId,
        string? ParentRunId,
        string? TurnId,
        int State,
        long FirstSequence,
        long LastSequence);
}
