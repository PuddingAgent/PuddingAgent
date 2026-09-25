namespace PuddingFullTextIndex.Contracts;

/// <summary>
/// 局部维护的**生命周期接缝**（方案 §3.2 的 <c>IFullTextIndexMaintenance</c>）：宿主只调用
/// <see cref="StartAsync"/> / <see cref="StopAsync"/>，**不**拥有 watcher、队列、扫描循环或租约。
/// <para>
/// ⚠️ 与 <see cref="IFullTextSearchEngine"/> / <see cref="IFullTextIndexRootedEngine"/> **完全独立**（理由见
/// <see cref="IFullTextIndexMaintenanceEngine"/> 的注释）：不给既有接口加成员，CLI 无须修改。
/// </para>
/// <para>
/// ⚠️ 默认关闭 ⇒ 零副作用（方案 §1.3 硬不变量 3）：未显式 <see cref="StartAsync"/>（或配置开关为
/// false）时，不得解析 / 探测 scope、不得访问索引根、不得创建 checkpoint 目录、不得创建 watcher、
/// 不得启动体检线程、不得获取租约。
/// </para>
/// <para>
/// ⚠️ 本切片（S1）只定义契约，不提供实现（实现属 S3/S5）。
/// </para>
/// </summary>
public interface IFullTextIndexMaintenance
{
    /// <summary>启动维护（幂等：已在运行则请求被合并，不重复创建 watcher / 线程）。</summary>
    /// <param name="scopes">本次生效的 scope 清单（已由调用方 fail-closed 校验）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task StartAsync(
        IReadOnlyList<FullTextMaintenanceScope> scopes,
        CancellationToken cancellationToken = default);

    /// <summary>停止维护并释放 watcher / 线程 / 租约（幂等；不得吞掉取消）。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 请求一次**补偿扫描**（异步受理，不等待完成）。触发源见 <see cref="FullTextRecoveryReason"/>；
    /// 同一 scope 已有在跑的补偿时请求被合并，不叠加。
    /// </summary>
    /// <param name="scopeRoot">scope 语料根。</param>
    /// <param name="reason">请求原因（诊断用；不同原因的重试时机不同）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    ValueTask RequestRecoveryScanAsync(
        string scopeRoot,
        FullTextRecoveryReason reason,
        CancellationToken cancellationToken = default);

    /// <summary>同步取维护状态快照（不可变；实现每次返回新实例，不得返回内部可变状态）。</summary>
    FullTextMaintenanceSnapshot GetSnapshot();
}

/// <summary>
/// 一个受维护的 scope（<see cref="IFullTextIndexMaintenance.StartAsync"/> 的入参元素）。
/// <para>
/// 调用方负责把配置里的原始 scope 路径解析成 <see cref="RootPath"/> / <see cref="ScopeKey"/>
/// （规范化与逐条拒绝属配置解析层），维护器不再重新解释原始路径。
/// </para>
/// </summary>
/// <param name="RootPath">scope 语料根（绝对路径，保留调用方给的大小写）。</param>
/// <param name="ScopeKey">scope 规范键（分隔符统一 <c>\</c> + 不变文化小写）。</param>
/// <param name="MaxIndexBytes">该 scope 生效的集合总预算（字节，硬限）。</param>
/// <param name="IndexDirectory">
/// 由 <c>FullTextIndexPaths.ResolveIndexDirectory</c> 推导出的 live 索引目录（诊断用；可能尚不存在）。
/// 为 null 表示调用方未推导（维护器必须自行经同一真源推导，不得复刻命名哈希）。
/// </param>
public sealed record FullTextMaintenanceScope(
    string RootPath,
    string ScopeKey,
    long MaxIndexBytes,
    string? IndexDirectory = null);

/// <summary>
/// 补偿扫描的触发原因（方案 §3.5）：维护启动、周期、watcher overflow / Error、队列压力、
/// checkpoint 读失败、手动重建完成或索引 generation 变化。
/// </summary>
public enum FullTextRecoveryReason
{
    /// <summary>维护启动后的首次补偿。</summary>
    Startup = 0,

