using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;
using PuddingCode.Platform;
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
        ILogger<SessionStateManager> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
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

    public async Task<long> AppendAsync(
        string sessionId, string workspaceId,
        ServerSentEventFrame frame,
        CancellationToken ct = default)
    {
        var recordedAt = DateTimeOffset.UtcNow.ToString("O");
        long seq;

        // 1. 持久化到 SQLite
        using var scope = _scopeFactory.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

        var seqLock = _seqLocks.GetOrAdd(sessionId, _ => new object());
        lock (seqLock)
        {
            var maxSeq = db.SessionEventLogs
                .Where(e => e.SessionId == sessionId)
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
            };
            db.SessionEventLogs.Add(entity);
        }

        await db.SaveChangesAsync(ct);

        _logger.LogDebug("[SSM] Append frame session={Session} type={EventType} seq={Seq}", sessionId, frame.Event, seq);

        // 2. 推送到内存 Channel（实时推送）
        var channel = _sessionChannels.GetOrAdd(sessionId, _ =>
            Channel.CreateBounded<ServerSentEventFrame>(
                new BoundedChannelOptions(256)
                {
                    FullMode = BoundedChannelFullMode.DropOldest
                }));

        var pushed = channel.Writer.TryWrite(frame);
        if (frame.Event == "delta" || frame.Event == "done" || frame.Event == "metadata")
            _logger.LogWarning("[SSM] Push channel session={Session} type={EventType} seq={Seq} ok={Ok}", sessionId, frame.Event, seq, pushed);

        // 3. 如有 done/error/cancelled 帧 → 标记流式完成
        if (frame.Event is "done" or "error" or "cancelled")
        {
            var isErr = frame.Event == "error";
            _logger.LogDebug("[SSM] Stream complete session={Session} event={Event} error={IsErr}", sessionId, frame.Event, isErr);
            await MarkStreamCompleteAsync(sessionId, ct);
        }

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
}
