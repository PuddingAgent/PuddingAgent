namespace PuddingConversationReadModel;

/// <summary>
/// 统一预算常量（Plan §5.4、§4.3、§7.2、§8.1；§3.3 要求只有一个配置来源，不在 Controller/SQL/Hook 各写一份）。
/// <para>默认值来自方案冻结表；调用方可通过构造覆盖，但必须通过 <see cref="Validate"/>。</para>
/// <para>字节预算按 UTF-8 编码后的字节数计，不含 JSON 字段开销；JSON 序列化侧另行预留固定余量。</para>
/// </summary>
public sealed record ConversationReadModelLimits
{
    /// <summary>读模型/归约规则合同版本；持久化行与缓存 key 必须带上。</summary>
    public const int CurrentReadModelVersion = 1;

    /// <summary>历史消息稳定排序合同版本；翻页游标绑定该版本，排序变化必须失效旧游标。</summary>
    public const int HistoryOrderVersion = 1;

    /// <summary>单条消息序列化固定开销估算（id/role/timestamp/turnId 等），用于页字节预算。</summary>
    public const int MessageJsonOverheadBytes = 256;

    /// <summary>单个过程条目序列化固定开销估算。</summary>
    public const int ProcessItemJsonOverheadBytes = 128;

    /// <summary>最近消息页默认可显示消息数。</summary>
    public int OpenMessageLimit { get; init; } = 20;

    /// <summary>最近消息页服务端硬上限（Plan §5.1：默认 20、最大 50）。</summary>
    public int MaxOpenMessageLimit { get; init; } = 50;

    /// <summary>打开/历史页 JSON 硬上限（Plan §5.4：128 KiB）。</summary>
    public int OpenPageMaxBytes { get; init; } = 128 * 1024;

    /// <summary>单条首屏正文预览上限（Plan §5.4：8 KiB UTF-8）。</summary>
    public int SingleMessagePreviewBytes { get; init; } = 8 * 1024;

    /// <summary>活动正文预览上限（Plan §5.4：16 KiB UTF-8，保留尾部）。</summary>
    public int ActivePreviewMaxBytes { get; init; } = 16 * 1024;

    /// <summary>单个巨型事件/工具结果的预览上限（Plan §5.4：16 KiB）。</summary>
    public int GiantEventPreviewBytes { get; init; } = 16 * 1024;

    /// <summary>过程明细页默认条目数（Plan §5.3：默认 100）。</summary>
    public int DetailPageItemLimit { get; init; } = 100;

    /// <summary>过程明细页最大条目数（Plan §5.3：最大 200）。</summary>
    public int MaxDetailPageItems { get; init; } = 200;

    /// <summary>单页过程明细字节上限（Plan §5.3：256 KiB）。</summary>
    public int DetailPageMaxBytes { get; init; } = 256 * 1024;

    /// <summary>单页候选扫描上限；只限候选范围，避免为凑满展示项无限追历史（Plan §5.3）。</summary>
    public int DetailPageCandidateScanLimit { get; init; } = 400;

    /// <summary>首屏子代理摘要最多项数（Plan §5.4：20 项）。</summary>
    public int SubAgentSummaryLimit { get; init; } = 20;

    /// <summary>首屏子代理摘要总字节上限（Plan §5.4：32 KiB）。</summary>
    public int SubAgentSummaryMaxBytes { get; init; } = 32 * 1024;

    /// <summary>历史页候选扫描上限；只限单次请求的候选窗口。</summary>
    public int HistoryPageCandidateScanLimit { get; init; } = 200;

    /// <summary>SSE 单次恢复事件数预算（Plan §4.3：2,000 条）。</summary>
    public int SseCatchUpMaxEvents { get; init; } = 2_000;

    /// <summary>SSE 单次恢复字节预算（Plan §4.3：2 MiB）。</summary>
    public int SseCatchUpMaxBytes { get; init; } = 2 * 1024 * 1024;

    /// <summary>SSE 单次恢复耗时预算（Plan §4.3：1 秒）。</summary>
    public TimeSpan SseCatchUpMaxDuration { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>首屏缓存会话数上限（Plan §7.2：5 个）。</summary>
    public int CacheSessionLimit { get; init; } = 5;

    /// <summary>首屏缓存总字节上限（Plan §7.2：5 MiB）。</summary>
    public long CacheMaxBytes { get; init; } = 5 * 1024 * 1024;

    /// <summary>后台回填单批条数（Plan §8.1：50 条起步）。</summary>
    public int BackfillBatchSize { get; init; } = 50;

    /// <summary>后台回填单批工作预算（Plan §8.1：50ms）。</summary>
    public TimeSpan BackfillBatchWorkBudget { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <summary>后台回填批次间隔（Plan §8.1：200ms）。</summary>
    public TimeSpan BackfillBatchDelay { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Ready 后的首屏保护窗口（Plan §8.1：Ready 不表示后台可占满 CPU/SQLite）。</summary>
    public TimeSpan BackfillReadyGracePeriod { get; init; } = TimeSpan.FromSeconds(60);

    public static ConversationReadModelLimits Default { get; } = new();

    /// <summary>按请求值归一化最近消息页条数；null/越界时钳到 [1, MaxOpenMessageLimit]。</summary>
    public int NormalizeOpenMessageLimit(int? requested) =>
        LimitRules.NormalizeOpenMessageLimit(this, requested);

    /// <summary>按请求值归一化过程明细页条数；null/越界时钳到 [1, MaxDetailPageItems]。</summary>
    public int NormalizeDetailItemLimit(int? requested) =>
        LimitRules.NormalizeDetailItemLimit(this, requested);

    /// <summary>校验预算自身的一致性；返回空列表表示通过。</summary>
    public IReadOnlyList<string> Validate() =>
        LimitRules.Validate(this);
}