    /// <summary>周期补偿（<c>RecoveryScanInterval</c>）。</summary>
    Interval = 1,

    /// <summary>watcher 内部缓冲区溢出（可能丢事件 ⇒ 必须补偿）。</summary>
    WatcherOverflow = 2,

    /// <summary>watcher 报错（可能已静默停止 ⇒ 必须补偿）。</summary>
    WatcherError = 3,

    /// <summary>队列路径预算坍缩（有界队列溢出）。</summary>
    QueuePressure = 4,

    /// <summary>checkpoint 读失败 / 版本不支持 / 与 scope 不匹配 ⇒ 按陈旧处理。</summary>
    CheckpointUnreadable = 5,

    /// <summary>手动重建完成（局部索引可能整批落后于源文件）。</summary>
    ManualRebuildCompleted = 6,

    /// <summary>检测到索引 generation 变化（索引在维护器背后被替换过）。</summary>
    IndexGenerationChanged = 7,

    /// <summary>显式人工请求。</summary>
    ManualRequest = 8,
}

/// <summary>单个 scope 的维护状态快照（可观测信号，方案 §3.3）。</summary>
/// <param name="ScopeKey">scope 规范键。</param>
/// <param name="RootPath">scope 语料根。</param>
/// <param name="IndexDirectory">live 索引目录（由单一真源推导）。</param>
/// <param name="IndexExists">live 索引目录当前是否存在（不存在 ⇒ 该 scope 处于「需手动重建」，不得局部初始化）。</param>
/// <param name="WatermarkUtc">最新 checkpoint 的 watermark（= 最近一次成功补偿扫描的**开始**时刻）；无 checkpoint 时为 null。</param>
/// <param name="Generation">checkpoint generation（每次成功推进递增；时钟回拨校准后也递增）。</param>
/// <param name="LastCompletedBatchId">最近一次成功推进的批次标识（诊断 / 幂等审计用）。</param>
/// <param name="PendingChangeCount">当前尚未 flush 的折叠后路径数。</param>
/// <param name="OverflowCount">累计队列 / watcher 溢出次数（大于 0 ⇒ 已请求补偿）。</param>
/// <param name="LastAppliedUtc">最近一次成功局部提交的时刻（UTC）。</param>
/// <param name="LastProbeState">最近一次体检结论；未做过体检时为 null。</param>
/// <param name="LastMessage">最近一次可读说明（失败原因 / 退避原因 / Busy 原因）。</param>
public sealed record FullTextMaintenanceScopeSnapshot(
    string ScopeKey,
    string RootPath,
    string? IndexDirectory,
    bool IndexExists,
    DateTimeOffset? WatermarkUtc,
    long Generation,
    string? LastCompletedBatchId,
    int PendingChangeCount,
    long OverflowCount,
    DateTimeOffset? LastAppliedUtc,
    FullTextIndexIntegrityState? LastProbeState,
    string? LastMessage);

/// <summary>
/// 维护器的整体状态快照（<see cref="IFullTextIndexMaintenance.GetSnapshot"/> 的返回值）。
/// </summary>
/// <param name="Running">维护是否在运行。</param>
/// <param name="Scopes">逐 scope 状态。</param>
/// <param name="StartedUtc">最近一次启动时刻（UTC）。</param>
/// <param name="StoppedUtc">最近一次停止时刻（UTC）。</param>
/// <param name="AppliedBatchCount">累计成功局部提交批次数。</param>
/// <param name="FailedBatchCount">累计失败批次数（≤ 已提交批次数；失败不得伪装成功）。</param>
/// <param name="LastError">最近一次错误说明；无错误时为 null。</param>
public sealed record FullTextMaintenanceSnapshot(
    bool Running,
    IReadOnlyList<FullTextMaintenanceScopeSnapshot> Scopes,
    DateTimeOffset? StartedUtc,
    DateTimeOffset? StoppedUtc,
    long AppliedBatchCount,
    long FailedBatchCount,
    string? LastError);
