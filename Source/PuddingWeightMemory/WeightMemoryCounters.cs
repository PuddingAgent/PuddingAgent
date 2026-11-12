namespace PuddingWeightMemory;

/// <summary>
/// 组件的运行计数：幂等命中、被跳过的训练、以及**降级**发生了几次。
/// <para>
/// 所有权：这是"影子期到底漏了多少"这一事实的唯一来源。降级不抛异常、不改变返回值形状，
/// 如果不计数，队列满就会变成一次无声的观测缺失。计数只在本进程内累积，进程重启归零；
/// 需要长期趋势时由宿主定期读取并落盘。
/// </para>
/// </summary>
public sealed class WeightMemoryCounters
{
    private long _registrationsApplied;
    private long _registrationsDuplicate;
    private long _registrationsDropped;
    private long _feedbacksApplied;
    private long _feedbacksDuplicate;
    private long _feedbacksSkippedByMargin;
    private long _feedbacksSkippedDeleted;
    private long _feedbacksDropped;
    private long _invalidationsApplied;
    private long _invalidationsDropped;
    private long _predictions;
    private long _predictionsDegraded;
    private long _predictionsUnknownSnapshot;
    private long _publishSucceeded;
    private long _publishRejected;

    /// <summary>成功写入的章节登记数。</summary>
    public long RegistrationsApplied => Volatile.Read(ref _registrationsApplied);

    /// <summary>因事件标识重复而未再次写入的登记数。</summary>
    public long RegistrationsDuplicate => Volatile.Read(ref _registrationsDuplicate);

    /// <summary>因写入队列已满而没有被登记的章节数（漏登记，表现为召不回）。</summary>
    public long RegistrationsDropped => Volatile.Read(ref _registrationsDropped);

    /// <summary>真正改变了候选权重的反馈数。</summary>
    public long FeedbacksApplied => Volatile.Read(ref _feedbacksApplied);

    /// <summary>事件标识或消息-章节对重复而未重复训练的反馈数。</summary>
    public long FeedbacksDuplicate => Volatile.Read(ref _feedbacksDuplicate);

    /// <summary>正确章已领先到门槛以上、按设计跳过梯度的反馈数。</summary>
    public long FeedbacksSkippedByMargin => Volatile.Read(ref _feedbacksSkippedByMargin);

    /// <summary>目标章节已失效、因此没有训练任何权重的反馈数。</summary>
    public long FeedbacksSkippedDeleted => Volatile.Read(ref _feedbacksSkippedDeleted);

    /// <summary>因写入队列已满而漏训的反馈数。</summary>
    public long FeedbacksDropped => Volatile.Read(ref _feedbacksDropped);

    /// <summary>成功标记的章节失效数。</summary>
    public long InvalidationsApplied => Volatile.Read(ref _invalidationsApplied);

    /// <summary>因写入队列已满而漏做的失效数。</summary>
    public long InvalidationsDropped => Volatile.Read(ref _invalidationsDropped);

    /// <summary>完成的预测次数（含返回空结果的次数）。</summary>
    public long Predictions => Volatile.Read(ref _predictions);

    /// <summary>因写入队列饱和而降级为空结果的预测次数。</summary>
    public long PredictionsDegraded => Volatile.Read(ref _predictionsDegraded);

    /// <summary>请求钉住的快照不存在的预测次数（按空结果处理，不回落到活动快照）。</summary>
    public long PredictionsUnknownSnapshot => Volatile.Read(ref _predictionsUnknownSnapshot);

    /// <summary>成功的快照发布（含回滚）次数。</summary>
    public long PublishSucceeded => Volatile.Read(ref _publishSucceeded);

    /// <summary>比较交换未命中的发布次数。</summary>
    public long PublishRejected => Volatile.Read(ref _publishRejected);

    internal void OnRegistrationApplied() => Interlocked.Increment(ref _registrationsApplied);

    internal void OnRegistrationDuplicate() => Interlocked.Increment(ref _registrationsDuplicate);

    internal void OnRegistrationDropped() => Interlocked.Increment(ref _registrationsDropped);

    internal void OnFeedbackApplied() => Interlocked.Increment(ref _feedbacksApplied);

    internal void OnFeedbackDuplicate() => Interlocked.Increment(ref _feedbacksDuplicate);

    internal void OnFeedbackSkippedByMargin() => Interlocked.Increment(ref _feedbacksSkippedByMargin);

    internal void OnFeedbackSkippedDeleted() => Interlocked.Increment(ref _feedbacksSkippedDeleted);

    internal void OnFeedbackDropped() => Interlocked.Increment(ref _feedbacksDropped);

    internal void OnInvalidationApplied() => Interlocked.Increment(ref _invalidationsApplied);

    internal void OnInvalidationDropped() => Interlocked.Increment(ref _invalidationsDropped);

    internal void OnPrediction() => Interlocked.Increment(ref _predictions);

    internal void OnPredictionDegraded() => Interlocked.Increment(ref _predictionsDegraded);

    internal void OnPredictionUnknownSnapshot() => Interlocked.Increment(ref _predictionsUnknownSnapshot);

    internal void OnPublishSucceeded() => Interlocked.Increment(ref _publishSucceeded);

    internal void OnPublishRejected() => Interlocked.Increment(ref _publishRejected);
}
