namespace PuddingWeightMemory;

/// <summary>
/// <see cref="IWeightMemory"/> 的 SQLite 实现：一次预测读一个快照，一次反馈只推进候选快照。
/// <para>
/// 分工：本类负责输入校验、编码、打分阈值与降级计数；落盘与事务边界在
/// <see cref="WeightMemoryStore"/>。这样"分数怎么算"和"状态怎么存"可以各自被读到、各自被测试。
/// </para>
/// <para>
/// 降级（设计文档 §3）：写入准入闸门满时，登记与反馈不写（记漏做/漏训），预测返回空结果，
/// 三种情况都不抛异常——影子期宁可少一条观测，也不能把等待加到用户回合上。
/// </para>
/// </summary>
public sealed class SqliteWeightMemory : IWeightMemory, IWeightMemoryMaintenance, IAsyncDisposable
{
    private readonly WeightMemoryOptions _options;
    private readonly ITextEncoder _encoder;
    private readonly IWriteAdmissionGate _gate;
    private readonly WeightMemoryStore _store;

    /// <summary>
    /// 打开（必要时创建）一个权重库。
    /// </summary>
    /// <param name="options">库路径、编码器身份、阈值与训练超参。</param>
    /// <param name="encoder">文本编码器；产品里是本地模型进程的适配器，测试里是确定性替身。</param>
    /// <param name="gate">写入准入闸门；缺省按 <see cref="WeightMemoryOptions.MaxPendingWrites"/> 建一个有界队列。</param>
    public SqliteWeightMemory(WeightMemoryOptions options, ITextEncoder encoder, IWriteAdmissionGate? gate = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(encoder);

        options.Validate();
        ArgumentOutOfRangeException.ThrowIfLessThan(encoder.Dimension, 1, nameof(encoder));

        _options = options;
        _encoder = encoder;
        _gate = gate ?? new BoundedWriteAdmissionGate(options.MaxPendingWrites);
        _store = new WeightMemoryStore(options, encoder.Dimension);
    }

    /// <summary>降级与幂等计数；影子期判断"漏了多少"只能靠它。</summary>
    public WeightMemoryCounters Counters { get; } = new();

