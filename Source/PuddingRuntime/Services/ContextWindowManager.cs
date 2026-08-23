using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using PuddingCode.Models;
using PuddingCode.Observability;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingCode.Services;
using PuddingMemoryEngine.Data;
using PuddingMemoryEngine.Entities;
using PuddingRuntime.Services.AgentLoop;

namespace PuddingRuntime.Services;

/// <summary>
/// 上下文窗口与会话历史管理器。
/// 负责内存历史、DB/JSONL 回填、历史裁剪与过期清理。
/// </summary>
public sealed class ContextWindowManager
{


    private readonly AgentSessionManager _sessionManager;
    private readonly InMemoryRuntimeSessionStore _runtimeSessionStore;
    private readonly ExecutionControlRegistry _controlRegistry;
    private readonly ExecutionJournal _journal;
    private readonly IDbContextFactory<MemoryDbContext>? _memoryDbFactory;
    private readonly PuddingCode.Services.JsonlSessionReader? _jsonlReader;
    private readonly IContextCompactionService? _compactionService;
    private readonly int _defaultToolCount;
    private readonly ConcurrentDictionary<string, List<ChatMessage>> _histories;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _historyLastAccessedAt;
    private readonly ConcurrentDictionary<string, TimeSpan> _historyTimeouts;
    private readonly ConcurrentDictionary<string, int> _activeExecutions;
    private readonly ILogger<ContextWindowManager> _logger;
    private readonly AgentCompactionNotifier? _compactionNotifier;
    private readonly IPreCompactionFlushService? _preCompactionFlushService;
    private readonly ContextCompactionOptions? _compactionOptions;
    private readonly ISessionCompactionEventEmitter? _compactionEventEmitter;
    private readonly ITelemetryMetricSink? _telemetrySink;
    private readonly IContextTierPlanner _tierPlanner;

        // 工作总结重试跟踪：每个 session 注入提示词的次数和首次注入时间
    private readonly ConcurrentDictionary<string, int> _workSummaryRetryCount = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _workSummaryFirstInjectedAt = new();
    private readonly ContextCompactionStrategy _strategy;

    // Per-session dedup of inbound message IDs to prevent duplicate LLM history entries
    // when Ack loss triggers retry dispatch of the same message.
    private readonly ConcurrentDictionary<string, HashSet<string>> _dispatchedMessageIds = new();

    private sealed record HydratedHistorySnapshot(List<ChatMessage> Messages, long LastCreatedAt);

    public ContextWindowManager(
        AgentSessionManager sessionManager,
        InMemoryRuntimeSessionStore runtimeSessionStore,
        ExecutionControlRegistry controlRegistry,
        ExecutionJournal journal,
        ILogger<ContextWindowManager> logger,
        IDbContextFactory<MemoryDbContext>? memoryDbFactory = null,
        PuddingCode.Services.JsonlSessionReader? jsonlReader = null,
        IContextCompactionService? compactionService = null,
        AgentCompactionNotifier? compactionNotifier = null,
        IPreCompactionFlushService? preCompactionFlushService = null,
        ContextCompactionOptions? compactionOptions = null,
        ISessionCompactionEventEmitter? compactionEventEmitter = null,
        ITelemetryMetricSink? telemetrySink = null,
        int defaultToolCount = 50,
        IContextTierPlanner? tierPlanner = null)
    {
        _sessionManager = sessionManager;
        _runtimeSessionStore = runtimeSessionStore;
        _controlRegistry = controlRegistry;
        _journal = journal;
        _logger = logger;
        _memoryDbFactory = memoryDbFactory;
        _jsonlReader = jsonlReader;
        _compactionService = compactionService;
        _compactionNotifier = compactionNotifier;
        _preCompactionFlushService = preCompactionFlushService;
        _compactionOptions = compactionOptions;
        _compactionEventEmitter = compactionEventEmitter;
        _telemetrySink = telemetrySink;
                _defaultToolCount = defaultToolCount;
        _tierPlanner = tierPlanner ?? new ContextTierPlanner();

        _strategy = new ContextCompactionStrategy(_logger, _workSummaryRetryCount, _workSummaryFirstInjectedAt);

        _histories = new ConcurrentDictionary<string, List<ChatMessage>>();
        _historyLastAccessedAt = new ConcurrentDictionary<string, DateTimeOffset>();
        _historyTimeouts = new ConcurrentDictionary<string, TimeSpan>();
        _activeExecutions = new ConcurrentDictionary<string, int>();
    }

    public List<ChatMessage> GetOrCreateHistory(string sessionId)
    {
        return _histories.GetOrAdd(sessionId, _ => []);
    }

    public void TouchHistoryAccess(string sessionId, TimeSpan sessionTimeout)
    {
        _historyLastAccessedAt[sessionId] = DateTimeOffset.UtcNow;
        _historyTimeouts[sessionId] = NormalizeSessionTimeout(sessionTimeout);
    }

    /// <summary>
    /// 失效指定 session 的内存历史。压缩写库成功后调用：移除内存历史，
    /// 使下次访问通过 TryHydrateStreamHistoryFromDbAsync 从 DB 重新水合
    /// （此时已含压缩摘要与 CompactedBy 打标），解决压缩后内存历史 stale 的缺口。
    /// 只清理历史相关状态（_histories/_historyLastAccessedAt/_historyTimeouts），
    /// 不清除会话管理状态（_runtimeSessionStore/_controlRegistry/_journal/_dispatchedMessageIds）。
    /// </summary>
    public void InvalidateHistory(string sessionId)
    {
        _histories.TryRemove(sessionId, out _);
        _historyLastAccessedAt.TryRemove(sessionId, out _);
        _historyTimeouts.TryRemove(sessionId, out _);
    }

    /// <summary>
    /// 标记消息已成功 dispatch。首次调用返回 true，重复调用返回 false。
    /// 用于运行时层入站消息去重：同一 message_id 因 Ack 丢失/重试被重复 dispatch 时，
    /// 不再重复进入 LLM 历史、不再重复执行。
    /// </summary>
    public bool TryMarkMessageDispatched(string sessionId, string messageId)
    {
        if (string.IsNullOrWhiteSpace(messageId))
            return true; // 无 messageId 的消息不做去重

        var set = _dispatchedMessageIds.GetOrAdd(sessionId, _ => new HashSet<string>());
        lock (set)
        {
            return set.Add(messageId);
        }
    }

    /// <summary>
    /// 取消消息 dispatch 标记——执行失败时调用，允许后续重试。
    /// </summary>
    public void UnmarkMessageDispatched(string sessionId, string messageId)
    {
        if (string.IsNullOrWhiteSpace(messageId))
            return;

        if (_dispatchedMessageIds.TryGetValue(sessionId, out var set))
        {
            lock (set)
            {
                set.Remove(messageId);
            }
        }
    }

