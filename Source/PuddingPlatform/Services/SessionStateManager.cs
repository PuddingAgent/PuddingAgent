using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;
using PuddingCode.Observability;
using PuddingCode.Platform;
using PuddingCode.Services;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Services;

/// <summary>
/// SessionStateManager — 会话事件系统的核心实现（Singleton）。
/// 
/// 实现接口：ISessionStateManager（兼容 Facade）、ISessionEventWriter、
///           ISessionEventReader、ISessionHeadNotifier。
/// 
/// 三大职责：
///   1. 持久化事件日志 — session_event_log 表，append-only 不可变记录。
///      a. 正常路径：先 SQLite → 后 JSONL → 后 Channel fan-out
///      b. 批量路径：先缓冲（最多 32 条或 150ms）→ flush SQLite → 后 Channel fan-out
///      c. 两条路径均保证 persist-before-notify（ADR-056 P0 正确性不变量）
///   2. 实时通知通道 — 双通道架构：
///      a. SessionChannelFanout（旧）：完整 ServerSentEventFrame，用于兼容
///      b. HeadNotificationChannel（新）：轻量 SessionHeadAdvanced，只携带 committed sequence
///         DropOldest 在此模式下安全（丢失唤醒 = 下次通知自动补齐）
///   3. 子代理状态追踪 — session_sub_agents 表
/// 
/// 关键设计决策：
///   - 使用 IDbContextFactory 解决 Singleton 与 Scoped DbContext 的冲突
///   - 序号分配与 SaveChangesAsync 在同一 per-session SemaphoreSlim 临界区（ADR-028）
///   - ISessionStateManager 保留为兼容 Facade，内部逐步迁移到 ISessionEventWriter/Reader 端口
/// 
/// 关联 ADR：ADR-056（可靠事件流）、ADR-028（序号原子性）
/// </summary>
public sealed class SessionStateManager : ISessionStateManager, ISessionEventWriter, ISessionEventReader, ISessionHeadNotifier
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SessionStateManager> _logger;
    private readonly IRuntimeActivitySink _activitySink;
    private readonly IRuntimeTraceAccessor _traceAccessor;
    private readonly JsonlSessionWriter _jsonlWriter;
        private readonly SessionStateStore _stateStore;
    private readonly AgentRawLogMirrorService? _rawLogMirror;

    // 会话实时订阅（sessionId → fan-out hub）。每个订阅者拥有独立 Channel，避免多连接竞争消费同一队列。
    private readonly ConcurrentDictionary<string, SessionChannelFanout> _sessionChannels = new();

    // Head 通知专用 Channel（sessionId → Channel<SessionHeadAdvanced>）。
    // 仅携带 committedThroughSequence，SSE 收到后从数据库读取实际事件。支持 DropOldest 无风险。
    private readonly ConcurrentDictionary<string, Channel<SessionHeadAdvanced>> _headNotificationChannels = new();

    // Agent 流式输出的短帧批量持久化缓冲。实时推送先走 fan-out，SQLite/JSONL 在后台按批次落盘。
    private readonly ConcurrentDictionary<string, SessionEventBatchBuffer> _streamBatchBuffers = new();

    // 工作区通知 Channel（workspaceId → Channel）
    private readonly ConcurrentDictionary<string, Channel<SessionNotification>> _workspaceChannels = new();

    // 会话状态追踪
    private readonly ConcurrentDictionary<string, SessionState> _sessionStates = new();

    // 序列号生成锁（per session SemaphoreSlim，保证递增 + SaveChangesAsync 在同一临界区）
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _seqLocks = new();
    private readonly ConcurrentDictionary<string, SessionSequenceState> _sequenceStates = new();

    // Channel TTL 倒计时
    private static readonly TimeSpan ChannelTtl = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan StreamBatchFlushDelay = TimeSpan.FromMilliseconds(150);
    private const int StreamBatchFlushThreshold = 32;
    private const string SubAgentDispatcherFailureSummary = "Sub-agent dispatcher failed before completion.";

        public SessionStateManager(
        IServiceScopeFactory scopeFactory,
        ILogger<SessionStateManager> logger,
        IRuntimeActivitySink activitySink,
        IRuntimeTraceAccessor traceAccessor,
        JsonlSessionWriter jsonlWriter,
        SessionStateStore stateStore,
        AgentRawLogMirrorService? rawLogMirror = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _activitySink = activitySink;
        _traceAccessor = traceAccessor;
        _jsonlWriter = jsonlWriter;
        _stateStore = stateStore;
        _rawLogMirror = rawLogMirror;
    }

    /// <summary>
    /// 启动恢复时注入会话状态（仅 SessionStateStore.LoadFromDisk 恢复流程调用）。
    /// 不触发事件，不写入持久化。
    /// </summary>
    public void Restore(string sessionId, SessionState state)
    {
        _sessionStates[sessionId] = state;
        _logger?.LogInformation(
            "[SSM] State restored session={Session} state={State}",
            sessionId, state);
    }

    private async Task<T> UseDbAsync<T>(Func<PlatformDbContext, Task<T>> action)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        return await action(db);
    }

    private async Task UseDbAsync(Func<PlatformDbContext, Task> action)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        await action(db);
    }

    private static long ElapsedMilliseconds(long startedAt)
        => (long)((Stopwatch.GetTimestamp() - startedAt) * 1000.0 / Stopwatch.Frequency);

    private static string InjectSequenceNum(string data, long seq)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            using var stream = new MemoryStream();
            using var writer = new Utf8JsonWriter(stream);
            writer.WriteStartObject();
            writer.WriteNumber("sequenceNum", seq);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.NameEquals("sequenceNum")) continue;
                prop.WriteTo(writer);
            }
            writer.WriteEndObject();
            writer.Flush();
            return Encoding.UTF8.GetString(stream.ToArray());
        }
        catch
        {
            return data;
        }
    }

    // ════════════════════════════════════════════════════════
    // 事件追加
    // ════════════════════════════════════════════════════════

    /// <summary>
    /// 向会话事件日志追加一帧。返回全局递增序列号。
    /// 
    /// 双写一致性策略（ARCH-SESSION-002）：
    ///   1. 先写 SQLite（主存储，对查询最重要）— 失败则抛异常，不尝试 JSONL
    ///   2. 再写 JSONL（fire-and-forget 备份）— 失败仅记录 Warning，不影响 SQLite 侧成功
    ///   3. 推送到内存 Channel（实时推送）
    /// 
    /// 关联 ADR：Docs/07架构/20会话状态机与事件规范ADR.md §6
    /// </summary>
    public async Task<long> AppendAsync(
        string sessionId, string workspaceId,
        ServerSentEventFrame frame,
        CancellationToken ct = default,
        RuntimeTraceContext? trace = null,
        string? component = null,
        string? operation = null)
    {
        var recordedAt = DateTimeOffset.UtcNow.ToString("O");
        var effectiveTrace = trace
            ?? _traceAccessor.Current?.WithSession(sessionId, workspaceId)
            ?? RuntimeTraceContext.CreateNew(sessionId: sessionId, workspaceId: workspaceId);
        var effectiveComponent = component ?? RuntimeActivityComponents.SessionState;
        var effectiveOperation = operation ?? $"append:{frame.Event}";
        var appendStartedAt = Stopwatch.GetTimestamp();
        long sqliteMs = 0;
        long jsonlMs = 0;
        long fanoutMs = 0;
        long markCompleteMs = 0;
        var hasLiveSubscribers = _sessionChannels.TryGetValue(sessionId, out var existingHub)
            && existingHub.SubscriberCount > 0;

        if (hasLiveSubscribers && IsRealtimeBatchable(frame, effectiveComponent))
        {
            return await AppendRealtimeBufferedAsync(
                sessionId,
                workspaceId,
                frame,
                recordedAt,
                effectiveTrace,
                effectiveComponent,
                effectiveOperation,
                ct);
        }

        await FlushPendingSessionEventsAsync(sessionId, ct);

        // 1. 持久化到 SQLite（主存储，失败抛异常，不继续 JSONL）
        //    ADR-028: 序号分配与 SaveChangesAsync 必须在同一 per-session 异步临界区
        var sqliteStartedAt = Stopwatch.GetTimestamp();
        var seq = await AppendSqliteEventWithRetryAsync(
            sessionId, workspaceId, frame, recordedAt,
            effectiveTrace, effectiveComponent, effectiveOperation, ct);
        sqliteMs = ElapsedMilliseconds(sqliteStartedAt);

        _logger.LogDebug("[SSM] Append frame session={Session} type={EventType} seq={Seq}", sessionId, frame.Event, seq);
        await MirrorRawEventAsync(
            sessionId,
            workspaceId,
            frame,
            recordedAt,
            seq,
            effectiveTrace,
            effectiveComponent,
            effectiveOperation,
            ct);

        // 2. 写入 JSONL（fire-and-forget 备份，失败不阻断主路径）
        //    JSONL 仅作为文本备份，SQLite 是权威数据源。
        //    JSONL 写入失败仅记录 Warning，不影响调用方感知的成功结果。
        try
        {
            var jsonlStartedAt = Stopwatch.GetTimestamp();
            _jsonlWriter.WriteEventLine(sessionId, frame.Event, frame.Data, seq, recordedAt);
            jsonlMs = ElapsedMilliseconds(jsonlStartedAt);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[SSM] JSONL write failed (non-fatal) session={Session} type={EventType} seq={Seq}",
                sessionId, frame.Event, seq);
        }

        // 3. 推送到内存 Channel（实时推送）
        var fanoutStartedAt = Stopwatch.GetTimestamp();
        var channelCreated = false;
        var hub = _sessionChannels.GetOrAdd(sessionId, _ =>
        {
            channelCreated = true;
            _logger.LogInformation(
                "[SSM] Channel created for append session={Session} activeChannels={ActiveChannels}",
                sessionId, _sessionChannels.Count + 1);
            return new SessionChannelFanout();
        });

        var pushed =         hub.Publish(frame with { Data = InjectSequenceNum(frame.Data, seq) });
        fanoutMs = ElapsedMilliseconds(fanoutStartedAt);

        // Head notification: lightweight signal carrying only committedThroughSequence.
        if (_headNotificationChannels.TryGetValue(sessionId, out var headChannel))
            headChannel.Writer.TryWrite(new SessionHeadAdvanced(sessionId, seq));

        _logger.LogInformation(
            "[SSM] Channel fanout session={Session} type={EventType} seq={Seq} subscribers={SubscriberCount} ok={Ok} created={Created} dataChars={DataChars} activeChannels={ActiveChannels}",
            sessionId, frame.Event, seq, hub.SubscriberCount, pushed, channelCreated, frame.Data.Length, _sessionChannels.Count);

        // 4. 如有 done/error/cancelled 帧 → 标记流式完成
        if (frame.Event is "done" or "error" or "cancelled")
        {
            var markCompleteStartedAt = Stopwatch.GetTimestamp();
            var isErr = frame.Event == "error";
            _logger.LogDebug("[SSM] Stream complete session={Session} event={Event} error={IsErr}", sessionId, frame.Event, isErr);
            await MarkStreamCompleteAsync(sessionId, ct);
            markCompleteMs = ElapsedMilliseconds(markCompleteStartedAt);
        }

        var appendDurationMs = ElapsedMilliseconds(appendStartedAt);

        await _activitySink.RecordAsync(new RuntimeActivity
        {
            Trace = effectiveTrace,
            Component = RuntimeActivityComponents.SessionState,
            Operation = effectiveOperation,
            Status = RuntimeActivityStatuses.Succeeded,
            StartedAtUtc = DateTimeOffset.Parse(recordedAt),
            EndedAtUtc = DateTimeOffset.UtcNow,
            DurationMs = appendDurationMs,
            Summary = $"Appended session event {frame.Event}",
            Metadata = new Dictionary<string, string>
            {
                ["sequence"] = seq.ToString(),
                ["eventType"] = frame.Event,
                ["sourceComponent"] = effectiveComponent,
                ["sqlite_ms"] = sqliteMs.ToString(),
                ["jsonl_ms"] = jsonlMs.ToString(),
                ["fanout_ms"] = fanoutMs.ToString(),
                ["mark_complete_ms"] = markCompleteMs.ToString(),
                ["data_chars"] = frame.Data.Length.ToString(),
                ["subscriber_count"] = hub.SubscriberCount.ToString(),
            },
        }, ct);

        return seq;
    }

    private async Task<long> AppendRealtimeBufferedAsync(
        string sessionId,
        string workspaceId,
        ServerSentEventFrame frame,
        string recordedAt,
        RuntimeTraceContext effectiveTrace,
        string effectiveComponent,
        string effectiveOperation,
        CancellationToken ct)
    {
        var seq = await ReserveSequenceAsync(sessionId, ct);

        var hub = _sessionChannels.GetOrAdd(sessionId, _ =>
        {
            _logger.LogInformation(
                "[SSM] Channel created for realtime append session={Session} activeChannels={ActiveChannels}",
                sessionId, _sessionChannels.Count + 1);
            return new SessionChannelFanout();
        });

        var item = new BufferedSessionEvent(
            sessionId,
            workspaceId,
            seq,
            frame,
            recordedAt,
            effectiveTrace,
            effectiveComponent,
            effectiveOperation);
        var buffer = _streamBatchBuffers.GetOrAdd(sessionId, _ => new SessionEventBatchBuffer());
        var queuedCount = buffer.Enqueue(item);

        _logger.LogDebug(
            "[SSM] Realtime append buffered session={Session} type={EventType} seq={Seq} queued={Queued} subscribers={SubscriberCount}",
            sessionId, frame.Event, seq, queuedCount, hub.SubscriberCount);

        if (IsRealtimeTerminalFrame(frame))
            await MarkStreamCompleteAsync(sessionId, ct);

        if (queuedCount >= StreamBatchFlushThreshold || IsRealtimeTerminalFrame(frame))
        {
            _ = FlushBufferedSessionEventsAsync(sessionId, CancellationToken.None, waitForGate: false);
        }
        else
        {
            ScheduleBufferedFlush(sessionId, buffer);
        }

        return seq;
    }

    // ════════════════════════════════════════════════════════
    // ISessionEventWriter（新统一事件写入接口，ADX-056-E）
    // ════════════════════════════════════════════════════════

    public async ValueTask<SessionEventEnvelope> AppendAsync(
        string sessionId,
        string workspaceId,
        SessionEventDraft draft,
        CancellationToken ct = default)
    {
        var recordedAt = DateTimeOffset.UtcNow.ToString("O");
        var trace = draft.Trace
            ?? _traceAccessor.Current?.WithSession(sessionId, workspaceId)
            ?? RuntimeTraceContext.CreateNew(sessionId: sessionId, workspaceId: workspaceId);
        var eventId = draft.EventId ?? Guid.NewGuid().ToString("N");

        var seq = await ReserveSequenceAsync(sessionId, ct);

        var payloadJson = draft.Payload.GetRawText();
        var entity = new SessionEventLogEntity
        {
            SessionId = sessionId,
            WorkspaceId = workspaceId,
            AgentInstanceId = trace.AgentInstanceId,
            AgentTemplateId = trace.AgentTemplateId,
            SequenceNum = seq,
            EventType = draft.EventType,
            Data = payloadJson,
            RecordedAt = recordedAt,
            TraceId = trace.TraceId,
            CorrelationId = trace.CorrelationId,
            ExecutionId = trace.ExecutionId,
            ParentExecutionId = trace.ParentExecutionId,
            SubAgentId = trace.SubAgentId,
            Component = RuntimeActivityComponents.SessionState,
            Operation = $"append:{draft.EventType}",
        };

        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            db.SessionEventLogs.Add(entity);
            await db.SaveChangesAsync(ct);
        }

        // Notify subscribers
        var envelope = MapToEnvelope(entity);

        if (_sessionChannels.TryGetValue(sessionId, out var hub))
        {
            var legacyFrame = new ServerSentEventFrame(draft.EventType, payloadJson);
            hub.Publish(legacyFrame);
        }

        if (_headNotificationChannels.TryGetValue(sessionId, out var headChannel))
            headChannel.Writer.TryWrite(new SessionHeadAdvanced(sessionId, seq));

        return envelope;
    }

    public async ValueTask<IReadOnlyList<SessionEventEnvelope>> AppendBatchAsync(
        string sessionId,
        string workspaceId,
        IReadOnlyList<SessionEventDraft> drafts,
        CancellationToken ct)
    {
        if (drafts.Count == 0)
            return [];

        var recordedAt = DateTimeOffset.UtcNow.ToString("O");
        var baseTrace = _traceAccessor.Current?.WithSession(sessionId, workspaceId)
            ?? RuntimeTraceContext.CreateNew(sessionId: sessionId, workspaceId: workspaceId);

        var envelopes = new List<SessionEventEnvelope>(drafts.Count);

        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            db.ChangeTracker.AutoDetectChangesEnabled = false;

            foreach (var draft in drafts)
            {
                var seq = await ReserveSequenceAsync(sessionId, ct);
                var eventId = draft.EventId ?? Guid.NewGuid().ToString("N");
                var payloadJson = draft.Payload.GetRawText();
                var trace = draft.Trace ?? baseTrace;

                var entity = new SessionEventLogEntity
                {
                    SessionId = sessionId,
                    WorkspaceId = workspaceId,
                    AgentInstanceId = trace.AgentInstanceId,
                    AgentTemplateId = trace.AgentTemplateId,
                    SequenceNum = seq,
                    EventType = draft.EventType,
                    Data = payloadJson,
                    RecordedAt = recordedAt,
                    TraceId = trace.TraceId,
                    CorrelationId = trace.CorrelationId,
                    ExecutionId = trace.ExecutionId,
                    ParentExecutionId = trace.ParentExecutionId,
                    SubAgentId = trace.SubAgentId,
                    Component = RuntimeActivityComponents.SessionState,
                    Operation = $"append:{draft.EventType}",
                };

                db.SessionEventLogs.Add(entity);
                envelopes.Add(MapToEnvelope(entity));
            }

            await db.SaveChangesAsync(ct);
        }

        // Notify: publish max sequence in batch (same notification serves all)
        if (envelopes.Count > 0)
        {
            var maxEnvelope = envelopes[^1];
            if (_headNotificationChannels.TryGetValue(sessionId, out var headChannel))
                headChannel.Writer.TryWrite(new SessionHeadAdvanced(sessionId, maxEnvelope.Sequence));
        }

        return envelopes;
    }

    // ════════════════════════════════════════════════════════
    // 历史加载
    // ════════════════════════════════════════════════════════

    public async Task<SessionEventPage> GetEventsAsync(
        string sessionId,
        long? fromSequence = null,
        int limit = 50,
        CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

        var query = db.SessionEventLogs
            .AsNoTracking()
            .Where(e => e.SessionId == sessionId);

        if (fromSequence.HasValue)
        {
            // 从 fromSequence 向前加载更早的事件
            query = query.Where(e => e.SequenceNum <= fromSequence.Value);
        }

        var events = await query
            .OrderByDescending(e => e.SequenceNum)
            .Take(limit + 1) // 多取 1 条判断 HasMore
            .Select(e => new SessionEventEntry
            {
                SequenceNum = e.SequenceNum,
                EventType = e.EventType,
                Data = e.Data,
                RecordedAt = DateTimeOffset.Parse(e.RecordedAt),
            })
            .ToListAsync(ct);

        var hasMore = events.Count > limit;
        if (hasMore) events.RemoveAt(events.Count - 1);

        // 反转为升序（前端 UI 从上往下渲染）
        events.Reverse();

        var totalCount = await db.SessionEventLogs
            .CountAsync(e => e.SessionId == sessionId, ct);

        return new SessionEventPage
        {
            Events = events,
            HasMore = hasMore,
            MinSequence = events.Count > 0 ? events[0].SequenceNum : 0,
            MaxSequence = events.Count > 0 ? events[^1].SequenceNum : 0,
            TotalCount = totalCount,
        };
    }

    // ════════════════════════════════════════════════════════
    // ISessionEventReader
    // ════════════════════════════════════════════════════════

    public async Task<long> GetHeadAsync(string sessionId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

        var maxSeq = await db.SessionEventLogs
            .AsNoTracking()
            .Where(e => e.SessionId == sessionId)
            .MaxAsync(e => (long?)e.SequenceNum, ct);

        return maxSeq ?? 0L;
    }

    public async Task<IReadOnlyList<SessionEventEnvelope>> ReadAfterAsync(
        string sessionId,
        long afterExclusive,
        long? throughInclusive = null,
        int limit = 256,
        CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

        var query = db.SessionEventLogs
            .AsNoTracking()
            .Where(e => e.SessionId == sessionId
                && e.SequenceNum > afterExclusive);

        if (throughInclusive.HasValue)
            query = query.Where(e => e.SequenceNum <= throughInclusive.Value);

        var events = await query
            .OrderBy(e => e.SequenceNum)
            .Take(limit)
            .ToListAsync(ct);

        return events.Select(MapToEnvelope).ToList();
    }

    public async Task<IReadOnlyList<SessionEventEnvelope>> ReadBeforeAsync(
        string sessionId,
        long beforeExclusive,
        int limit = 50,
        CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

        var events = await db.SessionEventLogs
            .AsNoTracking()
            .Where(e => e.SessionId == sessionId
                && e.SequenceNum < beforeExclusive)
            .OrderByDescending(e => e.SequenceNum)
            .Take(limit)
            .ToListAsync(ct);

        events.Reverse();
        return events.Select(MapToEnvelope).ToList();
    }

    private static SessionEventEnvelope MapToEnvelope(SessionEventLogEntity e)
    {
        JsonElement payload;
        try
        {
            payload = JsonSerializer.Deserialize<JsonElement>(e.Data);
        }
        catch
        {
            payload = JsonSerializer.SerializeToElement(new { raw = e.Data });
        }

        return new SessionEventEnvelope(
            EventId: e.Id.ToString(),
            SessionId: e.SessionId,
            ConversationId: e.SessionId,  // 当前系统中 conversationId = sessionId
            Sequence: e.SequenceNum,
            EventType: e.EventType,
            SchemaVersion: 1,
            CommandId: null,
            TurnId: null,
            MessageId: null,
            AgentId: e.AgentInstanceId,
            OccurredAt: DateTimeOffset.TryParse(e.RecordedAt, out var dt) ? dt : DateTimeOffset.UtcNow,
            Payload: payload,
            Trace: e.TraceId is not null
                ? new RuntimeTraceContext
                {
                    TraceId = e.TraceId,
                    CorrelationId = e.CorrelationId ?? e.TraceId,
                    SessionId = e.SessionId,
                    WorkspaceId = e.WorkspaceId,
                    AgentInstanceId = e.AgentInstanceId,
                    AgentTemplateId = e.AgentTemplateId,
                    ExecutionId = e.ExecutionId,
                }
                : null
        );
    }

    public async Task<SessionReplayResult> ReplaySessionAsync(
        string sessionId,
        long? fromSequenceNum = null,
        int limit = 200,
        CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

        // 1. 获取当前会话状态
        var currentState = _sessionStates.GetValueOrDefault(sessionId, SessionState.Streaming);

        // 2. 从指定序列号开始加载事件（升序，向后加载）
        var query = db.SessionEventLogs
            .AsNoTracking()
            .Where(e => e.SessionId == sessionId);

        if (fromSequenceNum.HasValue && fromSequenceNum.Value > 0)
        {
            query = query.Where(e => e.SequenceNum >= fromSequenceNum.Value);
        }

        var events = await query
            .OrderBy(e => e.SequenceNum)
            .Take(limit + 1) // 多取 1 条判断 HasMore
            .Select(e => new SessionEventEntry
            {
                SequenceNum = e.SequenceNum,
                EventType = e.EventType,
                Data = e.Data,
                RecordedAt = DateTimeOffset.Parse(e.RecordedAt),
            })
            .ToListAsync(ct);

        var hasMore = events.Count > limit;
        if (hasMore) events.RemoveAt(events.Count - 1);

        // 3. 获取总事件数
        var totalCount = await db.SessionEventLogs
            .CountAsync(e => e.SessionId == sessionId, ct);

        // 4. 获取子代理状态
        await ReconcileSubAgentTerminalStatesAsync(db, sessionId, ct);
        var subAgentEntities = await db.SessionSubAgents
            .AsNoTracking()
            .Where(e => e.ParentSessionId == sessionId)
            .OrderBy(e => e.SpawnedAt)
            .ToListAsync(ct);

        var subAgents = subAgentEntities.Select(e => new SubAgentStatus
        {
            SubSessionId = e.SubSessionId,
            Status = e.Status,
            TemplateId = e.TemplateId,
            ModelId = e.ModelId,
            TaskSummary = e.TaskSummary,
            SpawnedAt = DateTimeOffset.Parse(e.SpawnedAt),
            CompletedAt = e.CompletedAt != null ? DateTimeOffset.Parse(e.CompletedAt) : null,
            ResultSummary = e.ReplySummary,
            Success = e.Success,
        }).ToList();

        _logger.LogDebug(
            "[SSM] Replay session={Session} from={FromSeq} limit={Limit} events={EventCount} total={Total} hasMore={HasMore} subAgents={SubAgentCount}",
            sessionId, fromSequenceNum, limit, events.Count, totalCount, hasMore, subAgents.Count);

        return new SessionReplayResult
        {
            SessionId = sessionId,
            CurrentState = currentState.ToString(),
            Events = events,
            TotalEventCount = totalCount,
            HasMore = hasMore,
            SubAgents = subAgents,
        };
    }

    public async Task<long> GetEventCountAfterAsync(string sessionId, long afterSequence, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        return await db.SessionEventLogs
            .CountAsync(e => e.SessionId == sessionId && e.SequenceNum > afterSequence, ct);
    }

    public async Task<long> GetLatestSequenceNumAsync(string sessionId, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        return await db.SessionEventLogs
            .Where(e => e.SessionId == sessionId)
            .MaxAsync(e => (long?)e.SequenceNum, ct) ?? 0;
    }

    // ════════════════════════════════════════════════════════
    // 实时订阅
    // ════════════════════════════════════════════════════════

    public ChannelReader<ServerSentEventFrame>? Subscribe(string sessionId)
    {
        if (_sessionChannels.TryGetValue(sessionId, out var hub))
        {
            var reader = hub.Subscribe();
            _logger.LogInformation(
                "[SSM] Channel subscribe existing session={Session} subscribers={SubscriberCount} activeChannels={ActiveChannels}",
                sessionId, hub.SubscriberCount, _sessionChannels.Count);
            return reader;
        }

        // Channel 不存在且会话已关闭 → 返回 null
        if (_sessionStates.TryGetValue(sessionId, out var state) && state == SessionState.Closed)
        {
            _logger.LogWarning(
                "[SSM] Channel subscribe closed session={Session} state={State} activeChannels={ActiveChannels}",
                sessionId, state, _sessionChannels.Count);
            return null;
        }

        // 否则创建新 Channel（可能后续有事件产生）
        var newHub = _sessionChannels.GetOrAdd(sessionId, _ => new SessionChannelFanout());
        var newReader = newHub.Subscribe();
        _logger.LogInformation(
            "[SSM] Channel subscribe created session={Session} state={State} subscribers={SubscriberCount} activeChannels={ActiveChannels}",
            sessionId, _sessionStates.GetValueOrDefault(sessionId, SessionState.Streaming), newHub.SubscriberCount, _sessionChannels.Count);
        return newReader;
    }

    public void Unsubscribe(string sessionId, ChannelReader<ServerSentEventFrame> reader)
    {
        if (!_sessionChannels.TryGetValue(sessionId, out var hub))
            return;

        var removed = hub.Unsubscribe(reader);
        _logger.LogInformation(
            "[SSM] Channel unsubscribe session={Session} removed={Removed} subscribers={SubscriberCount} activeChannels={ActiveChannels}",
            sessionId, removed, hub.SubscriberCount, _sessionChannels.Count);
    }

    public ChannelReader<SessionNotification> SubscribeWorkspace(string workspaceId)
    {
        var ch = _workspaceChannels.GetOrAdd(workspaceId, _ =>
            Channel.CreateBounded<SessionNotification>(
                new BoundedChannelOptions(128)
                {
                    FullMode = BoundedChannelFullMode.DropOldest
                }));
        return ch.Reader;
    }

    public void UnsubscribeWorkspace(string workspaceId, ChannelReader<SessionNotification> reader)
    {
        if (!_workspaceChannels.TryGetValue(workspaceId, out var ch))
            return;

        _logger.LogInformation(
            "[SSM] Workspace channel unsubscribe workspace={Workspace} activeChannels={ActiveChannels}",
            workspaceId, _workspaceChannels.Count);

        // 启动 TTL 清理倒计时：最后一个订阅者断开后延迟释放 Channel
        _ = ScheduleWorkspaceChannelCleanupAsync(workspaceId);
    }

    // ════════════════════════════════════════════════════════
    // ISessionHeadNotifier
    // ════════════════════════════════════════════════════════

    public async IAsyncEnumerable<SessionHeadAdvanced> SubscribeAsync(
        string sessionId,
        CancellationToken ct = default)
    {
        var channel = _headNotificationChannels.GetOrAdd(sessionId, _ =>
        {
            _logger.LogInformation(
                "[SSM] Head notification channel created session={Session}",
                sessionId);
            return Channel.CreateBounded<SessionHeadAdvanced>(
                new BoundedChannelOptions(128)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                    SingleWriter = false,
                });
        });

        while (!ct.IsCancellationRequested)
        {
            var readOk = false;
            try
            {
                readOk = await channel.Reader.WaitToReadAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (!readOk) break;

            while (channel.Reader.TryRead(out var head))
            {
                yield return head;
            }
        }
    }

    // ════════════════════════════════════════════════════════
    // 会话状态
    // ════════════════════════════════════════════════════════

    public Task<SessionState> GetSessionStateAsync(string sessionId, CancellationToken ct = default)
    {
        var state = _sessionStates.GetValueOrDefault(sessionId, SessionState.Streaming);
        return Task.FromResult(state);
    }

    // ════════════════════════════════════════════════════════
    // 子代理追踪
    // ════════════════════════════════════════════════════════

    public async Task TrackSubAgentStartAsync(
        string parentSessionId, SubAgentSpawnInfo info,
        CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

        var entity = new SessionSubAgentEntity
        {
            ParentSessionId = info.ParentSessionId,
            ParentAgentId = info.ParentAgentId,
            SubSessionId = info.SubSessionId,
            Status = "running",
            TemplateId = info.TemplateId,
            ModelId = info.ModelId,
            TaskSummary = info.TaskSummary,
            SpawnedAt = info.SpawnedAt.ToString("O"),
        };
        db.SessionSubAgents.Add(entity);
        await db.SaveChangesAsync(ct);

        _logger.LogDebug("[SSM] SubAgent spawned parent={Parent} sub={Sub} template={Template} task={Task}",
            info.ParentSessionId, info.SubSessionId, info.TemplateId, info.TaskSummary);

        _logger.LogInformation(
            "[SSM] Sub-agent spawned parent={Parent} sub={Sub} template={Template}",
            parentSessionId, info.SubSessionId, info.TemplateId ?? "default");
    }

    public async Task TrackSubAgentCompleteAsync(
        string subSessionId, SubAgentResult result,
        CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

        var entity = await db.SessionSubAgents
            .FirstOrDefaultAsync(e => e.SubSessionId == subSessionId, ct);

        if (entity == null)
        {
            _logger.LogWarning("[SSM] Sub-agent not found for complete sub={Sub}", subSessionId);
            return;
        }

        if (entity.Status is "completed" or "failed" or "cancelled" or "timed_out")
        {
            _logger.LogInformation(
                "[SSM] Ignoring duplicate terminal sub-agent state sub={Sub} status={Status}",
                subSessionId,
                entity.Status);
            return;
        }

        entity.Status = result.Success ? "completed" : "failed";
        entity.CompletedAt = result.CompletedAt.ToString("O");
        entity.Success = result.Success;
        var resultSummary = result.Reply
            ?? result.ToolFailureSummary
            ?? result.Error;
        entity.ReplySummary = resultSummary?.Length > 200
            ? resultSummary[..200] + "..."
            : resultSummary;
        var errorSummary = result.Error ?? result.ToolFailureSummary;
        entity.ErrorSummary = errorSummary?.Length > 500
            ? errorSummary[..500]
            : errorSummary;

        await db.SaveChangesAsync(ct);

        _logger.LogDebug("[SSM] SubAgent completed sub={Sub} parent={Parent} success={Success} reply={Reply}",
            subSessionId, entity.ParentSessionId, result.Success, entity.ReplySummary ?? "-");

        // 推送工作区通知
        await PushWorkspaceNotificationAsync(entity.ParentSessionId,
            new SessionNotification
            {
                Type = SessionEventTypes.NotificationSubAgentCompleted,
                SessionId = entity.ParentSessionId,
                WorkspaceId = "", // 由调用方通过 AppendAsync 写入正确值
                AgentId = entity.ParentAgentId,
                Data = new
                {
                    subSessionId,
                    success = result.Success,
                    summary = entity.ReplySummary,
                    toolFailureCount = result.ToolFailureCount,
                    toolOutputTruncatedCount = result.ToolOutputTruncatedCount,
                    toolOutputChars = result.ToolOutputChars,
                    toolFailureSummary = result.ToolFailureSummary,
                },
                Timestamp = result.CompletedAt,
            });

        _logger.LogInformation(
            "[SSM] Sub-agent completed sub={Sub} parent={Parent} success={Success}",
            subSessionId, entity.ParentSessionId, result.Success);

        // 检查所有子代理是否完成 → 关闭会话
        var runningCount = await GetRunningSubAgentCountAsync(entity.ParentSessionId, ct);
        if (runningCount == 0)
        {
            await MarkSessionClosedAsync(entity.ParentSessionId, ct);
        }
    }

    public async Task<IReadOnlyList<SubAgentStatus>> GetSubAgentsAsync(
        string sessionId, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

        await ReconcileSubAgentTerminalStatesAsync(db, sessionId, ct);

        var entities = await db.SessionSubAgents
            .AsNoTracking()
            .Where(e => e.ParentSessionId == sessionId)
            .OrderByDescending(e => e.SpawnedAt)
            .ToListAsync(ct);

        return entities.Select(e => new SubAgentStatus
        {
            SubSessionId = e.SubSessionId,
            Status = e.Status,
            TemplateId = e.TemplateId,
            ModelId = e.ModelId,
            TaskSummary = e.TaskSummary,
            SpawnedAt = DateTimeOffset.Parse(e.SpawnedAt),
            CompletedAt = e.CompletedAt != null ? DateTimeOffset.Parse(e.CompletedAt) : null,
            ResultSummary = e.ReplySummary,
            Success = e.Success,
        }).ToList();
    }

    public async Task<int> GetRunningSubAgentCountAsync(string parentSessionId, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

        await ReconcileSubAgentTerminalStatesAsync(db, parentSessionId, ct);

        return await db.SessionSubAgents
            .CountAsync(e => e.ParentSessionId == parentSessionId && e.Status == "running", ct);
    }

    private async Task ReconcileSubAgentTerminalStatesAsync(
        PlatformDbContext db,
        string parentSessionId,
        CancellationToken ct)
    {
        var running = await db.SessionSubAgents
            .Where(e => e.ParentSessionId == parentSessionId && e.Status == "running")
            .ToListAsync(ct);
        if (running.Count == 0) return;

        var subSessionIds = running.Select(e => e.SubSessionId).ToList();

        var terminalRunRows = await db.SubAgentRuns
            .AsNoTracking()
            .Where(r => subSessionIds.Contains(r.SubSessionId) && r.Status != "running")
            .ToListAsync(ct);
        var terminalRuns = terminalRunRows
            .GroupBy(r => r.SubSessionId, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(r => r.CompletedAt ?? r.StartedAt).First(),
                StringComparer.Ordinal);

        var dispatcherFailureRows = await db.RuntimeActivities
            .AsNoTracking()
            .Where(a => a.Status == "failed")
            .Where(a => a.Component == RuntimeActivityComponents.EventDispatcher)
            .Where(a => a.Operation == "dispatch")
            .Where(a => a.SubAgentId != null && subSessionIds.Contains(a.SubAgentId))
            .Where(a => a.Summary == "Max retries exhausted" ||
                        (a.MetadataJson != null && a.MetadataJson.Contains("\"eventType\":\"subagent.run.created\"")))
            .ToListAsync(ct);
        var dispatcherFailures = dispatcherFailureRows
            .Where(a => a.SubAgentId != null)
            .GroupBy(a => a.SubAgentId!, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(a => a.StartedAtUtc).First(),
                StringComparer.Ordinal);

        var changed = 0;
        foreach (var entity in running)
        {
            if (terminalRuns.TryGetValue(entity.SubSessionId, out var run))
            {
                ApplySubAgentTerminalState(
                    entity,
                    run.Status,
                    run.CompletedAt ?? run.StartedAt,
                    run.ErrorMessage,
                    run.Status == "completed");
                changed++;
                continue;
            }

            if (dispatcherFailures.TryGetValue(entity.SubSessionId, out var failure))
            {
                ApplySubAgentTerminalState(
                    entity,
                    "failed",
                    failure.StartedAtUtc,
                    failure.ErrorMessage ?? failure.Summary ?? SubAgentDispatcherFailureSummary,
                    false);

                var runEntity = await db.SubAgentRuns
                    .FirstOrDefaultAsync(r => r.SubSessionId == entity.SubSessionId && r.Status == "running", ct);
                if (runEntity != null)
                {
                    runEntity.Status = "failed";
                    runEntity.CompletedAt = failure.StartedAtUtc;
                    runEntity.ErrorMessage = entity.ErrorSummary;
                }
                changed++;
            }
        }

        if (changed == 0) return;

        await db.SaveChangesAsync(ct);
        _logger.LogWarning(
            "[SSM] Reconciled stale sub-agent terminal states parent={Parent} count={Count}",
            parentSessionId,
            changed);
    }

    private static void ApplySubAgentTerminalState(
        SessionSubAgentEntity entity,
        string status,
        string completedAt,
        string? error,
        bool success)
    {
        entity.Status = status == "completed" ? "completed" : status == "cancelled" ? "cancelled" : "failed";
        entity.CompletedAt = completedAt;
        entity.Success = success;
        if (!success)
        {
            entity.ErrorSummary = string.IsNullOrWhiteSpace(error)
                ? SubAgentDispatcherFailureSummary
                : error.Length > 500
                    ? error[..500]
                    : error;
        }
    }

    // ════════════════════════════════════════════════════════
    // 生命周期标记
    // ════════════════════════════════════════════════════════

        public Task MarkStreamCompleteAsync(string sessionId, CancellationToken ct = default)
    {
        _sessionStates[sessionId] = SessionState.StreamCompleted;
        _logger.LogDebug("[SSM] Stream completed session={Session}", sessionId);
        _ = _stateStore.PersistAsync(sessionId, nameof(SessionState.StreamCompleted), "", "");
        return Task.CompletedTask;
    }

    public Task MarkSessionClosedAsync(string sessionId, CancellationToken ct = default)
    {
        _sessionStates[sessionId] = SessionState.Closed;
        _logger.LogDebug("[SSM] Session closed session={Session}", sessionId);
        _logger.LogInformation("[SSM] Session closed session={Session}", sessionId);

        _ = _stateStore.PersistAsync(sessionId, nameof(SessionState.Closed), "", "");

        // 启动 TTL 倒计时后清理 Channel（不立即清理，给重连窗口）
        _ = ScheduleChannelCleanupAsync(sessionId);

        return Task.CompletedTask;
    }

    // ════════════════════════════════════════════════════════
    // 内部方法
    // ════════════════════════════════════════════════════════

    /// <summary>
    /// ADR-028：在 per-session SemaphoreSlim 临界区内执行序号查询 + 插入 + SaveChangesAsync。
    /// 同一 session 的并发 append 串行化，消除 unique constraint 竞争。
    /// </summary>
    private async Task<long> AppendSqliteEventAsync(
        string sessionId,
        string workspaceId,
        ServerSentEventFrame frame,
        string recordedAt,
        RuntimeTraceContext effectiveTrace,
        string effectiveComponent,
        string effectiveOperation,
        CancellationToken ct)
    {
        var gate = _seqLocks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

            var state = _sequenceStates.GetOrAdd(sessionId, _ => new SessionSequenceState());
            long seq;
            if (state.Initialized)
            {
                seq = state.NextSequence++;
            }
            else
            {
                var maxSeq = await db.SessionEventLogs
                    .Where(e => e.SessionId == sessionId)
                    .MaxAsync(e => (long?)e.SequenceNum, ct) ?? 0;

                seq = maxSeq + 1;
                state.NextSequence = seq + 1;
                state.Initialized = true;
            }

            var frameData = InjectSequenceNum(frame.Data, seq);

            db.SessionEventLogs.Add(new SessionEventLogEntity
            {
                SessionId = sessionId,
                WorkspaceId = workspaceId,
                AgentInstanceId = effectiveTrace.AgentInstanceId,
                AgentTemplateId = effectiveTrace.AgentTemplateId,
                SequenceNum = seq,
                EventType = frame.Event,
                Data = frameData,
                RecordedAt = recordedAt,
                TraceId = effectiveTrace.TraceId,
                CorrelationId = effectiveTrace.CorrelationId,
                ExecutionId = effectiveTrace.ExecutionId,
                ParentExecutionId = effectiveTrace.ParentExecutionId,
                SubAgentId = effectiveTrace.SubAgentId,
                Component = effectiveComponent,
                Operation = effectiveOperation,
            });

            await db.SaveChangesAsync(ct);
            return seq;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// ADR-028-C：对 DbUpdateException + SQLite error 19（session_event_log 唯一约束冲突）
    /// 最多重试 3 次，每次重试延迟递增。作为多实例/旧代码路径的最后防线。
    /// </summary>
    private async Task<long> AppendSqliteEventWithRetryAsync(
        string sessionId,
        string workspaceId,
        ServerSentEventFrame frame,
        string recordedAt,
        RuntimeTraceContext effectiveTrace,
        string effectiveComponent,
        string effectiveOperation,
        CancellationToken ct)
    {
        const int maxAttempts = 3;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                return await AppendSqliteEventAsync(
                    sessionId, workspaceId, frame, recordedAt,
                    effectiveTrace, effectiveComponent, effectiveOperation, ct);
            }
            catch (DbUpdateException ex) when (IsSessionSequenceConflict(ex) && attempt < maxAttempts)
            {
                _logger.LogWarning(
                    ex,
                    "[SSM] Sequence conflict retry session={Session} event={EventType} attempt={Attempt}",
                    sessionId, frame.Event, attempt);

                await Task.Delay(TimeSpan.FromMilliseconds(20 * attempt), ct);
            }
        }

        // 最后一次尝试不捕获，让异常传播
        return await AppendSqliteEventAsync(
            sessionId, workspaceId, frame, recordedAt,
            effectiveTrace, effectiveComponent, effectiveOperation, ct);
    }

    /// <summary>
    /// 测试钩子：持有指定会话的 flush gate，模拟一个正在进行的批量 flush。
    /// 返回的 IDisposable 释放 gate。仅用于竞态条件单元测试。
    /// </summary>
    internal async Task<IDisposable> HoldFlushGateForTestingAsync(
        string sessionId,
        CancellationToken ct = default)
    {
        var buffer = _streamBatchBuffers.GetOrAdd(sessionId, _ => new SessionEventBatchBuffer());
        await buffer.FlushGate.WaitAsync(ct);
        return new FlushGateReleaser(buffer);
    }

    private sealed class FlushGateReleaser : IDisposable
    {
        private SessionEventBatchBuffer? _buffer;
        public FlushGateReleaser(SessionEventBatchBuffer buffer) => _buffer = buffer;
        public void Dispose()
        {
            _buffer?.FlushGate.Release();
            _buffer = null;
        }
    }

    private async Task<long> ReserveSequenceAsync(string sessionId, CancellationToken ct)
    {
        var gate = _seqLocks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);

        try
        {
            var state = _sequenceStates.GetOrAdd(sessionId, _ => new SessionSequenceState());
            if (!state.Initialized)
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
                var maxSeq = await db.SessionEventLogs
                    .Where(e => e.SessionId == sessionId)
                    .MaxAsync(e => (long?)e.SequenceNum, ct) ?? 0;

                state.NextSequence = maxSeq + 1;
                state.Initialized = true;
            }

            return state.NextSequence++;
        }
        finally
        {
            gate.Release();
        }
    }

    private static bool IsRealtimeBatchable(ServerSentEventFrame frame, string component)
        => component == RuntimeActivityComponents.AgentExecution
           && frame.Event is "delta" or "thinking" or "usage" or "done" or "error" or "cancelled";

    private static bool IsRealtimeTerminalFrame(ServerSentEventFrame frame)
        => frame.Event is "done" or "error" or "cancelled";

    private void ScheduleBufferedFlush(string sessionId, SessionEventBatchBuffer buffer)
    {
        if (!buffer.TrySchedule())
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(StreamBatchFlushDelay);
                await FlushBufferedSessionEventsAsync(sessionId, CancellationToken.None, waitForGate: false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[SSM] Delayed stream batch flush failed session={Session}", sessionId);
            }
        });
    }

    private async Task FlushPendingSessionEventsAsync(string sessionId, CancellationToken ct)
    {
        if (!_streamBatchBuffers.TryGetValue(sessionId, out var buffer))
            return;

        var attempts = 0;
        while (buffer.Count > 0 && attempts++ < 8)
            await FlushBufferedSessionEventsAsync(sessionId, ct, waitForGate: true);
    }

    private async Task FlushBufferedSessionEventsAsync(string sessionId, CancellationToken ct, bool waitForGate)
    {
        if (!_streamBatchBuffers.TryGetValue(sessionId, out var buffer))
            return;

        if (waitForGate)
        {
            await buffer.FlushGate.WaitAsync(ct);
        }
        else if (!await buffer.FlushGate.WaitAsync(0, ct))
        {
            // Gate 被占用：重置调度标志并重新安排延迟 flush。
            // 否则 _flushScheduled 卡在 1，后续 ScheduleBufferedFlush 的 TrySchedule 永远返回 false，
            // 导致 terminal 事件(done/usage)永远留在内存 buffer，不写入 SQLite session_event_log。
            // 关联 bug：WWmyKiW5kf9wrd 会话 done/usage 事件丢失
            _logger.LogInformation(
                "[SSM] Flush gate busy, rescheduled delayed flush session={Session} bufferedCount={BufferedCount}",
                sessionId, buffer.Count);
            buffer.MarkScheduleConsumed();
            ScheduleBufferedFlush(sessionId, buffer);
            return;
        }

        try
        {
            buffer.MarkScheduleConsumed();
            var batch = buffer.Drain();
            if (batch.Count == 0)
                return;

            var startedAt = Stopwatch.GetTimestamp();
            var sqliteStartedAt = Stopwatch.GetTimestamp();
            await PersistBufferedSessionEventsWithRetryAsync(batch, ct);
            var sqliteMs = ElapsedMilliseconds(sqliteStartedAt);

            // Fan-out to SSE subscribers AFTER SQLite commit (ADR-056: persist-first).
            if (_sessionChannels.TryGetValue(sessionId, out var fanoutHub))
            {
                foreach (var item in batch)
                    fanoutHub.Publish(item.Frame);
            }

            // Head notification: lightweight signal carrying only committedThroughSequence.
            // SSE clients read actual events from DB after receiving this.
            if (_headNotificationChannels.TryGetValue(sessionId, out var headChannel) && batch.Count > 0)
            {
                var maxSeq = batch[^1].SequenceNum;
                headChannel.Writer.TryWrite(new SessionHeadAdvanced(sessionId, maxSeq));
            }

            long jsonlMs = 0;
            var jsonlStartedAt = Stopwatch.GetTimestamp();
            foreach (var item in batch)
            {
                try
                {
                    _jsonlWriter.WriteEventLine(
                        item.SessionId,
                        item.Frame.Event,
                        item.Frame.Data,
                        item.SequenceNum,
                        item.RecordedAt);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "[SSM] JSONL batch write failed (non-fatal) session={Session} type={EventType} seq={Seq}",
                        item.SessionId, item.Frame.Event, item.SequenceNum);
                }
            }
            jsonlMs = ElapsedMilliseconds(jsonlStartedAt);

            var durationMs = ElapsedMilliseconds(startedAt);
            await RecordStreamBatchAppendActivityAsync(batch, durationMs, sqliteMs, jsonlMs, ct);
        }
        finally
        {
            buffer.FlushGate.Release();
            if (buffer.Count > 0)
                ScheduleBufferedFlush(sessionId, buffer);
        }
    }

    private async Task PersistBufferedSessionEventsWithRetryAsync(
        IReadOnlyList<BufferedSessionEvent> batch,
        CancellationToken ct)
    {
        const int maxAttempts = 3;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await PersistBufferedSessionEventsAsync(batch, ct);
                return;
            }
            catch (DbUpdateException ex) when (IsSessionSequenceConflict(ex) && attempt < maxAttempts)
            {
                _logger.LogWarning(
                    ex,
                    "[SSM] Stream batch sequence conflict retry session={Session} count={Count} attempt={Attempt}",
                    batch[0].SessionId, batch.Count, attempt);

                await Task.Delay(TimeSpan.FromMilliseconds(20 * attempt), ct);
            }
        }

        await PersistBufferedSessionEventsAsync(batch, ct);
    }

    private async Task PersistBufferedSessionEventsAsync(
        IReadOnlyList<BufferedSessionEvent> batch,
        CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        db.ChangeTracker.AutoDetectChangesEnabled = false;

        db.SessionEventLogs.AddRange(batch.Select(item => new SessionEventLogEntity
        {
            SessionId = item.SessionId,
            WorkspaceId = item.WorkspaceId,
            AgentInstanceId = item.Trace.AgentInstanceId,
            AgentTemplateId = item.Trace.AgentTemplateId,
            SequenceNum = item.SequenceNum,
            EventType = item.Frame.Event,
            Data = item.Frame.Data,
            RecordedAt = item.RecordedAt,
            TraceId = item.Trace.TraceId,
            CorrelationId = item.Trace.CorrelationId,
            ExecutionId = item.Trace.ExecutionId,
            ParentExecutionId = item.Trace.ParentExecutionId,
            SubAgentId = item.Trace.SubAgentId,
            Component = item.Component,
            Operation = item.Operation,
        }));

        await db.SaveChangesAsync(ct);

        foreach (var item in batch)
        {
            await MirrorRawEventAsync(
                item.SessionId,
                item.WorkspaceId,
                item.Frame,
                item.RecordedAt,
                item.SequenceNum,
                item.Trace,
                item.Component,
                item.Operation,
                ct);
        }
    }

    private async Task MirrorRawEventAsync(
        string sessionId,
        string workspaceId,
        ServerSentEventFrame frame,
        string recordedAt,
        long sequenceNum,
        RuntimeTraceContext trace,
        string component,
        string operation,
        CancellationToken ct)
    {
        if (_rawLogMirror is null || string.IsNullOrWhiteSpace(trace.AgentInstanceId))
            return;

        try
        {
            await _rawLogMirror.MirrorAsync(
                new AgentRawLogMirrorRecord(
                    WorkspaceId: workspaceId,
                    AgentInstanceId: trace.AgentInstanceId,
                    AgentTemplateId: trace.AgentTemplateId,
                    SessionId: sessionId,
                    SequenceNum: sequenceNum,
                    EventType: frame.Event,
                    Data: frame.Data,
                    RecordedAt: recordedAt,
                    TraceId: trace.TraceId,
                    CorrelationId: trace.CorrelationId,
                    ExecutionId: trace.ExecutionId,
                    ParentExecutionId: trace.ParentExecutionId,
                    SubAgentId: trace.SubAgentId,
                    Component: component,
                    Operation: operation),
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "[SSM] Agent raw log mirror failed (non-fatal) session={Session} agent={Agent} type={EventType} seq={Seq}",
                sessionId,
                trace.AgentInstanceId,
                frame.Event,
                sequenceNum);
        }
    }

    private async Task RecordStreamBatchAppendActivityAsync(
        IReadOnlyList<BufferedSessionEvent> batch,
        long durationMs,
        long sqliteMs,
        long jsonlMs,
        CancellationToken ct)
    {
        if (batch.Count == 0)
            return;

        var first = batch[0];
        var last = batch[^1];
        var deltaCount = batch.Count(item => item.Frame.Event == "delta");
        var thinkingCount = batch.Count(item => item.Frame.Event == "thinking");
        var dataChars = batch.Sum(item => item.Frame.Data.Length);

        await _activitySink.RecordAsync(new RuntimeActivity
        {
            Trace = first.Trace,
            Component = RuntimeActivityComponents.SessionState,
            Operation = "append.batch:stream",
            Status = RuntimeActivityStatuses.Succeeded,
            StartedAtUtc = DateTimeOffset.Parse(first.RecordedAt),
            EndedAtUtc = DateTimeOffset.UtcNow,
            DurationMs = durationMs,
            Summary = $"Appended {batch.Count} buffered stream events.",
            Metadata = new Dictionary<string, string>
            {
                ["count"] = batch.Count.ToString(),
                ["first_sequence"] = first.SequenceNum.ToString(),
                ["last_sequence"] = last.SequenceNum.ToString(),
                ["delta_count"] = deltaCount.ToString(),
                ["thinking_count"] = thinkingCount.ToString(),
                ["sqlite_ms"] = sqliteMs.ToString(),
                ["jsonl_ms"] = jsonlMs.ToString(),
                ["data_chars"] = dataChars.ToString(),
            },
        }, ct);
    }

    /// <summary>
    /// 判定 DbUpdateException 是否为 session_event_log 的 (session_id, sequence_num) 唯一约束冲突。
    /// </summary>
    private static bool IsSessionSequenceConflict(DbUpdateException ex)
    {
        return ex.InnerException is SqliteException sqlite
            && sqlite.SqliteErrorCode == 19
            && sqlite.Message.Contains("session_event_log.session_id, session_event_log.sequence_num", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 推送工作区级别通知到对应 Channel。
    /// 工作区 ID 从 session_event_log 中查询。
    /// </summary>
    private async Task PushWorkspaceNotificationAsync(
        string sessionId, SessionNotification notification)
    {
        try
        {
            // 查找会话所属工作区
            using var scope = _scopeFactory.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var workspaceId = await db.SessionEventLogs
                .Where(e => e.SessionId == sessionId)
                .Select(e => e.WorkspaceId)
                .FirstOrDefaultAsync();

            if (string.IsNullOrEmpty(workspaceId)) return;

            var enriched = notification with { WorkspaceId = workspaceId };

            if (_workspaceChannels.TryGetValue(workspaceId, out var ch))
            {
                ch.Writer.TryWrite(enriched);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[SSM] PushWorkspaceNotification failed session={Session}", sessionId);
        }
    }

    /// <summary>
    /// TTL 倒计时后清理会话 Channel（后台执行，不等待）。
    /// </summary>
    private async Task ScheduleChannelCleanupAsync(string sessionId)
    {
        try
        {
            await Task.Delay(ChannelTtl);
            if (_sessionChannels.TryRemove(sessionId, out var hub))
            {
                hub.Complete();
                _logger.LogDebug("[SSM] Channel cleaned up session={Session}", sessionId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[SSM] Channel cleanup failed session={Session}", sessionId);
        }
    }

    /// <summary>
    /// TTL 倒计时后清理工作区 Channel（后台执行，不等待）。
    /// </summary>
    private async Task ScheduleWorkspaceChannelCleanupAsync(string workspaceId)
    {
        try
        {
            await Task.Delay(ChannelTtl);
            if (_workspaceChannels.TryRemove(workspaceId, out var ch))
            {
                ch.Writer.Complete();
                _logger.LogDebug("[SSM] Workspace channel cleaned up workspace={Workspace}", workspaceId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[SSM] Workspace channel cleanup failed workspace={Workspace}", workspaceId);
        }
    }

    // ════════════════════════════════════════════════════════
    // 一致性检查（ARCH-SESSION-002）
    // ════════════════════════════════════════════════════════

    /// <summary>
    /// 检查 SQLite 事件日志与 JSONL 文件的一致性。
    /// 比较 SQLite session_event_log 表的事件计数与 JSONL 文件行数。
    /// 关联 ADR：Docs/07架构/20会话状态机与事件规范ADR.md §6
    /// </summary>
    public async Task<SessionConsistencyReport> CheckConsistencyAsync(string sessionId, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

        var sqliteCount = await db.SessionEventLogs
            .CountAsync(e => e.SessionId == sessionId, ct);

        var jsonlCount = _jsonlWriter.GetLineCount(sessionId);

        var diff = sqliteCount - jsonlCount;
        string? details = null;

        if (diff > 0)
            details = $"SQLite 比 JSONL 多 {diff} 条事件（JSONL 写入可能丢失）";
        else if (diff < 0)
            details = $"JSONL 比 SQLite 多 {-diff} 行（JSONL 存在多余数据，不自动修正）";

        _logger.LogDebug(
            "[SSM] Consistency check session={Session} sqlite={Sqlite} jsonl={Jsonl} diff={Diff}",
            sessionId, sqliteCount, jsonlCount, diff);

        return new SessionConsistencyReport
        {
            SessionId = sessionId,
            SqliteEventCount = sqliteCount,
            JsonlLineCount = jsonlCount,
            IsConsistent = diff == 0,
            Difference = diff,
            Details = details,
        };
    }

    // ════════════════════════════════════════════════════════
    // Trace 聚合（ARCH-SESSION-004）
    // ════════════════════════════════════════════════════════

    /// <summary>
    /// 获取会话级 Trace 聚合报告。
    /// 从 session_event_log 查询该会话的所有事件，按 traceId、component 等维度聚合。
    /// 关联 ADR：Docs/07架构/20会话状态机与事件规范ADR.md §6
    /// </summary>
    public async Task<SessionTraceReport> GetTraceReportAsync(string sessionId, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

        // 获取该会话所有事件（按序列号升序）
        var events = await db.SessionEventLogs
            .AsNoTracking()
            .Where(e => e.SessionId == sessionId)
            .OrderBy(e => e.SequenceNum)
            .ToListAsync(ct);

        if (events.Count == 0)
        {
            return new SessionTraceReport
            {
                SessionId = sessionId,
                TraceIds = Array.Empty<string>(),
                ComponentTimeline = Array.Empty<ComponentTimelineEntry>(),
                LlmCalls = Array.Empty<LlmCallEntry>(),
                ToolCalls = Array.Empty<ToolCallEntry>(),
                SubAgents = Array.Empty<SubAgentTraceEntry>(),
                TotalDurationMs = 0,
                TotalTokens = 0,
            };
        }

        // 解析时间戳
        var firstTs = DateTimeOffset.Parse(events[0].RecordedAt);
        var lastTs = DateTimeOffset.Parse(events[^1].RecordedAt);
        var totalDurationMs = (long)(lastTs - firstTs).TotalMilliseconds;

        // 收集所有唯一 traceId
        var traceIds = events
            .Where(e => e.TraceId != null)
            .Select(e => e.TraceId!)
            .Distinct()
            .ToList();

        // 按组件聚合时序（component → [{operation, status, startedAt, durationMs}...]）
        var componentTimeline = events
            .Where(e => e.Component != null)
            .Select(e => new ComponentTimelineEntry
            {
                Component = e.Component!,
                Operation = e.Operation ?? "unknown",
                Status = e.EventType switch
                {
                    "error" => "failed",
                    "cancelled" => "cancelled",
                    "done" => "succeeded",
                    _ => "started",
                },
                StartedAt = DateTimeOffset.Parse(e.RecordedAt),
                DurationMs = null, // 单条事件无法确定 duration
            })
            .ToList();

        // LLM 调用：从 usage 事件解析 token 信息
        // ARCH-HARDEN-006：usage 解析支持多种字段命名风格
        var llmCalls = new List<LlmCallEntry>();
        foreach (var e in events.Where(ev => ev.EventType == "usage"))
        {
            try
            {
                using var doc = JsonDocument.Parse(e.Data);
                var root = doc.RootElement;
                var (promptTokens, completionTokens, _) = ParseTokenUsage(root);

                llmCalls.Add(new LlmCallEntry
                {
                    Model = root.TryGetProperty("model", out var m) ? m.GetString() : null,
                    Endpoint = root.TryGetProperty("endpoint", out var ep) ? ep.GetString() : null,
                    InputTokens = promptTokens > 0 ? promptTokens : null,
                    OutputTokens = completionTokens > 0 ? completionTokens : null,
                    DurationMs = root.TryGetProperty("durationMs", out var d) ? d.GetInt64() : null,
                });
            }
            catch
            {
                // 跳过无法解析的 usage 数据
            }
        }

        // Token 总量
        var totalTokens = llmCalls.Sum(c => (c.InputTokens ?? 0) + (c.OutputTokens ?? 0));

        // 工具调用：配对 tool_call 和 tool_result
        var toolCalls = new List<ToolCallEntry>();
        var toolCallEvents = events.Where(e => e.EventType == "tool_call").ToList();
        var toolResultEvents = events.Where(e => e.EventType == "tool_result").ToList();

        foreach (var tc in toolCallEvents)
        {
            string? toolName = null;
            try
            {
                using var doc = JsonDocument.Parse(tc.Data);
                toolName = doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() : "unknown";
            }
            catch { toolName = "unknown"; }

            // 查找对应的 tool_result（简单按后续第一条 tool_result 匹配）
            var tcIdx = events.IndexOf(tc);
            var matchedResult = events.Skip(tcIdx + 1).FirstOrDefault(e => e.EventType == "tool_result");
            var success = matchedResult != null; // 有 tool_result 即视为成功
            long? durationMs = null;
            if (matchedResult != null)
            {
                var tcTs = DateTimeOffset.Parse(tc.RecordedAt);
                var trTs = DateTimeOffset.Parse(matchedResult.RecordedAt);
                durationMs = (long)(trTs - tcTs).TotalMilliseconds;
            }

            toolCalls.Add(new ToolCallEntry
            {
                ToolName = toolName!,
                Success = success,
                DurationMs = durationMs,
            });
        }

        // 子代理调用树：subagent.spawned 和 subagent.completed
        var subAgents = new List<SubAgentTraceEntry>();
        foreach (var e in events.Where(ev => ev.EventType is "subagent.spawned" or "subagent.completed"))
        {
            string? subAgentId = e.SubAgentId;
            if (subAgentId == null)
            {
                try
                {
                    using var doc = JsonDocument.Parse(e.Data);
                    subAgentId = doc.RootElement.TryGetProperty("subAgentId", out var sai) ? sai.GetString() : null;
                }
                catch (JsonException)
                {
                    // malformed data JSON — skip this event for sub-agent lookup
                    continue;
                }
            }

            if (subAgentId == null) continue;

            var existing = subAgents.FirstOrDefault(s => s.SubAgentId == subAgentId);
            if (existing != null && e.EventType == "subagent.completed")
            {
                // 更新已存在的 spawned 条目：计算 duration
                var spawnedTs = DateTimeOffset.Parse(events
                    .First(ev => ev.EventType == "subagent.spawned" && (ev.SubAgentId == subAgentId || ev.Data.Contains(subAgentId)))
                    .RecordedAt);
                var completedTs = DateTimeOffset.Parse(e.RecordedAt);
                subAgents.Remove(existing);
                subAgents.Add(new SubAgentTraceEntry
                {
                    SubAgentId = subAgentId,
                    Status = e.EventType == "subagent.completed" ? "completed" : "running",
                    DurationMs = (long)(completedTs - spawnedTs).TotalMilliseconds,
                    ParentExecutionId = e.ParentExecutionId,
                });
            }
            else if (existing == null)
            {
                subAgents.Add(new SubAgentTraceEntry
                {
                    SubAgentId = subAgentId,
                    Status = e.EventType == "subagent.completed" ? "completed" : "running",
                    DurationMs = null,
                    ParentExecutionId = e.ParentExecutionId,
                });
            }
        }

        _logger.LogDebug(
            "[SSM] Trace report session={Session} events={EventCount} traces={TraceCount} llmCalls={LlmCount} toolCalls={ToolCount} subAgents={SubCount} durationMs={DurationMs} tokens={Tokens}",
            sessionId, events.Count, traceIds.Count, llmCalls.Count, toolCalls.Count, subAgents.Count, totalDurationMs, totalTokens);

        return new SessionTraceReport
        {
            SessionId = sessionId,
            TraceIds = traceIds,
            ComponentTimeline = componentTimeline,
            LlmCalls = llmCalls,
            ToolCalls = toolCalls,
            SubAgents = subAgents,
            TotalDurationMs = totalDurationMs,
            TotalTokens = totalTokens,
        };
    }

    /// <summary>
    /// 兼容解析 token usage payload，支持多种字段命名风格：
    ///   - promptTokens / completionTokens / totalTokens (camelCase)
    ///   - PromptTokens / CompletionTokens / TotalTokens (PascalCase)
    ///   - inputTokens / outputTokens / totalTokens (alternative)
    /// 返回 (promptTokens, completionTokens, totalTokens)，解析失败返回 (0,0,0)。
    /// ARCH-HARDEN-006：Trace Report Token Usage 兼容解析。
    /// </summary>
    private static (long prompt, long completion, long total) ParseTokenUsage(object? usagePayload)
    {
        if (usagePayload is null)
            return (0, 0, 0);

        if (usagePayload is not JsonElement root)
            return (0, 0, 0);

        // 尝试多种命名风格的 prompt/input tokens
        long prompt = TryGetInt64(root, "promptTokens")
            ?? TryGetInt64(root, "PromptTokens")
            ?? TryGetInt64(root, "inputTokens")
            ?? TryGetInt64(root, "InputTokens")
            ?? 0;

        // 尝试多种命名风格的 completion/output tokens
        long completion = TryGetInt64(root, "completionTokens")
            ?? TryGetInt64(root, "CompletionTokens")
            ?? TryGetInt64(root, "outputTokens")
            ?? TryGetInt64(root, "OutputTokens")
            ?? 0;

        // 尝试多种命名风格的 total tokens
        long total = TryGetInt64(root, "totalTokens")
            ?? TryGetInt64(root, "TotalTokens")
            ?? TryGetInt64(root, "total_tokens")
            ?? prompt + completion;

        return (prompt, completion, total);
    }

    /// <summary>从 JsonElement 安全读取 Int64 属性，不存在或类型不匹配返回 null。</summary>
    private static long? TryGetInt64(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.Number)
            {
                try { return prop.GetInt64(); }
                catch { return null; }
            }
        }
        return null;
    }
}

/// <summary>
/// Per-session fan-out for live SSE frames. A Channel has a single-consumer reader,
/// so every client connection must get its own Channel to avoid stealing frames
/// from other subscribers.
/// </summary>
internal sealed class SessionChannelFanout
{
    private readonly ConcurrentDictionary<Guid, Channel<ServerSentEventFrame>> _subscribers = new();
    private readonly ConcurrentDictionary<ChannelReader<ServerSentEventFrame>, Guid> _readerIndex = new();

    public int SubscriberCount => _subscribers.Count;

    public ChannelReader<ServerSentEventFrame> Subscribe()
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<ServerSentEventFrame>(
            new BoundedChannelOptions(256)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            });

        _subscribers[id] = channel;
        _readerIndex[channel.Reader] = id;
        return channel.Reader;
    }

    public bool Unsubscribe(ChannelReader<ServerSentEventFrame> reader)
    {
        if (!_readerIndex.TryRemove(reader, out var id))
            return false;

        if (!_subscribers.TryRemove(id, out var channel))
            return false;

        channel.Writer.TryComplete();
        return true;
    }

    public bool Publish(ServerSentEventFrame frame)
    {
        var delivered = false;
        foreach (var subscriber in _subscribers)
            delivered |= subscriber.Value.Writer.TryWrite(frame);

        return delivered;
    }

    public void Complete()
    {
        foreach (var subscriber in _subscribers)
            subscriber.Value.Writer.TryComplete();

        _subscribers.Clear();
        _readerIndex.Clear();
    }
}

internal sealed class SessionSequenceState
{
    public bool Initialized { get; set; }
    public long NextSequence { get; set; }
}

internal sealed record BufferedSessionEvent(
    string SessionId,
    string WorkspaceId,
    long SequenceNum,
    ServerSentEventFrame Frame,
    string RecordedAt,
    RuntimeTraceContext Trace,
    string Component,
    string Operation);

internal sealed class SessionEventBatchBuffer
{
    private readonly object _sync = new();
    private readonly List<BufferedSessionEvent> _events = new();
    private int _flushScheduled;

    public SemaphoreSlim FlushGate { get; } = new(1, 1);

    public int Count
    {
        get
        {
            lock (_sync)
                return _events.Count;
        }
    }

    public int Enqueue(BufferedSessionEvent item)
    {
        lock (_sync)
        {
            _events.Add(item);
            return _events.Count;
        }
    }

    public List<BufferedSessionEvent> Drain()
    {
        lock (_sync)
        {
            if (_events.Count == 0)
                return new List<BufferedSessionEvent>();

            var drained = new List<BufferedSessionEvent>(_events);
            _events.Clear();
            return drained;
        }
    }

    public bool TrySchedule()
        => Interlocked.Exchange(ref _flushScheduled, 1) == 0;

    public void MarkScheduleConsumed()
        => Volatile.Write(ref _flushScheduled, 0);
}