    /// <inheritdoc />
    public async Task<RecallResult> PredictAsync(RecallQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        ValidateScope(query.Scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(query.CurrentText);

        var requestedSnapshot = query.SnapshotId ?? string.Empty;
        var read = await _store.ReadSnapshotAsync(query.Scope, requestedSnapshot, ct).ConfigureAwait(false);
        Counters.OnPrediction();

        if (!read.Exists)
        {
            // 钉住的快照不存在时不回落到活动快照：回放必须知道自己读的是哪一代，假装成功会污染指标。
            if (requestedSnapshot.Length != 0)
                Counters.OnPredictionUnknownSnapshot();

            return new RecallResult(requestedSnapshot, Array.Empty<MemoryHit>());
        }

        if (read.Chapters.Count == 0 || query.Limit <= 0)
            return new RecallResult(read.SnapshotId, Array.Empty<MemoryHit>());

        if (_gate.IsSaturated)
        {
            Counters.OnPredictionDegraded();
            return new RecallResult(read.SnapshotId, Array.Empty<MemoryHit>());
        }

        var vector = VectorMath.Normalize(await EncodeAsync(query.CurrentText, ct).ConfigureAwait(false));

        var scored = new List<MemoryHit>(read.Chapters.Count);
        foreach (var chapter in read.Chapters)
            scored.Add(new MemoryHit(chapter.ChapterId, (float)VectorMath.Dot(chapter.Weight, vector)));

        scored.Sort(CompareHits);

        if (scored[0].Score < _options.MinimumTopScore)
            return new RecallResult(read.SnapshotId, Array.Empty<MemoryHit>());

        if (scored.Count > 1 && scored[0].Score - scored[1].Score < _options.MinimumScoreGap)
            return new RecallResult(read.SnapshotId, Array.Empty<MemoryHit>());

        var take = Math.Min(query.Limit, scored.Count);
        return new RecallResult(read.SnapshotId, scored.GetRange(0, take));
    }

    /// <inheritdoc />
    public async Task RegisterAsync(SavedChapter chapter, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(chapter);
        ValidateScope(chapter.Scope);
        ValidateTurn(chapter.Source);
        ArgumentException.ThrowIfNullOrWhiteSpace(chapter.EventId);
        ArgumentException.ThrowIfNullOrWhiteSpace(chapter.BookId);
        ArgumentException.ThrowIfNullOrWhiteSpace(chapter.ChapterId);
        ArgumentException.ThrowIfNullOrWhiteSpace(chapter.SourceCue);

        if (!_gate.TryEnter())
        {
            Counters.OnRegistrationDropped();
            return;
        }

        try
        {
            var anchor = VectorMath.Normalize(await EncodeAsync(chapter.SourceCue, ct).ConfigureAwait(false));
            var outcome = await _store.RegisterAsync(chapter.Scope, chapter, anchor, ct).ConfigureAwait(false);

            if (outcome == RegisterOutcome.Applied)
                Counters.OnRegistrationApplied();
            else
                Counters.OnRegistrationDuplicate();
        }
        finally
        {
            _gate.Exit();
        }
    }

    /// <inheritdoc />
    public async Task ApplyFeedbackAsync(Feedback feedback, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(feedback);
        ValidateScope(feedback.Scope);
        ValidateTurn(feedback.Source);
        ArgumentException.ThrowIfNullOrWhiteSpace(feedback.EventId);
        ArgumentException.ThrowIfNullOrWhiteSpace(feedback.PrimaryChapterId);
        ArgumentException.ThrowIfNullOrWhiteSpace(feedback.Cue);

        if (!_gate.TryEnter())
        {
            Counters.OnFeedbackDropped();
            return;
        }

        try
        {
            var cue = VectorMath.Normalize(await EncodeAsync(feedback.Cue, ct).ConfigureAwait(false));
            var outcome = await _store.ApplyFeedbackAsync(feedback.Scope, feedback, cue, ct).ConfigureAwait(false);

            switch (outcome)
            {
                case FeedbackOutcome.Applied:
                    Counters.OnFeedbackApplied();
                    break;
                case FeedbackOutcome.Duplicate:
                    Counters.OnFeedbackDuplicate();
                    break;
                case FeedbackOutcome.SkippedByMargin:
                    Counters.OnFeedbackSkippedByMargin();
                    break;
                case FeedbackOutcome.SkippedDeleted:
                    Counters.OnFeedbackSkippedDeleted();
                    break;
                default:
                    throw new WeightMemoryException($"未知的反馈结果 {outcome}。");
            }
        }
        finally
        {
            _gate.Exit();
        }
    }

    /// <inheritdoc />
    public async Task InvalidateChapterAsync(
        MemoryScope scope,
        string chapterId,
        string eventId,
        CancellationToken ct)
    {
        ValidateScope(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(chapterId);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);

        if (!_gate.TryEnter())
        {
            Counters.OnInvalidationDropped();
            return;
        }

        try
        {
            // 失效没有独立的来源轮次字段：事件标识本身就是回执，消息列留空（不作为去重主体）。
            var outcome = await _store
                .InvalidateChapterAsync(scope, chapterId, eventId, string.Empty, ct)
                .ConfigureAwait(false);

            if (outcome == InvalidateOutcome.Applied)
                Counters.OnInvalidationApplied();
        }
        finally
        {
            _gate.Exit();
        }
    }

    /// <inheritdoc />
    public async Task<bool> TryPublishAsync(
        MemoryScope scope,
        string expectedActiveSnapshotId,
        string evaluatedCandidateSnapshotId,
        CancellationToken ct)
    {
        ValidateScope(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedActiveSnapshotId);
        ArgumentException.ThrowIfNullOrWhiteSpace(evaluatedCandidateSnapshotId);

        var published = await _store
            .TryPublishAsync(scope, expectedActiveSnapshotId, evaluatedCandidateSnapshotId, ct)
            .ConfigureAwait(false);

        if (published)
            Counters.OnPublishSucceeded();
        else
            Counters.OnPublishRejected();

        return published;
    }

    /// <inheritdoc />
    public Task<WeightMemorySnapshots?> ReadSnapshotsAsync(MemoryScope scope, CancellationToken ct)
    {
        ValidateScope(scope);
        return _store.ReadSnapshotsAsync(scope, ct);
    }

    /// <summary>释放库连接池与内部信号量；之后实例不可再用。</summary>
    /// <returns>完成即代表资源已释放。</returns>
    public ValueTask DisposeAsync() => _store.DisposeAsync();

    /// <summary>分数降序；同分按章节 ID 序数序，保证同一快照的排序与检索顺序无关。</summary>
    private static int CompareHits(MemoryHit left, MemoryHit right)
    {
        var byScore = right.Score.CompareTo(left.Score);
        return byScore != 0 ? byScore : string.CompareOrdinal(left.ChapterId, right.ChapterId);
    }

    /// <summary>编码并校验维度：维度不符是配置错误，不能当作空结果吞掉。</summary>
    private async ValueTask<float[]> EncodeAsync(string text, CancellationToken ct)
    {
        var vector = await _encoder.EncodeAsync(text, ct).ConfigureAwait(false);
        if (vector is null)
            throw new WeightMemoryException("编码器返回了空向量。");

        if (vector.Length != _encoder.Dimension)
        {
            throw new WeightMemoryException(
                $"编码器声明 {_encoder.Dimension} 维，实际返回 {vector.Length} 维。");
        }

        return vector;
    }

    private static void ValidateScope(MemoryScope? scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope.WorkspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope.UserId);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope.AgentId);
    }

    private static void ValidateTurn(TurnKey? turn)
    {
        ArgumentNullException.ThrowIfNull(turn);
        ArgumentException.ThrowIfNullOrWhiteSpace(turn.ConversationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(turn.TurnId);
        ArgumentException.ThrowIfNullOrWhiteSpace(turn.MessageId);
    }
}