    public void MarkSessionExecuting(string sessionId)
    {
        _activeExecutions.AddOrUpdate(sessionId, 1, static (_, current) => current + 1);
    }

    public void MarkSessionExecutionCompleted(string sessionId)
    {
        while (true)
        {
            if (!_activeExecutions.TryGetValue(sessionId, out var current))
                return;

            if (current <= 1)
            {
                _activeExecutions.TryRemove(sessionId, out _);
                return;
            }

            if (_activeExecutions.TryUpdate(sessionId, current - 1, current))
                return;
        }
    }

    public void CleanupExpiredSessions(string protectedSessionId)
    {
        CleanupExpiredHistories(protectedSessionId);

        var expiredSessions = _sessionManager.CleanupExpired(
            protectedSessionId,
            shouldSkip: IsSessionExecuting);

        if (expiredSessions.Count == 0)
            return;

        foreach (var sessionId in expiredSessions)
        {
            _histories.TryRemove(sessionId, out _);
            _historyLastAccessedAt.TryRemove(sessionId, out _);
            _historyTimeouts.TryRemove(sessionId, out _);

            _runtimeSessionStore.Terminate(sessionId, "timeout");
            _controlRegistry.Remove(sessionId);
            _journal.ClearAnchor(sessionId);
            _dispatchedMessageIds.TryRemove(sessionId, out _);
        }

        _logger.LogInformation(
            "[AgentExec] Cleanup removed {Count} expired sessions (protected={ProtectedSession})",
            expiredSessions.Count,
            protectedSessionId);
    }

    private void CleanupExpiredHistories(string protectedSessionId)
    {
        var now = DateTimeOffset.UtcNow;
        var removedCount = 0;

        foreach (var pair in _historyLastAccessedAt)
        {
            var sessionId = pair.Key;
            if (string.Equals(sessionId, protectedSessionId, StringComparison.Ordinal))
                continue;

            if (IsSessionExecuting(sessionId))
                continue;

            if (_sessionManager.IsWaitingEvent(sessionId))
                continue;

            var timeout = NormalizeSessionTimeout(_historyTimeouts.GetValueOrDefault(sessionId));
            if (now - pair.Value <= timeout)
                continue;

            if (_histories.TryRemove(sessionId, out _))
                removedCount++;

            _historyLastAccessedAt.TryRemove(sessionId, out _);
            _historyTimeouts.TryRemove(sessionId, out _);
            _dispatchedMessageIds.TryRemove(sessionId, out _);
        }

        if (removedCount > 0)
        {
            _logger.LogInformation(
                "[AgentExec] Cleanup removed {Count} expired history entries (protected={ProtectedSession})",
                removedCount,
                protectedSessionId);
        }
    }

    private bool IsSessionExecuting(string sessionId) =>
        _activeExecutions.ContainsKey(sessionId);

    /// <summary>
    /// 从数据库构建会话上下文窗口（优先供 SSE 流式路径使用）。
    /// V1：按 token 预算（默认 8000）从新到旧取候选，再按旧到新组装，跳过已压缩消息。
    /// query 非空时，正文命中 query 的旧证据在 Tier 分级时被临时晋升（§8.1 有界晋升），
    /// 预算紧张时优先保留，避免被冷数据裁剪误伤。
    /// </summary>
    public async Task<List<ChatMessage>> BuildContextFromDbAsync(
        string sessionId,
                int maxTokenBudget = ContextWindowConstants.DefaultMaxTokenBudget,
        CancellationToken ct = default,
        string? query = null)
        => (await BuildContextFromDbSnapshotAsync(sessionId, maxTokenBudget, ct, query)).Messages;

    private async Task<HydratedHistorySnapshot> BuildContextFromDbSnapshotAsync(
        string sessionId,
        int maxTokenBudget = ContextWindowConstants.DefaultMaxTokenBudget,
        CancellationToken ct = default,
        string? query = null)
    {
        if (_memoryDbFactory is null)
            return new([], 0);

        await using var db = await _memoryDbFactory.CreateDbContextAsync(ct);
        var entities = await db.Messages
            .AsNoTracking()
            .Where(m => m.SessionId == sessionId && m.CompactedBy == null)
            .OrderByDescending(m => m.CreatedAt)
            .Take(ContextWindowConstants.MaxDbFetchMessages)
            .ToListAsync(ct);

        // 按 Sequence 升序得到稳定有序的消息列表（Sequence 是稳定顺序）。
        var ordered = entities.OrderBy(m => m.Sequence).ToList();

        // Tier 化分级：user 消息为轮次边界，最后 user 轮视为当前轮（T0），保证最近轮全保真。
        var inputs = MapToTierInputs(ordered, query);
        var plan = _tierPlanner.Plan(inputs, options: null);
        var assignments = plan.Assignments;

        // 按 Tier 保真度填充：T0 → T4，同 tier 内新的（Sequence 大）在前；
        // 预算耗尽后更冷 tier 整体不再填充（保新弃旧，替代旧的"从旧到新累加 + break"）。
        var selected = new List<MessageEntity>(ordered.Count);
        var estimatedTokens = 0;
        var budgetExhausted = false;

        foreach (var tier in Enum.GetValues<ContextSegmentTier>().OrderBy(t => (int)t))
        {
            if (budgetExhausted)
                break;

            foreach (var (entity, _) in assignments
                         .Select((a, i) => (Entity: ordered[i], a.Tier))
                         .Where(p => p.Tier == tier)
                         .OrderByDescending(p => p.Entity.Sequence))
            {
                var content = entity.Content ?? string.Empty;

                // 简化估算：约 1 token ≈ 3 字符（中英混排折中），最少按 1 token 计。
                var tokenEstimate = Math.Max(1, content.Length / ContextWindowConstants.TokenEstimateCharDivisor);
                if (estimatedTokens + tokenEstimate > maxTokenBudget && selected.Count > ContextWindowConstants.MinMessagesBeforeTokenBreak)
                {
                    budgetExhausted = true;
                    break;
                }

                estimatedTokens += tokenEstimate;
                selected.Add(entity);
            }
        }

        // 最终输出仍按 Sequence 升序（旧→新），与历史直觉一致。
        var lastCreatedAt = selected.Count > 0 ? selected.Max(m => m.CreatedAt) : 0L;
        var messages = selected
            .OrderBy(m => m.Sequence)
            .Select(entity => new ChatMessage(
                string.Equals(entity.ContentType, ContextWindowConstants.CompactSummaryContentType, StringComparison.OrdinalIgnoreCase)
                    ? ChatRole.Assistant
                    : ParseChatRole(entity.Role),
                entity.Content ?? string.Empty,
                ToolCallId: null,
                ToolCalls: null,
                ToolName: null,
                ReasoningContent: null,
                // ADR-077 §7.1：DB 水合必须恢复图片 part，不能只构造纯文本消息。
                ContentParts: ContentPartsEnvelope.Decode(entity.AttachmentsJson)))
            .ToList();

        return new(SanitizeForLlmContext(messages), lastCreatedAt);
    }

