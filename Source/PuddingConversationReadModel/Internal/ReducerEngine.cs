namespace PuddingConversationReadModel;

/// <summary>
/// <see cref="ConversationReadModelReducer"/> 的内部实现（纯函数：无 I/O、无时钟、无环境访问）。
/// <para>批次分类语义（与冻结合同 XML 文档一致）：</para>
/// <list type="bullet">
/// <item><b>Duplicate</b>：sequence 不大于已投影位置 C 的事件（重放/重叠批次），不重复累计；</item>
/// <item><b>OutOfOrder</b>：输入顺序倒置（sequence 不大于本批此前出现的最大 sequence），以及跳号
/// （不能从 C+1 连续消费）；跳号之后的事件不应用，投影位置停在缺口前，保证 C 不假装覆盖未投影事件；</item>
/// <item><b>ForeignConversation</b>：会话 id 不匹配，直接忽略且不推进游标；</item>
/// <item><b>LateAfterTerminal</b>：目标 Turn 已终态后到达的事件，消费游标但摘要不变；</item>
/// <item><b>Ignored</b>：Kind=Other 或缺少最小身份字段（TurnId/RunId）的事件，消费游标但摘要不变。</item>
/// </list>
/// <para>只有真正改变摘要的事件（Applied）才会递增 <see cref="ConversationReadModelState.Revision"/>；
/// 仅推进检查点（迟到/忽略）不改变摘要修订号。</para>
/// </summary>
internal static class ReducerEngine
{
    internal static ConversationReadModelState CreateEmpty(
        string conversationId,
        ReadModelInitializationState initialization)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);

        return new ConversationReadModelState
        {
            ConversationId = conversationId,
            Initialization = initialization,
            ReadModelVersion = ConversationReadModelLimits.CurrentReadModelVersion,
            Turns = new Dictionary<string, TurnReadSummary>(StringComparer.Ordinal),
            SubAgentRuns = Array.Empty<SubAgentRunSummary>(),
        };
    }

    internal static ReduceBatchResult Apply(
        ConversationReadModelState state,
        IReadOnlyList<ConversationEventInput> batch,
        ConversationReadModelLimits limits)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(limits);

        var foreignConversationCount = 0;
        var duplicateEventCount = 0;
        var outOfOrderEventCount = 0;

        // 先做与顺序无关的过滤：跨会话直接忽略；已投影事件按重放处理；同时记录输入顺序倒置。
        var candidates = new List<ConversationEventInput>(batch.Count);
        var maxSequenceSeenInInputOrder = long.MinValue;
        foreach (var ev in batch)
        {
            if (!string.Equals(ev.ConversationId, state.ConversationId, StringComparison.Ordinal))
            {
                foreignConversationCount++;
                continue;
            }

            if (ev.Sequence <= state.ProjectedThroughSequence)
            {
                duplicateEventCount++;
                continue;
            }

            if (ev.Sequence <= maxSequenceSeenInInputOrder)
                outOfOrderEventCount++;
            else
                maxSequenceSeenInInputOrder = ev.Sequence;

            candidates.Add(ev);
        }

        if (candidates.Count == 0)
        {
            return new ReduceBatchResult
            {
                State = state,
                DuplicateEventCount = duplicateEventCount,
                OutOfOrderEventCount = outOfOrderEventCount,
                ForeignConversationCount = foreignConversationCount,
            };
        }

        // 归约必须按 canonical sequence 顺序应用，不依赖调用方批次内的数组顺序。
        candidates.Sort(static (left, right) => left.Sequence.CompareTo(right.Sequence));

        var projection = new ProjectionBuilder(state, limits);
        var expectedSequence = state.ProjectedThroughSequence + 1;

        foreach (var ev in candidates)
        {
            if (ev.Sequence != expectedSequence)
            {
                // 跳号（或批内重复 sequence）：不能应用，否则 ProjectedThrough 会跳过未投影事件。
                outOfOrderEventCount++;
                continue;
            }

            switch (projection.Apply(ev))
            {
                case EventDisposition.Applied:
                    projection.AppliedCount++;
                    expectedSequence++;
                    break;
                case EventDisposition.Late:
                    projection.LateCount++;
                    expectedSequence++;
                    break;
                default:
                    projection.IgnoredCount++;
                    expectedSequence++;
                    break;
            }
        }

        return new ReduceBatchResult
        {
            State = projection.Build(),
            AppliedEventCount = projection.AppliedCount,
            DuplicateEventCount = duplicateEventCount,
            OutOfOrderEventCount = outOfOrderEventCount,
            ForeignConversationCount = foreignConversationCount,
            LateAfterTerminalCount = projection.LateCount,
            IgnoredEventCount = projection.IgnoredCount,
        };
    }

    private enum EventDisposition
    {
        Applied,
        Late,
        Ignored,
    }

    /// <summary>批次内的可变投影累加器；构建完成后冻结为新的不可变状态。</summary>
    private sealed class ProjectionBuilder
    {
        private readonly ConversationReadModelLimits _limits;
        private readonly ConversationReadModelState _original;
        private readonly int _nextRevision;
        private readonly Dictionary<string, TurnReadSummary> _turns;
        private readonly List<SubAgentRunSummary> _subAgentRuns;
        private int _subAgentRunsOmitted;

        private string? _currentTurnId;
        private string? _currentRootRunId;
        private TurnTerminalState _currentRootTerminalState;
        private CompactionReadState? _compaction;
        private long _projectedThroughSequence;
        private string? _projectedThroughEventId;

        internal ProjectionBuilder(ConversationReadModelState state, ConversationReadModelLimits limits)
        {
            _original = state;
            _limits = limits;
            _nextRevision = state.Revision + 1;
            _turns = new Dictionary<string, TurnReadSummary>(state.Turns, StringComparer.Ordinal);
            _subAgentRuns = new List<SubAgentRunSummary>(state.SubAgentRuns);
            _subAgentRunsOmitted = state.SubAgentRunsOmitted;
            _currentTurnId = state.CurrentTurnId;
            _currentRootRunId = state.CurrentRootRunId;
            _currentRootTerminalState = state.CurrentRootTerminalState;
            _compaction = state.Compaction;
            _projectedThroughSequence = state.ProjectedThroughSequence;
            _projectedThroughEventId = state.ProjectedThroughEventId;
        }

        internal int AppliedCount { get; set; }

        internal int LateCount { get; set; }

        internal int IgnoredCount { get; set; }

        internal EventDisposition Apply(ConversationEventInput ev)
        {
            return ev.Kind switch
            {
                ConversationEventKind.Other => ConsumeIgnored(ev),
                ConversationEventKind.TurnStarted => ApplyTurnStarted(ev),
                ConversationEventKind.TurnCompleted
                    or ConversationEventKind.TurnFailed
                    or ConversationEventKind.TurnCancelled => ApplyTurnTerminal(ev),
                ConversationEventKind.MessageContentAppended
                    or ConversationEventKind.MessageThinkingSummaryAppended
                    or ConversationEventKind.MessageCompleted
                    or ConversationEventKind.MessageFailed
                    or ConversationEventKind.ToolCallRequested
                    or ConversationEventKind.ToolCallCompleted
                    or ConversationEventKind.ToolCallFailed => ApplyTurnScopedEvent(ev),
                ConversationEventKind.SubAgentRunCreated
                    or ConversationEventKind.SubAgentRunStarted
                    or ConversationEventKind.SubAgentRunCompleted
                    or ConversationEventKind.SubAgentRunFailed
                    or ConversationEventKind.SubAgentRunCancelled
                    or ConversationEventKind.SubAgentRunTimedOut
                    or ConversationEventKind.SubAgentRunInterrupted
                    or ConversationEventKind.SubAgentRunBudgetExhausted => ApplySubAgentEvent(ev),
                ConversationEventKind.ContextCompactionStarted
                    or ConversationEventKind.ContextCompactionCompleted
                    or ConversationEventKind.ContextCompactionFailed => ApplyCompactionEvent(ev),
                _ => ConsumeIgnored(ev),
            };
        }

        internal ConversationReadModelState Build()
        {
            return _original with
            {
                ProjectedThroughSequence = _projectedThroughSequence,
                ProjectedThroughEventId = _projectedThroughEventId,
                CurrentTurnId = _currentTurnId,
                CurrentRootRunId = _currentRootRunId,
                CurrentRootTerminalState = _currentRootTerminalState,
                Compaction = _compaction,
                Turns = _turns,
                SubAgentRuns = _subAgentRuns.Count == 0 ? Array.Empty<SubAgentRunSummary>() : _subAgentRuns,
                SubAgentRunsOmitted = _subAgentRunsOmitted,
                Revision = AppliedCount > 0 ? _nextRevision : _original.Revision,
            };
        }

        // ------------------------------------------------------------ 事件处理

        private EventDisposition ApplyTurnStarted(ConversationEventInput ev)
        {
            // 子代理 turn 事件（ParentRunId 非空）不得抢占根 Turn / 根 run。
            if (!string.IsNullOrEmpty(ev.ParentRunId))
                return ConsumeIgnored(ev);

            var turnId = ResolveTurnId(ev);
            if (turnId is null)
                return ConsumeIgnored(ev);

            if (IsTerminalTurn(turnId))
                return ConsumeLate(ev);

            var existing = TryGetTurn(turnId);
            var turn = existing ?? new TurnReadSummary { TurnId = turnId };

            ProcessSummary process;
            if (existing is null)
            {
                // TurnStarted 头可用 ⇒ 过程计数覆盖可以完整（终态时置 Complete）。
                process = CreateProcessSummary(hasTurnHeader: true);
            }
            else
            {
                process = existing.Process;
                // 先见到内容再见到 TurnStarted：头部已补全，不能再宣称 turn_header_missing。
                if (string.Equals(process.IncompleteReason, ProcessCompletenessReasons.TurnHeaderMissing, StringComparison.Ordinal))
                {
                    process = process with
                    {
                        Completeness = SummaryCompleteness.Partial,
                        IncompleteReason = ResolveHeaderPresentReason(turn),
                        Revision = _nextRevision,
                    };
                }
            }

            _turns[turnId] = turn with
            {
                RootRunId = ev.RunId ?? turn.RootRunId,
                UserMessageId = ev.MessageId ?? turn.UserMessageId,
                FirstSequence = turn.FirstSequence == 0 ? ev.Sequence : Math.Min(turn.FirstSequence, ev.Sequence),
                LastSequence = ev.Sequence,
                Process = process,
                Revision = _nextRevision,
            };

            _currentTurnId = turnId;
            _currentRootRunId = ev.RunId;
            _currentRootTerminalState = TurnTerminalState.Unknown;

            return ConsumeApplied(ev);
        }

        private EventDisposition ApplyTurnTerminal(ConversationEventInput ev)
        {
            var turnId = ResolveTurnId(ev);
            if (turnId is null)
                return ConsumeIgnored(ev);

            if (IsTerminalTurn(turnId))
                return ConsumeLate(ev);

            var existing = TryGetTurn(turnId);
            var turn = existing ?? new TurnReadSummary
            {
                TurnId = turnId,
                // 终态首见、没有 TurnStarted 头 ⇒ 过程计数覆盖不可证。
                Process = CreateProcessSummary(hasTurnHeader: false),
            };

            var terminalState = ev.Kind switch
            {
                ConversationEventKind.TurnCompleted => TurnTerminalState.Completed,
                ConversationEventKind.TurnFailed => TurnTerminalState.Failed,
                _ => TurnTerminalState.Cancelled,
            };

            // 失败/取消终态不得伪造助手正文；只有 MessageCompleted 才能把 HasAssistantBody 置 true。
            var hasAssistantBody = terminalState == TurnTerminalState.Completed && turn.HasAssistantBody;

            _turns[turnId] = turn with
            {
                TerminalState = terminalState,
                HasAssistantBody = hasAssistantBody,
                FirstSequence = turn.FirstSequence == 0 ? ev.Sequence : turn.FirstSequence,
                LastSequence = ev.Sequence,
                Process = ResolveTerminalProcess(turn),
                Revision = _nextRevision,
            };

            if (_currentTurnId is null)
                _currentTurnId = turnId;
            if (string.Equals(_currentTurnId, turnId, StringComparison.Ordinal))
                _currentRootTerminalState = terminalState;

            return ConsumeApplied(ev);
        }

        private EventDisposition ApplyTurnScopedEvent(ConversationEventInput ev)
        {
            var turnId = ResolveTurnId(ev);
            if (turnId is null)
                return ConsumeIgnored(ev);

            if (IsTerminalTurn(turnId))
                return ConsumeLate(ev);

            // 子 run 的内容/思考/工具事件归子代理明细，不并入父 Turn 摘要（§4.1.2）：
            // 只消费游标并计入 IgnoredEventCount，不追加预览、不计数、不伪造助手正文。
            if (!string.IsNullOrEmpty(ev.ParentRunId))
                return ConsumeIgnored(ev);

            var existing = TryGetTurn(turnId) ?? new TurnReadSummary
            {
                TurnId = turnId,
                // 没看到 TurnStarted 头（中段窗口/旧库）⇒ 过程计数覆盖不可证。
                Process = CreateProcessSummary(hasTurnHeader: false),
            };
            if (existing.FirstSequence == 0)
                existing = existing with { FirstSequence = ev.Sequence };
            if (_currentTurnId is null)
                _currentTurnId = turnId;

            var process = existing.Process;
            var preview = existing.ActivePreview;
            var previewTruncated = existing.ActivePreviewTruncated;
            var hasAssistantBody = existing.HasAssistantBody;
            var assistantMessageId = existing.AssistantMessageId;

            switch (ev.Kind)
            {
                case ConversationEventKind.MessageContentAppended:
                    process = process with { ContentAppendCount = SaturatingIncrement(process.ContentAppendCount) };
                    if (!string.IsNullOrEmpty(ev.TextDelta))
                        (preview, previewTruncated) = AppendPreview(preview, previewTruncated, ev.TextDelta);
                    break;

                case ConversationEventKind.MessageThinkingSummaryAppended:
                    process = process with { ThinkingAppendCount = SaturatingIncrement(process.ThinkingAppendCount) };
                    break;

                case ConversationEventKind.MessageCompleted:
                    hasAssistantBody = true;
                    if (!string.IsNullOrEmpty(ev.MessageId))
                        assistantMessageId = ev.MessageId;
                    break;

                case ConversationEventKind.ToolCallRequested:
                    process = process with { ToolCallRequestedCount = SaturatingIncrement(process.ToolCallRequestedCount) };
                    break;

                case ConversationEventKind.ToolCallCompleted:
                    process = process with { ToolCallCompletedCount = SaturatingIncrement(process.ToolCallCompletedCount) };
                    break;

                case ConversationEventKind.ToolCallFailed:
                    process = process with { ToolCallFailedCount = SaturatingIncrement(process.ToolCallFailedCount) };
                    break;
            }

            process = process with { Revision = _nextRevision };

            _turns[turnId] = existing with
            {
                Process = process,
                ActivePreview = preview,
                ActivePreviewTruncated = previewTruncated,
                HasAssistantBody = hasAssistantBody,
                AssistantMessageId = assistantMessageId,
                LastSequence = ev.Sequence,
                Revision = _nextRevision,
            };

            return ConsumeApplied(ev);
        }

        private EventDisposition ApplySubAgentEvent(ConversationEventInput ev)
        {
            if (string.IsNullOrWhiteSpace(ev.RunId))
                return ConsumeIgnored(ev);

            var turnId = ResolveTurnId(ev);
            if (turnId is not null && IsTerminalTurn(turnId))
                return ConsumeLate(ev);

            var lifecycleState = MapSubAgentLifecycle(ev.Kind);
            var index = _subAgentRuns.FindIndex(run => string.Equals(run.RunId, ev.RunId, StringComparison.Ordinal));

            if (index >= 0)
            {
                var existing = _subAgentRuns[index];

                // 终态不可回退；不同终态冲突保留先到者（fail-closed）。迟到/冲突事件只消费游标并计入 Ignored。
                if (IsTerminalLifecycleState(existing.State))
                    return ConsumeIgnored(ev);

                _subAgentRuns[index] = existing with
                {
                    SubAgentId = existing.SubAgentId ?? ev.SubAgentId,
                    ParentRunId = existing.ParentRunId ?? ev.ParentRunId,
                    TurnId = existing.TurnId ?? turnId,
                    State = lifecycleState,
                    LastSequence = ev.Sequence,
                };

                TouchParentTurn(turnId, ev.Sequence);
                return ConsumeApplied(ev);
            }

            // 新 run 进入有界摘要前先保证容量：先淘汰最旧终态 run；全为活动 run 时无法淘汰，只能遗漏。
            var capacity = Math.Max(1, _limits.SubAgentSummaryLimit);
            while (_subAgentRuns.Count >= capacity)
            {
                var evictIndex = FindOldestTerminalRunIndex();
                if (evictIndex < 0)
                {
                    // 全部为活动 run：新 run 被拒收，缺失归因到该事件所属 Turn。
                    _subAgentRunsOmitted++;
                    MarkSubAgentSummaryTruncated(turnId);
                    return ConsumeApplied(ev);
                }

                // 被淘汰的终态 run 所属 Turn 的子代理摘要不再完整 ⇒ 缺失归因到该 run 的 Turn。
                var evictedTurnId = _subAgentRuns[evictIndex].TurnId;
                _subAgentRuns.RemoveAt(evictIndex);
                _subAgentRunsOmitted++;
                MarkSubAgentSummaryTruncated(evictedTurnId);
            }

            _subAgentRuns.Add(new SubAgentRunSummary
            {
                RunId = ev.RunId,
                SubAgentId = ev.SubAgentId,
                ParentRunId = ev.ParentRunId,
                TurnId = turnId,
                State = lifecycleState,
                FirstSequence = ev.Sequence,
                LastSequence = ev.Sequence,
            });

            TouchParentTurnAfterRunAdmission(turnId, ev.Sequence);
            return ConsumeApplied(ev);
        }

        private void TouchParentTurn(string? turnId, long sequence)
        {
            if (turnId is null || !_turns.TryGetValue(turnId, out var turn))
                return;

            _turns[turnId] = turn with
            {
                LastSequence = sequence,
                Revision = _nextRevision,
            };
        }

        private void TouchParentTurnAfterRunAdmission(string? turnId, long sequence)
        {
            if (turnId is null || !_turns.TryGetValue(turnId, out var turn))
                return;

            var process = turn.Process with
            {
                SubAgentRunCount = SaturatingIncrement(turn.Process.SubAgentRunCount),
                Revision = _nextRevision,
            };

            _turns[turnId] = turn with
            {
                Process = process,
                LastSequence = sequence,
                Revision = _nextRevision,
            };
        }

        private EventDisposition ApplyCompactionEvent(ConversationEventInput ev)
        {
            var existing = _compaction;
            _compaction = ev.Kind switch
            {
                ConversationEventKind.ContextCompactionStarted => new CompactionReadState
                {
                    IsRunning = true,
                    RunId = ev.RunId ?? existing?.RunId,
                    StartedSequence = ev.Sequence,
                    CompletedSequence = null,
                    LastAttemptFailed = false,
                    Revision = _nextRevision,
                },
                ConversationEventKind.ContextCompactionCompleted => new CompactionReadState
                {
                    IsRunning = false,
                    RunId = ev.RunId ?? existing?.RunId,
                    StartedSequence = existing?.StartedSequence ?? 0,
                    CompletedSequence = ev.Sequence,
                    LastAttemptFailed = false,
                    Revision = _nextRevision,
                },
                _ => new CompactionReadState
                {
                    IsRunning = false,
                    RunId = ev.RunId ?? existing?.RunId,
                    StartedSequence = existing?.StartedSequence ?? 0,
                    CompletedSequence = null,
                    LastAttemptFailed = true,
                    Revision = _nextRevision,
                },
            };

            return ConsumeApplied(ev);
        }

        // ------------------------------------------------------------ 工具方法

        private EventDisposition ConsumeApplied(ConversationEventInput ev)
        {
            Consume(ev);
            return EventDisposition.Applied;
        }

        private EventDisposition ConsumeLate(ConversationEventInput ev)
        {
            Consume(ev);
            return EventDisposition.Late;
        }

        private EventDisposition ConsumeIgnored(ConversationEventInput ev)
        {
            Consume(ev);
            return EventDisposition.Ignored;
        }

        private void Consume(ConversationEventInput ev)
        {
            _projectedThroughSequence = ev.Sequence;
            _projectedThroughEventId = ev.EventId;
        }

        private string? ResolveTurnId(ConversationEventInput ev)
        {
            var turnId = string.IsNullOrWhiteSpace(ev.TurnId) ? _currentTurnId : ev.TurnId;
            return string.IsNullOrWhiteSpace(turnId) ? null : turnId;
        }

        private TurnReadSummary? TryGetTurn(string turnId)
            => _turns.TryGetValue(turnId, out var turn) ? turn : null;

        private bool IsTerminalTurn(string turnId)
            => _turns.TryGetValue(turnId, out var turn) && turn.TerminalState != TurnTerminalState.Unknown;

        private (string Preview, bool Truncated) AppendPreview(string preview, bool alreadyTruncated, string delta)
        {
            var combined = preview + delta;
            if (TextBudget.Utf8Length(combined) <= _limits.ActivePreviewMaxBytes)
                return (combined, alreadyTruncated);

            // 活动正文只保留 UTF-8 有界尾部；截断标记一旦置位保持置位。
            return (TextBudget.TakeUtf8Tail(combined, _limits.ActivePreviewMaxBytes), true);
        }

        private ProcessSummary CreateProcessSummary(bool hasTurnHeader) => new()
        {
            Completeness = SummaryCompleteness.Partial,
            IncompleteReason = _original.Initialization != ReadModelInitializationState.Ready
                ? ProcessCompletenessReasons.BaselineIncomplete
                : hasTurnHeader
                    ? ProcessCompletenessReasons.TurnActive
                    : ProcessCompletenessReasons.TurnHeaderMissing,
            Revision = _nextRevision,
        };

        /// <summary>TurnStarted 头已到达且无更高优先级原因时，按是否存在子代理摘要截断选择原因。</summary>
        private string ResolveHeaderPresentReason(TurnReadSummary turn)
            => IsSubAgentSummaryTruncated(turn)
                ? ProcessCompletenessReasons.SubAgentSummaryTruncated
                : ProcessCompletenessReasons.TurnActive;

        /// <summary>
        /// 子代理摘要缺失判定：显式标记（omitted 已归因到该 Turn）优先；无法精确归因到 Turn 的
        /// 旧状态按「会话存在 omitted 且该 Turn 有子代理摘要行」保守降级（Lead 审查 fallback 口径）。
        /// </summary>
        private bool IsSubAgentSummaryTruncated(TurnReadSummary turn)
        {
            if (string.Equals(
                    turn.Process.IncompleteReason,
                    ProcessCompletenessReasons.SubAgentSummaryTruncated,
                    StringComparison.Ordinal))
            {
                return true;
            }

            return _subAgentRunsOmitted > 0 && turn.Process.SubAgentRunCount > 0;
        }

        /// <summary>
        /// 把子代理摘要缺失归因到具体 Turn：只降级优先级低于 <c>subagent_summary_truncated</c> 的状态，
        /// baseline / turn_header_missing 原因（更高优先级）保持不变；终态行被淘汰时同样降级为 Partial。
        /// </summary>
        private void MarkSubAgentSummaryTruncated(string? turnId)
        {
            if (turnId is null || !_turns.TryGetValue(turnId, out var turn))
                return;
            if (_original.Initialization != ReadModelInitializationState.Ready)
                return;

            var currentReason = turn.Process.IncompleteReason;
            if (string.Equals(currentReason, ProcessCompletenessReasons.SubAgentSummaryTruncated, StringComparison.Ordinal)
                || string.Equals(currentReason, ProcessCompletenessReasons.TurnHeaderMissing, StringComparison.Ordinal)
                || string.Equals(currentReason, ProcessCompletenessReasons.BaselineIncomplete, StringComparison.Ordinal))
            {
                return;
            }

            _turns[turnId] = turn with
            {
                Process = turn.Process with
                {
                    Completeness = SummaryCompleteness.Partial,
                    IncompleteReason = ProcessCompletenessReasons.SubAgentSummaryTruncated,
                    Revision = _nextRevision,
                },
                Revision = _nextRevision,
            };
        }

        /// <summary>
        /// 终态完整性（按优先级）：baseline_incomplete &gt; turn_header_missing &gt;
        /// subagent_summary_truncated &gt; Complete。子代理摘要不完整时不得宣称覆盖完整（Plan §3.2）。
        /// </summary>
        private ProcessSummary ResolveTerminalProcess(TurnReadSummary turn)
        {
            var process = turn.Process;

            if (_original.Initialization != ReadModelInitializationState.Ready)
            {
                return process with
                {
                    Completeness = SummaryCompleteness.Partial,
                    IncompleteReason = ProcessCompletenessReasons.BaselineIncomplete,
                    Revision = _nextRevision,
                };
            }

            if (string.Equals(process.IncompleteReason, ProcessCompletenessReasons.TurnHeaderMissing, StringComparison.Ordinal))
            {
                return process with
                {
                    Completeness = SummaryCompleteness.Partial,
                    IncompleteReason = ProcessCompletenessReasons.TurnHeaderMissing,
                    Revision = _nextRevision,
                };
            }

            if (IsSubAgentSummaryTruncated(turn))
            {
                return process with
                {
                    Completeness = SummaryCompleteness.Partial,
                    IncompleteReason = ProcessCompletenessReasons.SubAgentSummaryTruncated,
                    Revision = _nextRevision,
                };
            }

            return process with
            {
                Completeness = SummaryCompleteness.Complete,
                IncompleteReason = null,
                Revision = _nextRevision,
            };
        }

        private static bool IsTerminalLifecycleState(SubAgentRunLifecycleState state)
            => state is SubAgentRunLifecycleState.Completed
                or SubAgentRunLifecycleState.Failed
                or SubAgentRunLifecycleState.Cancelled
                or SubAgentRunLifecycleState.TimedOut
                or SubAgentRunLifecycleState.Interrupted
                or SubAgentRunLifecycleState.BudgetExhausted;

        /// <summary>找到 LastSequence 最小的终态 run（活动 run 永不淘汰）；返回 -1 表示全部为活动 run。</summary>
        private int FindOldestTerminalRunIndex()
        {
            var oldestIndex = -1;
            var oldestSequence = long.MaxValue;

            for (var i = 0; i < _subAgentRuns.Count; i++)
            {
                var run = _subAgentRuns[i];
                if (run.IsActive || run.LastSequence >= oldestSequence)
                    continue;

                oldestSequence = run.LastSequence;
                oldestIndex = i;
            }

            return oldestIndex;
        }

        private static int SaturatingIncrement(int value)
            => value == int.MaxValue ? int.MaxValue : value + 1;

        private static SubAgentRunLifecycleState MapSubAgentLifecycle(ConversationEventKind kind) => kind switch
        {
            ConversationEventKind.SubAgentRunCreated => SubAgentRunLifecycleState.Created,
            ConversationEventKind.SubAgentRunStarted => SubAgentRunLifecycleState.Running,
            ConversationEventKind.SubAgentRunCompleted => SubAgentRunLifecycleState.Completed,
            ConversationEventKind.SubAgentRunFailed => SubAgentRunLifecycleState.Failed,
            ConversationEventKind.SubAgentRunCancelled => SubAgentRunLifecycleState.Cancelled,
            ConversationEventKind.SubAgentRunTimedOut => SubAgentRunLifecycleState.TimedOut,
            ConversationEventKind.SubAgentRunInterrupted => SubAgentRunLifecycleState.Interrupted,
            _ => SubAgentRunLifecycleState.BudgetExhausted,
        };
    }
}

/// <summary><see cref="ProcessSummary.IncompleteReason"/> 的冻结取值（Plan §3.2）。</summary>
internal static class ProcessCompletenessReasons
{
    internal const string TurnActive = "turn_active";
    internal const string TurnHeaderMissing = "turn_header_missing";
    internal const string SubAgentSummaryTruncated = "subagent_summary_truncated";
    internal const string BaselineIncomplete = "baseline_incomplete";
}
