using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
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
/// SessionStateManager — ISessionStateManager 的 SQLite + Channel 实现。
/// 
/// 三大职责：
///   1. 持久化事件日志（session_event_log 表，append-only）
///   2. 实时推送通道（Channel per session，生命周期独立于 HTTP 连接）
///   3. 子代理状态追踪（session_sub_agents 表）
/// 
/// Singleton 生命周期。使用 IDbContextFactory 解决 Singleton 与 Scoped DbContext 的冲突。
/// 关联 ADR：Docs/07架构/16会话状态层与客户端解耦ADR.md
/// </summary>
public sealed class SessionStateManager : ISessionStateManager
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SessionStateManager> _logger;
    private readonly IRuntimeActivitySink _activitySink;
    private readonly IRuntimeTraceAccessor _traceAccessor;
    private readonly JsonlSessionWriter _jsonlWriter;

    // 会话 Channel（sessionId → Channel）
    private readonly ConcurrentDictionary<string, Channel<ServerSentEventFrame>> _sessionChannels = new();

    // 工作区通知 Channel（workspaceId → Channel）
    private readonly ConcurrentDictionary<string, Channel<SessionNotification>> _workspaceChannels = new();

    // 会话状态追踪
    private readonly ConcurrentDictionary<string, SessionState> _sessionStates = new();

    // 序列号生成锁（per session，保证递增）
    private readonly ConcurrentDictionary<string, object> _seqLocks = new();

    // Channel TTL 倒计时
    private static readonly TimeSpan ChannelTtl = TimeSpan.FromMinutes(30);

    public SessionStateManager(
        IServiceScopeFactory scopeFactory,
        ILogger<SessionStateManager> logger,
        IRuntimeActivitySink activitySink,
        IRuntimeTraceAccessor traceAccessor,
        JsonlSessionWriter jsonlWriter)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _activitySink = activitySink;
        _traceAccessor = traceAccessor;
        _jsonlWriter = jsonlWriter;
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
        long seq;

        // 1. 持久化到 SQLite（主存储，失败抛异常，不继续 JSONL）
        using var scope = _scopeFactory.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

        var seqLock = _seqLocks.GetOrAdd(sessionId, _ => new object());
        lock (seqLock)
        {
            var maxSeq = db.SessionEventLogs
                .Where(wqe => wqe.SessionId == sessionId)
                .Max(e => (long?)e.SequenceNum) ?? 0;
            seq = maxSeq + 1;

            var entity = new SessionEventLogEntity
            {
                SessionId = sessionId,
                WorkspaceId = workspaceId,
                SequenceNum = seq,
                EventType = frame.Event,
                Data = frame.Data,
                RecordedAt = recordedAt,
                TraceId = effectiveTrace.TraceId,
                CorrelationId = effectiveTrace.CorrelationId,
                ExecutionId = effectiveTrace.ExecutionId,
                ParentExecutionId = effectiveTrace.ParentExecutionId,
                SubAgentId = effectiveTrace.SubAgentId,
                Component = effectiveComponent,
                Operation = effectiveOperation,
            };
            db.SessionEventLogs.Add(entity);
        }

        await db.SaveChangesAsync(ct);

        _logger.LogDebug("[SSM] Append frame session={Session} type={EventType} seq={Seq}", sessionId, frame.Event, seq);

        // 2. 写入 JSONL（fire-and-forget 备份，失败不阻断主路径）
        //    JSONL 仅作为文本备份，SQLite 是权威数据源。
        //    JSONL 写入失败仅记录 Warning，不影响调用方感知的成功结果。
        try
        {
            _jsonlWriter.WriteEventLine(sessionId, frame.Event, frame.Data, seq, recordedAt);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[SSM] JSONL write failed (non-fatal) session={Session} type={EventType} seq={Seq}",
                sessionId, frame.Event, seq);
        }

        // 3. 推送到内存 Channel（实时推送）
        var channel = _sessionChannels.GetOrAdd(sessionId, _ =>
            Channel.CreateBounded<ServerSentEventFrame>(
                new BoundedChannelOptions(256)
                {
                    FullMode = BoundedChannelFullMode.DropOldest
                }));

        var pushed = channel.Writer.TryWrite(frame);
        if (frame.Event == "delta" || frame.Event == "done" || frame.Event == "metadata")
            _logger.LogWarning("[SSM] Push channel session={Session} type={EventType} seq={Seq} ok={Ok}", sessionId, frame.Event, seq, pushed);

        // 4. 如有 done/error/cancelled 帧 → 标记流式完成
        if (frame.Event is "done" or "error" or "cancelled")
        {
            var isErr = frame.Event == "error";
            _logger.LogDebug("[SSM] Stream complete session={Session} event={Event} error={IsErr}", sessionId, frame.Event, isErr);
            await MarkStreamCompleteAsync(sessionId, ct);
        }

        await _activitySink.RecordAsync(new RuntimeActivity
        {
            Trace = effectiveTrace,
            Component = RuntimeActivityComponents.SessionState,
            Operation = effectiveOperation,
            Status = RuntimeActivityStatuses.Succeeded,
            StartedAtUtc = DateTimeOffset.Parse(recordedAt),
            EndedAtUtc = DateTimeOffset.UtcNow,
            Summary = $"Appended session event {frame.Event}",
            Metadata = new Dictionary<string, string>
            {
                ["sequence"] = seq.ToString(),
                ["eventType"] = frame.Event,
                ["sourceComponent"] = effectiveComponent,
            },
        }, ct);

        return seq;
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

    // ════════════════════════════════════════════════════════
    // 实时订阅
    // ════════════════════════════════════════════════════════

    public ChannelReader<ServerSentEventFrame>? Subscribe(string sessionId)
    {
        if (_sessionChannels.TryGetValue(sessionId, out var ch))
            return ch.Reader;

        // Channel 不存在且会话已关闭 → 返回 null
        if (_sessionStates.TryGetValue(sessionId, out var state) && state == SessionState.Closed)
            return null;

        // 否则创建新 Channel（可能后续有事件产生）
        var newCh = _sessionChannels.GetOrAdd(sessionId, _ =>
            Channel.CreateBounded<ServerSentEventFrame>(
                new BoundedChannelOptions(256)
                {
                    FullMode = BoundedChannelFullMode.DropOldest
                }));
        return newCh.Reader;
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
        entity.ReplySummary = result.Reply?.Length > 200
            ? result.Reply[..200] + "..."
            : result.Reply;
        entity.ErrorSummary = result.Error?.Length > 500
            ? result.Error[..500]
            : result.Error;

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

        return await db.SessionSubAgents
            .CountAsync(e => e.ParentSessionId == parentSessionId && e.Status == "running", ct);
    }

    // ════════════════════════════════════════════════════════
    // 生命周期标记
    // ════════════════════════════════════════════════════════

    public Task MarkStreamCompleteAsync(string sessionId, CancellationToken ct = default)
    {
        _sessionStates[sessionId] = SessionState.StreamCompleted;
        _logger.LogDebug("[SSM] Stream completed session={Session}", sessionId);
        return Task.CompletedTask;
    }

    public Task MarkSessionClosedAsync(string sessionId, CancellationToken ct = default)
    {
        _sessionStates[sessionId] = SessionState.Closed;
        _logger.LogDebug("[SSM] Session closed session={Session}", sessionId);
        _logger.LogInformation("[SSM] Session closed session={Session}", sessionId);

        // 启动 TTL 倒计时后清理 Channel（不立即清理，给重连窗口）
        _ = ScheduleChannelCleanupAsync(sessionId);

        return Task.CompletedTask;
    }

    // ════════════════════════════════════════════════════════
    // 内部方法
    // ════════════════════════════════════════════════════════

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
            if (_sessionChannels.TryRemove(sessionId, out var ch))
            {
                ch.Writer.TryComplete();
                _logger.LogDebug("[SSM] Channel cleaned up session={Session}", sessionId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[SSM] Channel cleanup failed session={Session}", sessionId);
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
        var llmCalls = new List<LlmCallEntry>();
        foreach (var e in events.Where(ev => ev.EventType == "usage"))
        {
            try
            {
                using var doc = JsonDocument.Parse(e.Data);
                var root = doc.RootElement;
                llmCalls.Add(new LlmCallEntry
                {
                    Model = root.TryGetProperty("model", out var m) ? m.GetString() : null,
                    Endpoint = root.TryGetProperty("endpoint", out var ep) ? ep.GetString() : null,
                    InputTokens = root.TryGetProperty("inputTokens", out var it) ? it.GetInt64() : null,
                    OutputTokens = root.TryGetProperty("outputTokens", out var ot) ? ot.GetInt64() : null,
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
                catch { }
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
}