    /// <summary>
    /// SSE 路径优先按数据库重建上下文；失败时保持现有内存历史不变。
    /// </summary>
    public async Task TryHydrateStreamHistoryFromDbAsync(
        string sessionId,
        List<ChatMessage> history,
        int maxTokenBudget,
        CancellationToken ct,
        string? query = null)
    {
        try
        {
            var inMemorySource = history
                .Where(message => !IsRuntimeControlMessage(message))
                .ToList();
            var normalizedInMemory = LlmMessageSequenceNormalizer.Normalize(inMemorySource);
            var removedRuntimeControls = history.Count - inMemorySource.Count;
            if (normalizedInMemory.Changed || removedRuntimeControls > 0)
            {
                history.Clear();
                history.AddRange(normalizedInMemory.Messages);
                _logger.LogWarning(
                    "[AgentExec] Repaired in-memory LLM history before hydration session={Session} incompleteToolRounds={IncompleteToolRounds} orphanToolMessages={OrphanToolMessages} runtimeControls={RuntimeControls}",
                    sessionId,
                    normalizedInMemory.RepairedIncompleteToolRounds,
                    normalizedInMemory.DroppedOrphanToolMessages,
                    removedRuntimeControls);
            }

            HydratedHistorySnapshot? hydrated = null;

            if (_memoryDbFactory is not null)
            {
                var dbHistory = await BuildContextFromDbSnapshotAsync(sessionId, maxTokenBudget, ct, query);
                if (dbHistory.Messages.Count > 0)
                {
                    hydrated = dbHistory;
                }
            }

            if (_jsonlReader is not null)
            {
                var jsonlHistory = await BuildContextFromJsonlSnapshotAsync(sessionId, maxTokenBudget, ct, query);
                if (jsonlHistory.Messages.Count > 0
                    && (hydrated is null || jsonlHistory.LastCreatedAt > hydrated.LastCreatedAt))
                {
                    hydrated = jsonlHistory;
                }
            }

            if (hydrated is null || hydrated.Messages.Count == 0)
                return;

            var hydratedContext = SanitizeForLlmContext(hydrated.Messages.Where(m => m.Role != ChatRole.System));

            // Apply history pruning if enabled (Batch2-4)
            if (_compactionOptions?.EnableHistoryPruning == true)
            {
                var maxPruned = _compactionOptions.HistoryPruningMaxMessages > 0
                    ? _compactionOptions.HistoryPruningMaxMessages
                    : ContextWindowConstants.DefaultHistoryPruningMaxMessages;
                var originalCount = hydratedContext.Count;
                var prunedMessages = ContextPipeline.PruneSessionMessages(hydratedContext, maxPruned);
                hydratedContext = prunedMessages.Select(pm => new ChatMessage(
                    Role: pm.Role == "user" ? ChatRole.User : ChatRole.Assistant,
                    Content: pm.Content
                )).ToList();
                _logger.LogInformation(
                    "[AgentExec] History pruning applied session={Session} original={OriginalCount} pruned={PrunedCount} maxMessages={MaxMessages}",
                    sessionId, originalCount, hydratedContext.Count, maxPruned);
            }

            var existingContextCount = history.Count(m => m.Role != ChatRole.System);
            if (existingContextCount > 0 && hydratedContext.Count < existingContextCount)
            {
                _logger.LogInformation(
                    "[AgentExec] Skip stream history hydration because persisted context is shorter than in-memory context. session={Session} persisted={PersistedCount} memory={MemoryCount}",
                    sessionId,
                    hydratedContext.Count,
                    existingContextCount);
                return;
            }

            history.Clear();
            history.AddRange(hydratedContext);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "[AgentExec] Build context from db/jsonl failed; fallback to in-memory history. session={Session}",
                sessionId);
        }
    }

    public async Task<List<ChatMessage>> BuildContextFromJsonlAsync(
        string sessionId,
        int maxTokenBudget,
        CancellationToken ct,
        string? query = null)
        => (await BuildContextFromJsonlSnapshotAsync(sessionId, maxTokenBudget, ct, query)).Messages;

    private async Task<HydratedHistorySnapshot> BuildContextFromJsonlSnapshotAsync(
        string sessionId,
        int maxTokenBudget,
        CancellationToken ct,
        string? query = null)
    {
        if (_jsonlReader is null)
            return new([], 0);

        var entries = (await _jsonlReader.ReadSessionAsync(sessionId, ct))
            .Where(IsJsonlMessageEntry)
            .ToList();
        if (entries.Count == 0)
            return new([], 0);

        var coverage = await LoadCoverageAsync(sessionId, ct);

        // generation 过滤：已被压缩覆盖的消息不再进入模型上下文（防止经 JSONL 旁路复活，方案 §9 去重规则）。
        var active = entries
            .Where(e => !coverage.CoveredMessageIds.Contains(e.MessageId))
            .ToList();
        if (active.Count == 0)
            return new([], 0);

        // 升序（旧→新）保证轮次推导正确；CreatedAt 全部为 0 时 OrderBy 稳定，保持原顺序。
        var ordered = active.OrderBy(e => e.CreatedAt).ToList();

        // Tier 分级（对齐 DB 路径 BuildContextFromDbSnapshotAsync）：
        // user 消息为轮次边界，最后 user 轮视为当前轮（T0），query 命中由 ContextTierPlanner 有界晋升。
        var inputs = MapToTierInputs(ordered, query);
        var plan = _tierPlanner.Plan(inputs, options: null);
        var assignments = plan.Assignments;

        // 按 Tier 保真度填充：T0 → T4，同 tier 内新的（CreatedAt 大）在前；
        // 预算耗尽后更冷 tier 整体不再填充（保新弃旧，替代旧的“倒序累加 + break”）。
        var selected = new List<JsonlEntry>(ordered.Count);
        var estimatedTokens = 0;
        var budgetExhausted = false;

        foreach (var tier in Enum.GetValues<ContextSegmentTier>().OrderBy(t => (int)t))
        {
            if (budgetExhausted)
                break;

            foreach (var (entry, _) in assignments
                         .Select((a, i) => (Entry: ordered[i], a.Tier))
                         .Where(p => p.Tier == tier)
                         .OrderByDescending(p => p.Entry.CreatedAt))
            {
                var content = entry.Content ?? string.Empty;

                // 简化估算：约 1 token ≈ 3 字符（中英混排折中），最少按 1 token 计。
                var tokenEstimate = Math.Max(1, content.Length / ContextWindowConstants.TokenEstimateCharDivisor);
                if (estimatedTokens + tokenEstimate > maxTokenBudget && selected.Count > ContextWindowConstants.MinMessagesBeforeTokenBreak)
                {
                    budgetExhausted = true;
                    break;
                }

                estimatedTokens += tokenEstimate;
                selected.Add(entry);
            }
        }

        // 最终输出按 CreatedAt 升序（旧→新），与历史直觉一致。
        // summary 消息处理保持旧行为：仅按 Role 映射，不特殊判断 ContentType（与 DB 路径不同，勿照搬）。
        var lastCreatedAt = selected.Count > 0 ? selected.Max(e => e.CreatedAt) : 0L;
        var messages = selected
            .OrderBy(e => e.CreatedAt)
            .Select(entry => new ChatMessage(
                ParseChatRole(entry.Role),
                entry.Content ?? string.Empty,
                ToolCallId: null,
                ToolCalls: null,
                ToolName: null,
                ReasoningContent: null))
            .ToList();

        return new(SanitizeForLlmContext(messages), lastCreatedAt);
    }

    private async Task<CompactionCoverage> LoadCoverageAsync(string sessionId, CancellationToken ct)
        => await new CompactionCoverageFilter(_memoryDbFactory).LoadAsync(sessionId, ct);

    private static bool IsJsonlMessageEntry(JsonlEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Content))
            return false;

        return entry.Role.Trim().ToLowerInvariant() is "user" or "assistant" or "agent" or "system" or "tool";
    }

    /// <summary>
    /// JSONL 冷启动专用：<see cref="JsonlEntry"/> 无 Sequence，用 CreatedAt 升序隐含顺序；
    /// Role == "user" 作为轮次边界（0-based 递增），最后 user 轮视为当前轮（T0）；
    /// query 命中（正文包含 query 的有效 token）→ IsQueryHit=true。
    /// internal 以便单测直接验证（沿用 ExtractQueryHits 的 InternalsVisibleTo 约定）。
    /// </summary>
    internal static List<TierPlannerSegmentInput> MapToTierInputs(
        IReadOnlyList<JsonlEntry> entries,
        string? query = null)
        => BuildTierInputs(
            entries,
            segmentIdSelector: (e, _) => e.MessageId,
            isUserBoundary: e => string.Equals(e.Role, "user", StringComparison.OrdinalIgnoreCase),
            contentSelector: e => e.Content ?? string.Empty,
            query);

    public async Task TrimHistoryAsync(
        string sessionId,
        List<ChatMessage> history,
        int maxTokenBudget,
        bool preferDbContextWindow,
        CancellationToken ct,
        string? traceId = null,
        string? query = null)
        => await TrimHistoryAsync(
            sessionId,
            history,
            maxTokenBudget,
            preferDbContextWindow,
            workspaceId: null,
            agentId: null,
            ct,
            traceId: traceId,
            query: query);

    public async Task TrimHistoryAsync(
        string sessionId,
        List<ChatMessage> history,
        int maxTokenBudget,
        bool preferDbContextWindow,
        string? workspaceId,
        string? agentId,
        CancellationToken ct,
        int? maxOutputTokens = null,
        int? maxInputTokens = null,
        string? agentTemplateId = null,
        string? traceId = null,
        string? query = null)
    {
        var autoCompacted = await TryAutoCompactAsync(
            sessionId,
            workspaceId,
            agentId,
            maxTokenBudget,
            maxOutputTokens,
            maxInputTokens,
            agentTemplateId,
            traceId,
            ct);

        if ((preferDbContextWindow || autoCompacted) && _memoryDbFactory is not null)
        {
            try
            {
                var dbHistory = await BuildContextFromDbAsync(sessionId, maxTokenBudget, ct, query);
                if (dbHistory.Count > 0)
                {
                    var system = history.FirstOrDefault(m => m.Role == ChatRole.System);
                    history.Clear();
                    if (system is not null)
                        history.Add(system);
                    history.AddRange(SanitizeForLlmContext(dbHistory.Where(m => m.Role != ChatRole.System)));
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "[AgentExec] TrimHistory db path failed; fallback to legacy trimming. session={Session}",
                    sessionId);
            }
        }

        TrimHistory(history, maxTokenBudget, query);
    }

    private bool _compactionServiceNullLogged;

    private async Task<bool> TryAutoCompactAsync(
        string sessionId,
        string? workspaceId,
        string? agentId,
        int maxTokenBudget,
        int? maxOutputTokens,
        int? maxInputTokens,
        string? agentTemplateId,
        string? traceId,
        CancellationToken ct)
    {
        if (_compactionService is null || string.IsNullOrWhiteSpace(workspaceId))
        {
            if (_compactionService is null && !_compactionServiceNullLogged)
            {
                _compactionServiceNullLogged = true;
                _logger.LogWarning("[ContextWindow] Auto compact disabled: IContextCompactionService not registered");
            }
            return false;
        }

        try
        {
            var compressionWatch = System.Diagnostics.Stopwatch.StartNew();
            var healthStart = System.Diagnostics.Stopwatch.GetTimestamp();
            var health = await _compactionService.GetHealthAsync(
                sessionId,
                ct,
                contextWindowTokens: maxTokenBudget,
                maxOutputTokens: maxOutputTokens,
                maxInputTokens: maxInputTokens,
                toolCount: _defaultToolCount);
            var healthMs = (System.Diagnostics.Stopwatch.GetTimestamp() - healthStart) * 1000 / System.Diagnostics.Stopwatch.Frequency;

            _logger.LogInformation(
                "[ContextWindow:AutoCompact] healthCheck session={Session} state={State} ratio={Ratio:F2} used={UsedTokens} effective={EffectiveTokens} remaining={RemainingTokens} budget={TokenBudget} elapsedMs={ElapsedMs}",
                sessionId, health.State, health.UsageRatio, health.UsedTokens, health.EffectiveWindowTokens, health.RemainingTokens, maxTokenBudget, healthMs);

            await RecordAutoCompactionMetricAsync(
                sessionId,
                workspaceId,
                agentId,
                TelemetryMetricStatuses.Recorded,
                "context.auto_compaction.health",
                numericValue: health.UsageRatio,
                durationMs: healthMs,
                dimensions: BuildHealthDimensions(health, maxTokenBudget),
                ct: ct);

            if (!health.ShouldAutoCompact)
            {
                compressionWatch.Stop();
                return false;
            }

            _logger.LogInformation(
                "[ContextWindow:AutoCompact] triggering session={Session} state={State} ratio={Ratio:F2} budget={TokenBudget}",
                sessionId, health.State, health.UsageRatio, maxTokenBudget);

            // 检查历史中是否有 Agent 的工作总结，由策略决定下一步动作
            var agentWorkSummary = ExtractAgentWorkSummaryFromHistory(sessionId);
            var decision = _strategy.Decide(
                sessionId, health, agentWorkSummary, _compactionOptions,
                hasCompactionNotifier: _compactionNotifier is not null);

            if (decision == CompactionDecision.WaitForSummary)
            {
                InjectAgentWorkSummaryPrompt(sessionId);

                await RecordAutoCompactionMetricAsync(
                    sessionId,
                    workspaceId,
                    agentId,
                    TelemetryMetricStatuses.Recorded,
                    "context.auto_compaction.waiting_summary",
                    countValue: _strategy.GetRetryCount(sessionId),
                    numericValue: _strategy.GetElapsedTime(sessionId).TotalSeconds,
                    dimensions: new Dictionary<string, string>
                    {
                        ["reason"] = _strategy.GetRetryCount(sessionId) <= 1 ? "first_injection" : "retry",
                        ["max_retries"] = (_compactionOptions?.MaxWorkSummaryRetries ?? ContextWindowConstants.DefaultMaxWorkSummaryRetries).ToString(CultureInfo.InvariantCulture),
                        ["max_wait_seconds"] = (_compactionOptions?.MaxWaitForWorkSummarySeconds ?? ContextWindowConstants.DefaultMaxWaitForWorkSummarySeconds).ToString(CultureInfo.InvariantCulture),
                        ["health_state"] = health.State.ToString(),
                        ["usage_ratio"] = health.UsageRatio.ToString("F4", CultureInfo.InvariantCulture),
                    },
                    ct: ct);

                return false;
            }

            var compactionId = Guid.NewGuid().ToString("N");

            await EmitCompactionLifecycleEventAsync(
                sessionId,
                workspaceId,
                SseEventTypes.ContextCompactionStarted,
                new
                {
                    compactionId,
                    sessionId,
                    mode = "Auto",
                    level = "Full",
                                        reason = ContextWindowConstants.AutoCompactionReason,
                    state = health.State.ToString(),
                    usageRatio = health.UsageRatio,
                    usedTokens = health.UsedTokens,
                    effectiveWindowTokens = health.EffectiveWindowTokens,
                    remainingTokens = health.RemainingTokens,
                    budget = maxTokenBudget,
                    agentId,
                },
                traceId,
                ct);

            await RecordAutoCompactionMetricAsync(
                sessionId,
                workspaceId,
                agentId,
                TelemetryMetricStatuses.Started,
                "context.auto_compaction",
                numericValue: health.UsageRatio,
                dimensions: BuildHealthDimensions(health, maxTokenBudget),
                ct: ct);

            // ---- Pre-Compaction Flush（借鉴 Claude Code）----
            // 压缩前用 Flash LLM 快速提取关键事实，防止信息丢失。
            // 失败不影响压缩继续执行。
            IReadOnlyList<string> preCompactionFacts = [];
            if (_preCompactionFlushService is not null)
            {
                preCompactionFacts = await FlushMemoriesBeforeCompactionAsync(
                    sessionId, workspaceId, agentId, agentTemplateId, agentWorkSummary, compactionId, ct);
            }

            var compactStart = System.Diagnostics.Stopwatch.GetTimestamp();
            var result = await _compactionService.CompactAsync(
                new ContextCompactionRequest(
                    workspaceId,
                    sessionId,
                    agentId,
                                        ContextCompactionMode.Auto,
                    ContextCompactionLevel.Full,
                    ContextWindowConstants.AutoCompactionReason,
                    AgentWorkSummary: agentWorkSummary,
                    CompactionId: compactionId,
                    AgentTemplateId: agentTemplateId,
                    PreCompactionFacts: preCompactionFacts.Count > 0 ? preCompactionFacts : null)
                {
                    TraceId = traceId,
                },
                ct);
            var compactMs = (System.Diagnostics.Stopwatch.GetTimestamp() - compactStart) * 1000 / System.Diagnostics.Stopwatch.Frequency;
            compressionWatch.Stop();

            var compressionRatio = result.BeforeTokens > 0
                ? (double)(result.BeforeTokens - result.AfterTokens) / result.BeforeTokens
                : 0.0;

            _logger.LogInformation(
                "[ContextWindow:AutoCompact] completed session={Session} agent={AgentId} compacted={CompactedCount} before={BeforeTokens} after={AfterTokens} ratio={CompressionRatio:P1} compactMs={CompactMs} totalMs={TotalMs} summaryId={SummaryId}",
                sessionId,
                agentId,
                result.CompactedMessageCount,
                result.BeforeTokens,
                result.AfterTokens,
                compressionRatio,
                compactMs,
                compressionWatch.ElapsedMilliseconds,
                result.SummaryMessageId);

            if (result.CompactedMessageCount > 0)
            {
                _logger.LogInformation(
                    "[ContextWindow:AutoCompact] quality session={Session} summaryPreview={Preview} hasAgentWorkSummary={HasSummary}",
                    sessionId,
                    TruncateForLog(result.SummaryPreview, ContextWindowConstants.CompactionSummaryLogPreviewLength),
                    !string.IsNullOrWhiteSpace(agentWorkSummary));
            }

            await RecordAutoCompactionMetricAsync(
                sessionId,
                workspaceId,
                agentId,
                TelemetryMetricStatuses.Succeeded,
                "context.auto_compaction",
                countValue: result.CompactedMessageCount,
                numericValue: compressionRatio,
                durationMs: compressionWatch.ElapsedMilliseconds,
                dimensions: BuildCompletionDimensions(health, result, compactMs, maxTokenBudget, agentWorkSummary),
                ct: ct);

            await EmitCompactionLifecycleEventAsync(
                sessionId,
                workspaceId,
                SseEventTypes.ContextCompactionCompleted,
                new
                {
                    compactionId,
                    sessionId,
                    mode = "Auto",
                                        level = "Full",
                    reason = ContextWindowConstants.AutoCompactionReason,
                    compaction = result,
                    diagnostics = result.Diagnostics,
                    compactedCount = result.CompactedMessageCount,
                    beforeTokens = result.BeforeTokens,
                    afterTokens = result.AfterTokens,
                    compressionRatio,
                    compactMs,
                    totalMs = compressionWatch.ElapsedMilliseconds,
                    summaryId = result.SummaryMessageId,
                    hasAgentWorkSummary = !string.IsNullOrWhiteSpace(agentWorkSummary),
                },
                traceId,
                ct);

                        // 清理重试状态
            _strategy.ClearRetryState(sessionId);

            return result.CompactedMessageCount > 0;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "[ContextWindow] Auto compact failed; fallback to in-memory trim. session={Session} agent={AgentId}",
                sessionId,
                agentId);

            await RecordAutoCompactionMetricAsync(
                sessionId,
                workspaceId,
                agentId,
                TelemetryMetricStatuses.Failed,
                "context.auto_compaction",
                error: ex,
                ct: CancellationToken.None);

            await EmitCompactionLifecycleEventAsync(
                sessionId,
                workspaceId,
                SseEventTypes.ContextCompactionFailed,
                new
                {
                    sessionId,
                    mode = "Auto",
                                        level = "Full",
                    reason = ContextWindowConstants.AutoCompactionReason,
                    error = ex.Message,
                    errorType = ex.GetType().Name,
                },
                traceId,
                CancellationToken.None);
            return false;
        }
    }

    private async Task EmitCompactionLifecycleEventAsync(
        string sessionId,
        string workspaceId,
        string eventType,
        object payload,
        string? traceId,
        CancellationToken ct)
    {
        if (_compactionEventEmitter is null)
        {
            _logger.LogWarning(
                "[ContextWindow:AutoCompact] compaction lifecycle emitter unavailable session={Session} event={EventType}",
                sessionId,
                eventType);

            await RecordAutoCompactionMetricAsync(
                sessionId,
                workspaceId,
                agentId: null,
                status: TelemetryMetricStatuses.Failed,
                name: "context.auto_compaction.event_missing",
                dimensions: new Dictionary<string, string> { ["event_type"] = eventType },
                ct: CancellationToken.None);
            return;
        }

        try
        {
            await _compactionEventEmitter.EmitAsync(sessionId, workspaceId, eventType, payload, traceId, ct);
            _logger.LogInformation(
                "[ContextWindow:AutoCompact] emitted lifecycle event session={Session} event={EventType}",
                sessionId,
                eventType);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "[ContextWindow:AutoCompact] failed to emit lifecycle event session={Session} event={EventType}",
                sessionId,
                eventType);

            await RecordAutoCompactionMetricAsync(
                sessionId,
                workspaceId,
                agentId: null,
                status: TelemetryMetricStatuses.Failed,
                name: "context.auto_compaction.event_emit",
                error: ex,
                dimensions: new Dictionary<string, string> { ["event_type"] = eventType },
                ct: CancellationToken.None);
        }
    }

    private async Task RecordAutoCompactionMetricAsync(
        string sessionId,
        string? workspaceId,
        string? agentId,
        string status,
        string name,
        long? countValue = null,
        double? numericValue = null,
        long? durationMs = null,
        IReadOnlyDictionary<string, string>? dimensions = null,
        Exception? error = null,
        CancellationToken ct = default)
    {
        if (_telemetrySink is null)
            return;

        try
        {
            var trace = RuntimeTraceContext
                .CreateNew(sessionId: sessionId, workspaceId: workspaceId)
                .WithAgent(agentId);
            await _telemetrySink.RecordAsync(new TelemetryMetric
            {
                Trace = trace,
                Source = "pudding.runtime.context_window_manager",
                Category = TelemetryMetricCategories.Context,
                Name = name,
                Status = status,
                DurationMs = durationMs,
                CountValue = countValue,
                NumericValue = numericValue,
                Unit = name.EndsWith(".health", StringComparison.Ordinal) ? "usage_ratio" : null,
                Severity = status == TelemetryMetricStatuses.Failed ? "warning" : "info",
                Summary = BuildAutoCompactionMetricSummary(name, status),
                Dimensions = dimensions,
                ErrorCode = error?.GetType().Name,
                ErrorMessage = error?.Message,
            }, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "[ContextWindow:AutoCompact] failed to record telemetry metric session={Session} name={MetricName}",
                sessionId,
                name);
        }
    }

    private Dictionary<string, string> BuildHealthDimensions(ContextHealthSnapshot health, int maxTokenBudget) =>
        new()
        {
            ["state"] = health.State.ToString(),
            ["used_tokens"] = health.UsedTokens.ToString(CultureInfo.InvariantCulture),
            ["context_window_tokens"] = health.ContextWindowTokens.ToString(CultureInfo.InvariantCulture),
            ["effective_window_tokens"] = health.EffectiveWindowTokens.ToString(CultureInfo.InvariantCulture),
            ["remaining_tokens"] = health.RemainingTokens.ToString(CultureInfo.InvariantCulture),
            ["usage_ratio"] = health.UsageRatio.ToString("F4", CultureInfo.InvariantCulture),
            ["usage_source"] = health.UsageSource,
            ["usage_confidence"] = health.UsageConfidence,
            ["should_auto_compact"] = health.ShouldAutoCompact ? "true" : "false",
            ["should_block_send"] = health.ShouldBlockSend ? "true" : "false",
            ["should_suggest_compact"] = health.ShouldSuggestCompact ? "true" : "false",
            ["token_budget"] = maxTokenBudget.ToString(CultureInfo.InvariantCulture),
            ["tool_count"] = _defaultToolCount.ToString(CultureInfo.InvariantCulture),
            ["provider_prompt_tokens"] = health.ProviderPromptTokens?.ToString(CultureInfo.InvariantCulture) ?? "",
            ["provider_total_tokens"] = health.ProviderTotalTokens?.ToString(CultureInfo.InvariantCulture) ?? "",
        };

    private Dictionary<string, string> BuildCompletionDimensions(
        ContextHealthSnapshot health,
        ContextCompactionResult result,
        long compactMs,
        int maxTokenBudget,
        string? agentWorkSummary)
    {
        var dimensions = BuildHealthDimensions(health, maxTokenBudget);
        dimensions["before_tokens"] = result.BeforeTokens.ToString(CultureInfo.InvariantCulture);
        dimensions["after_tokens"] = result.AfterTokens.ToString(CultureInfo.InvariantCulture);
        dimensions["compacted_message_count"] = result.CompactedMessageCount.ToString(CultureInfo.InvariantCulture);
        dimensions["summary_id"] = result.SummaryMessageId;
        dimensions["compact_ms"] = compactMs.ToString(CultureInfo.InvariantCulture);
        dimensions["has_agent_work_summary"] = string.IsNullOrWhiteSpace(agentWorkSummary) ? "false" : "true";
        return dimensions;
    }

    private static string BuildAutoCompactionMetricSummary(string name, string status) =>
        $"{name} {status}";

        public void TrimHistory(List<ChatMessage> history, int maxTokenBudget, string? query = null)
    {
        // Proportional to token budget (~2500 tokens/msg), floor at 40.
        // A 1M window → 400 messages; a 128k window → 51 messages.
        int maxMessages = Math.Max(ContextWindowConstants.MinMaxMessagesFloor, Math.Max(1, maxTokenBudget) / ContextWindowConstants.TokenToMessageRatio);
        if (history.Count <= maxMessages + 1) return;

        var system = history.FirstOrDefault(m => m.Role == ChatRole.System);
        var nonSystem = history.Where(m => m.Role != ChatRole.System).ToList();

        if (nonSystem.Count > maxMessages)
        {
            // Tier 化裁剪：从最冷 tier 的最旧消息开始裁，直到条数 ≤ maxMessages；
            // 替代旧的扁平 TakeLast（无差别保最近 N 条）。
            var inputs = MapToTierInputs(nonSystem, query);
            var assignments = _tierPlanner.Plan(inputs, options: null).Assignments;
            var removeCount = nonSystem.Count - maxMessages;

            var removeIndexes = assignments
                .Select((a, i) => (Index: i, a.Tier))
                .OrderByDescending(x => (int)x.Tier)   // 最冷 tier 先裁
                .ThenBy(x => x.Index)                  // 同 tier 内最旧先裁
                .Take(removeCount)
                .Select(x => x.Index)
                .ToHashSet();

            var kept = new List<ChatMessage>(maxMessages);
            for (var i = 0; i < nonSystem.Count; i++)
            {
                if (!removeIndexes.Contains(i))
                    kept.Add(nonSystem[i]);
            }

            nonSystem = kept;
        }

        history.Clear();
        if (system is not null) history.Add(system);
        history.AddRange(SanitizeForLlmContext(nonSystem));
    }

    /// <summary>
    /// 把有序 <see cref="MessageEntity"/> 列表映射为 TierPlanner 输入（v1）。
    /// 规则：Role == "user" 作为轮次边界（0-based 递增），最后 user 轮视为当前轮（T0）；
    /// query 命中（正文包含 query 的有效 token）→ IsQueryHit=true；AtomicGroupId 恒 null（原子组留 Task E）。
    /// </summary>
    private static List<TierPlannerSegmentInput> MapToTierInputs(
        IReadOnlyList<MessageEntity> entities,
        string? query = null)
        => BuildTierInputs(
            entities,
            segmentIdSelector: (e, _) => e.MessageId,
            isUserBoundary: e => string.Equals(e.Role, "user", StringComparison.OrdinalIgnoreCase),
            contentSelector: e => e.Content ?? string.Empty,
            query);

    /// <summary>
    /// TrimHistory 专用：ChatMessage 无 MessageId，用稳定索引字符串作为 SegmentId。
    /// </summary>
    private static List<TierPlannerSegmentInput> MapToTierInputs(
        IReadOnlyList<ChatMessage> messages,
        string? query = null)
        => BuildTierInputs(
            messages,
            segmentIdSelector: (_, i) => i.ToString(CultureInfo.InvariantCulture),
            isUserBoundary: m => m.Role == ChatRole.User,
            contentSelector: m => m.Content ?? string.Empty,
            query);

    /// <summary>
    /// 通用轮次推导：按序遇到 user 边界开启新轮（0-based），消息归属当前轮；
    /// 最后 user 轮标记为当前轮（IsCurrentTurn → T0），保证最近一轮全保真；
    /// query 非空时正文包含命中串的消息标记 IsQueryHit（由 ContextTierPlanner 有界晋升）。
    /// 输出与输入同序。
    /// </summary>
    private static List<TierPlannerSegmentInput> BuildTierInputs<T>(
        IReadOnlyList<T> items,
        Func<T, int, string> segmentIdSelector,
        Func<T, bool> isUserBoundary,
        Func<T, string> contentSelector,
        string? query = null)
    {
        var inputs = new List<TierPlannerSegmentInput>(items.Count);
        var turnOrdinal = -1;       // 遇到第一条 user 后从 0 开始
        var lastUserTurn = -1;      // 最后一条 user 开启的轮号（无 user 时为 -1）
        var queryHits = ExtractQueryHits(query);

        for (var i = 0; i < items.Count; i++)
        {
            var isUser = isUserBoundary(items[i]);
            if (isUser)
            {
                turnOrdinal++;
                lastUserTurn = turnOrdinal;
            }

            var content = contentSelector(items[i]) ?? string.Empty;
            var isQueryHit = queryHits.Count > 0
                && content.Length > 0
                && queryHits.Any(hit => content.Contains(hit, StringComparison.OrdinalIgnoreCase));

            inputs.Add(new TierPlannerSegmentInput(
                segmentIdSelector(items[i], i),
                turnOrdinal,
                IsCurrentTurn: false,
                IsQueryHit: isQueryHit,
                AtomicGroupId: null));
        }

        if (lastUserTurn >= 0)
        {
            for (var i = 0; i < inputs.Count; i++)
            {
                if (inputs[i].TurnOrdinal == lastUserTurn)
                    inputs[i] = inputs[i] with { IsCurrentTurn = true };
            }
        }

        return inputs;
    }

    /// <summary>
    /// 从当前用户 query 提取「命中串」列表（纯内存、确定性、无 I/O）：
    /// 按空白/常见标点切分 → 小写 → 过滤长度 &lt; 2 或纯数字/纯符号的 token → 去重。
    /// query 为 null/空白 → 空列表；无有效 token → 以 query 整体小写作为单一命中串。
    /// </summary>
    internal static IReadOnlyList<string> ExtractQueryHits(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<string>();

        const string Separators = " \t\r\n,，。;；:：.！!？?()（）[]【】{}<>《》\"'\"'-_/\\|@#$%^&*+=~`";

        var tokens = query
            .Split(Separators.ToCharArray(), StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.ToLowerInvariant())
            .Where(t => t.Length >= 2 && !IsNumericOrSymbolOnly(t))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (tokens.Count == 0)
        {
            var fallback = query.Trim().ToLowerInvariant();
            return string.IsNullOrWhiteSpace(fallback) ? Array.Empty<string>() : [fallback];
        }

        return tokens;
    }

    /// <summary>token 是否不含任何字母/表意字符（纯数字或纯符号）→ 无检索语义，应过滤。</summary>
    private static bool IsNumericOrSymbolOnly(string token)
    {
        foreach (var c in token)
        {
            if (char.IsLetter(c))
                return false;
        }

        return true;
    }

    private static List<ChatMessage> SanitizeForLlmContext(IEnumerable<ChatMessage> source)
    {
        var messages = source.Where(message => !IsRuntimeControlMessage(message));
        return LlmMessageSequenceNormalizer.Normalize(messages).Messages.ToList();
    }

    private static bool IsRuntimeControlMessage(ChatMessage message)
    {
        if (message.Role != ChatRole.Assistant)
            return false;

        var text = message.Content?.Trim();
        if (string.IsNullOrWhiteSpace(text))
            return false;

        if (text.StartsWith("Session fuse triggered.", StringComparison.Ordinal))
            return true;

        if (text.Contains("State: Faulted", StringComparison.Ordinal)
            && text.Contains("Recovery: Send /resume", StringComparison.Ordinal))
            return true;

        if (text.StartsWith("Session '", StringComparison.Ordinal)
            && (text.Contains("Fuse is no longer active", StringComparison.Ordinal)
                || text.Contains("has been reset from Faulted to Running", StringComparison.Ordinal)))
            return true;

        return text.StartsWith("Runtime mode is now ", StringComparison.Ordinal);
    }

    /// <summary>
    /// 从历史消息中提取 Agent 的工作总结。
    /// 检查最后几条 Assistant 消息，判断是否包含工作纪要特征。
    /// </summary>
    private string? ExtractAgentWorkSummaryFromHistory(string sessionId)
    {
        if (_compactionNotifier is null)
            return null;

        if (!_histories.TryGetValue(sessionId, out var history))
            return null;

        // 从后往前检查最近的 5 条 Assistant 消息
        var recentAssistantMessages = history
            .Where(m => m.Role == ChatRole.Assistant)
            .TakeLast(ContextWindowConstants.WorkSummarySearchWindow)
            .Reverse()
            .ToList();

        foreach (var message in recentAssistantMessages)
        {
            if (string.IsNullOrWhiteSpace(message.Content))
                continue;

            if (_compactionNotifier.IsAgentWorkSummaryResponse(message.Content))
            {
                return message.Content;
            }
        }

        return null;
    }

    /// <summary>
    /// 向当前会话历史注入系统指令，让 Agent 生成工作总结。
    /// </summary>
    private void InjectAgentWorkSummaryPrompt(string sessionId)
    {
        if (_compactionNotifier is null)
            return;

        var history = GetOrCreateHistory(sessionId);
        var prompt = _compactionNotifier.GetWorkSummaryPrompt();

        // 检查是否已经有这个提示词，避免重复注入
        var alreadyInjected = history.Any(m =>
            m.Role == ChatRole.System &&
            m.Content != null &&
            m.Content.Contains(ContextWindowConstants.WorkSummaryPromptMarker));

        if (alreadyInjected)
            return;

        history.Add(new ChatMessage(ChatRole.System, prompt));

                _logger.LogInformation(
            "[ContextWindow] Injected agent work summary prompt session={Session}",
            sessionId);
    }

    /// <summary>
    /// 压缩前冲洗：用 Flash LLM 从当前会话中提取关键事实。
    /// 借鉴 Claude Code 的 pre-compaction flush 模式。
    /// 冲洗结果写入会话历史，供后续潜意识 LLM 转化为正式记忆。
    /// 失败不抛异常，只记录日志。
    /// </summary>
    private async Task<IReadOnlyList<string>> FlushMemoriesBeforeCompactionAsync(
        string sessionId,
        string workspaceId,
        string? agentId,
        string? agentTemplateId,
        string? agentWorkSummary,
        string compactionId,
        CancellationToken ct)
    {
        if (_preCompactionFlushService is null)
            return [];

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            _logger.LogInformation(
                "[PreCompactFlush] Started session={Session} agent={AgentId}",
                sessionId, agentId);

            // 构建消息列表
            var messages = new List<ContextCompactionMessage>();
            if (_histories.TryGetValue(sessionId, out var history))
            {
                long seq = 0;
                foreach (var msg in history)
                {
                    if (!string.IsNullOrWhiteSpace(msg.Content)
                        && (msg.Role == ChatRole.User || msg.Role == ChatRole.Assistant))
                    {
                        messages.Add(new ContextCompactionMessage(
                            MessageId: $"flush-{seq}",
                            Sequence: seq++,
                            Role: msg.Role.ToString().ToLowerInvariant(),
                            Content: msg.Content));
                    }
                }
            }

            var request = new PreCompactionFlushRequest(
                workspaceId,
                sessionId,
                agentId,
                                messages,
                ContextWindowConstants.AutoCompactionReason)
            {
                AgentTemplateId = agentTemplateId,
                AgentWorkSummary = agentWorkSummary,
            };

            var result = await _preCompactionFlushService.FlushAsync(request, ct);

            sw.Stop();

            if (result.Success)
            {
                _logger.LogInformation(
                    "[PreCompactFlush] Succeeded session={Session} facts={Count} duration={DurationMs}ms",
                    sessionId, result.FactsExtracted, sw.ElapsedMilliseconds);

                // 将冲洗结果保存为系统消息，标记来源
                if (!string.IsNullOrWhiteSpace(result.FlushContent))
                {
                    var flushMessage = new ChatMessage(
                        ChatRole.System,
                        $"[PreCompactFlush compaction={compactionId}]\n{result.FlushContent}");
                    history?.Add(flushMessage);
                }

                return result.Facts ?? [];
            }
            else
            {
                _logger.LogInformation(
                    "[PreCompactFlush] No facts extracted session={Session} duration={DurationMs}ms",
                    sessionId, sw.ElapsedMilliseconds);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogWarning(
                ex,
                "[PreCompactFlush] Failed (non-blocking) session={Session}: {Message}",
                sessionId, ex.Message);
        }

        return [];
    }

    private static ChatRole ParseChatRole(string role)
    {
        var normalized = role.Trim().ToLowerInvariant();
        return normalized switch
        {
            "assistant" or "agent" => ChatRole.Assistant,
            "system" => ChatRole.System,
            "tool" => ChatRole.Tool,
            _ => ChatRole.User,
        };
    }

        private static TimeSpan NormalizeSessionTimeout(TimeSpan timeout) =>
        timeout > TimeSpan.Zero ? timeout : ContextWindowConstants.DefaultSessionTimeout;

    private static string TruncateForLog(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var normalized = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength] + "…";
    }
}
