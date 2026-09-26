using PuddingFullTextIndex.Infrastructure.Supply;

namespace PuddingFullTextIndex.Infrastructure.Maintenance;

/// <summary>
/// 一条配置违规记录：**选项名 + 违规取值 + 可读消息**（三者齐全，缺一不可）。
/// <para>结构化错误的存在意义：调用方能逐条展示 / 上报，而不是只拿到一句「配置非法」。</para>
/// </summary>
/// <param name="Option">违规的配置项名（与 <see cref="MaintenanceOptions"/> 的属性名一致）。</param>
/// <param name="Value">违规取值（原样字符串化，便于直接定位是哪一条配置写错了）。</param>
/// <param name="Message">可读消息（含规则口径与合法范围）。</param>
public sealed record MaintenanceOptionsViolation(string Option, string Value, string Message);

/// <summary>
/// 配置校验结果：合法 ⇔ 违规列表为空。**fail-closed**：有任何违规就整体拒绝，
/// 绝不「纠正后继续」（纠正会让配置与生效行为不一致，是最难排查的一类事故）。
/// </summary>
/// <param name="IsValid">是否合法。</param>
/// <param name="Violations">违规项（<paramref name="IsValid"/> 为 true 时必为空）。</param>
public sealed record MaintenanceOptionsValidationResult(
    bool IsValid,
    IReadOnlyList<MaintenanceOptionsViolation> Violations)
{
    /// <summary>合法结果（空违规列表）。</summary>
    public static MaintenanceOptionsValidationResult Valid { get; } =
        new(true, Array.Empty<MaintenanceOptionsViolation>());
}

/// <summary>
/// 局部维护的配置与其 **fail-closed 校验**（方案 §7.1 / §7.3）。
/// <para>
/// <b>默认值 = 关闭</b>：<see cref="Enabled"/> 默认 false，且关闭状态**零副作用** ——
/// 不要求任何目录存在、不探测 scope、不推导任何路径、不创建状态目录、不启动任何线程。
/// </para>
/// <para>
/// 校验分两层（口径写死在这里，避免调用方各自理解）：
/// <list type="number">
/// <item><description><b>数值域</b>：与开关**无关**，始终校验。纯算术、零 IO、零路径推导，
/// 因此不违反「关闭即零副作用」。负值 / 超上限一律拒绝。</description></item>
/// <item><description><b>路径与 scope 域</b>：仅当 <see cref="Enabled"/> 为 true 时校验
/// （否则就会在关闭状态下解析配置里的路径）。</description></item>
/// </list>
/// </para>
/// <para>
/// ⚠️ 本层只做**纯逻辑**校验：scope 是否**实际存在 / 是不是目录**属 IO 判定，
/// 由启动路径（S3/S5 的 resolver / start 流程）fail-closed 处理，本层不触盘。
/// </para>
/// </summary>
public sealed record MaintenanceOptions
{
    /// <summary>队列容量上限（防误配成天文数字之后把内存吃光）。</summary>
    public const int MaxQueueCapacityAllowed = 1_048_576;

    /// <summary>单批路径数上限。</summary>
    public const int MaxBatchPathsAllowed = 65_536;

    /// <summary>体检单轮文件数上限。</summary>
    public const int MaxHealthCheckSliceFilesAllowed = 4_096;

    /// <summary>补偿扫描间隔下限：更短就等价于「持续全量轮询」，与「低优先级体检」的裁定冲突。</summary>
    public static readonly TimeSpan MinRecoveryScanInterval = TimeSpan.FromSeconds(30);

    /// <summary>补偿扫描间隔上限：更长的间隔意味着停机期变更迟迟不被补偿。</summary>
    public static readonly TimeSpan MaxRecoveryScanInterval = TimeSpan.FromDays(7);

    /// <summary>体检间隔下限。</summary>
    public static readonly TimeSpan MinHealthCheckInterval = TimeSpan.FromMinutes(5);

    /// <summary>体检间隔上限。</summary>
    public static readonly TimeSpan MaxHealthCheckInterval = TimeSpan.FromDays(30);

    /// <summary>
    /// mtime 重叠窗口上限：重叠窗口只是「覆盖低精度文件系统向下取整」的小量，
    /// 一旦放大到分钟级就变成「每轮重复处理一个巨大的时间窗」，与「只处理变化文件」的前提相悖。
    /// </summary>
    public static readonly TimeSpan MaxMTimeOverlap = TimeSpan.FromMinutes(1);

    /// <summary>体检切片间隔上限。</summary>
    public static readonly TimeSpan MaxHealthCheckSliceDelay = TimeSpan.FromMinutes(1);

    /// <summary>连续 <c>RetainedOld</c> 告警阈值上限（比这更大就永远看不到告警）。</summary>
    public const int MaxRetainedOldWarnThreshold = 1_000;

    /// <summary>资源压力退避时长上限。</summary>
    public static readonly TimeSpan MaxPressureBackoff = TimeSpan.FromHours(1);

    /// <summary>
    /// 跨进程租约**有界等待**上界的默认值（方案 §4.4：<c>租约等待期间的变更不能丢</c>、
    /// <c>获取租约后必须重新 stat 最终状态</c>）。
    /// <para>
    /// 取值 2 s = 与 <see cref="MaxCoalesceWait"/> 同量级：维护是背景渐进路径，租约通常由「手动重建 / 另一个
    /// 维护批次」持有；等太久只会把维护线程挂在别人身上，拿不到就下一轮重放同一变更集（见
    /// <c>LuceneFullTextIndexMaintenanceEngine.AcquireLeaseWithinBoundAsync</c>）。
    /// </para>
    /// </summary>
    public static readonly TimeSpan DefaultLeaseWaitUpperBound = TimeSpan.FromSeconds(2);

    /// <summary>
    /// 租约等待上界的允许上限（1 分钟）：再长就不再是「有界等待」而是把维护线程长挂住 ——
    /// 与之竞争的常客是分钟级的手动重建，等它全程结束毫无收益。
    /// </summary>
    public static readonly TimeSpan MaxLeaseWaitAllowed = TimeSpan.FromMinutes(1);

    /// <summary>
    /// 局部维护总开关。**默认 false**；默认关闭即零副作用（不探测 scope、不访问索引根、不创建状态目录、不起线程）。
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>受维护的语料根；可为绝对路径，也可为相对 <see cref="WorkspaceRoot"/> 的形式（后者要求 WorkspaceRoot 也是绝对路径）。</summary>
    public IReadOnlyList<string> Scopes { get; init; } = Array.Empty<string>();

    /// <summary>相对 scope 的解析基准（必须绝对）；无相对 scope 时可为 null。</summary>
    public string? WorkspaceRoot { get; init; }

    /// <summary>索引根目录（必须绝对）；checkpoint 位置只能由它 + 命名哈希真源推导。</summary>
    public string? IndexRootDirectory { get; init; }

    /// <summary>集合总预算硬限（字节）。默认 1 GiB（与既有配置一致）。</summary>
    public long MaxIndexBytes { get; init; } = 1_073_741_824L;

    /// <summary>watcher 有界队列容量（溢出必须计数并请求补偿，不得静默丢弃）。</summary>
    public int QueueCapacity { get; init; } = 4096;

    /// <summary>事件去抖窗口（方案 §4.5：500 ms）。</summary>
    public TimeSpan Debounce { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>去抖后的最大等待（方案 §4.5：2 s）—— 持续写入时也必须至少每 2 秒 flush 一次。</summary>
    public TimeSpan MaxCoalesceWait { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>单批路径数上限（对应任务书里的 MaxBatchFiles；方案 §7.1 的配置键名为 MaxBatchPaths）。</summary>
    public int MaxBatchPaths { get; init; } = 512;

    /// <summary>
    /// 补偿扫描间隔（方案 §7.1 的 <c>RecoveryScanInterval</c>；即任务书里提到的 refresh 周期）。
    /// </summary>
    public TimeSpan RecoveryScanInterval { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>mtime 重叠窗口（方案 §2.2：默认 2 s）；生效值由 <see cref="MTimeComparison"/> 的入参注入。</summary>
    public TimeSpan MTimeOverlap { get; init; } = MTimeComparison.DefaultMTimeOverlap;

    /// <summary>体检周期（方案 §7.1：1 天）。</summary>
    public TimeSpan HealthCheckInterval { get; init; } = TimeSpan.FromHours(24);

    /// <summary>体检单轮检查的文件数上限（小切片运行，让位于交互负载）。</summary>
    public int HealthCheckSliceFiles { get; init; } = 64;

    /// <summary>体检切片之间的休眠。</summary>
    public TimeSpan HealthCheckSliceDelay { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>CPU 高水位（百分比，取值区间 (0,100]）；超过则体检退避。</summary>
    public int CpuHighWatermarkPercent { get; init; } = 35;

    /// <summary>目标磁盘忙度高水位（百分比，取值区间 (0,100]）；超过则体检退避。</summary>
    public int DiskHighWatermarkPercent { get; init; } = 20;

    /// <summary>资源压力下的退避时长。</summary>
    public TimeSpan PressureBackoff { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>可选内存内容缓存上限（0 = 关闭；方案 §4.5 默认关闭）。</summary>
    public long ContentCacheMaxBytes { get; init; }

    /// <summary>
    /// 跨进程租约的**有界等待上界**（方案 §4.4）。拿不到租约时最多等这么久；超时 ⇒ 本批返回
    /// <c>FullTextMutationState.Busy</c>（未写入任何字节、未推进 checkpoint），同一变更集下一轮重放。
    /// <para>
    /// 必须落在 <c>(0, <see cref="MaxLeaseWaitAllowed"/>]</c>：<b>0 / 负值被拒绝</b> ——
    /// 「不等待、直接放弃」是*供给 / 构建*路径的语义（见 <c>IFullTextSupplyLease</c> 的接口注释），
    /// 维护路径按 §4.4 必须<b>有界等待</b>；上界不设上限则等价于无限挂起。
    /// </para>
    /// <para>本项属<b>数值域</b>校验：与开关无关、始终校验（纯算术、零 IO、零路径推导）。</para>
    /// </summary>
    public TimeSpan LeaseWaitUpperBound { get; init; } = DefaultLeaseWaitUpperBound;

    /// <summary>
    /// 同一路径**连续多少轮** <c>RetainedOld</c> 触发告警（用户裁定③ / 任务书 M9）。
    /// <para>
    /// ⚠️ 告警**不得**被实现成「自动跳过该文件」：判据是「允许成功文件被重复处理，漏掉文件不允许」。
    /// 本旋钮只控制<b>什么时候把这件事说出来</b>，不改变处理行为 —— 一个永久不可读的文件会让
    /// checkpoint 永不推进、每轮重扫（<c>CheckpointAdvancePolicy</c> 文档里登记的饥饿风险），
    /// 告警是让人介入的**唯一**出口。
    /// </para>
    /// <para>本项属<b>数值域</b>校验：与开关无关、始终校验（纯算术、零 IO）。</para>
    /// </summary>
    public int RetainedOldWarnThreshold { get; init; } = 3;

    /// <summary>
    /// fail-closed 校验（方案 §7.3）。返回结构化违规列表；**不修改**任何配置值。
    /// </summary>
    /// <param name="options">待校验配置。</param>
    public static MaintenanceOptionsValidationResult Validate(MaintenanceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var violations = new List<MaintenanceOptionsViolation>();

        // ── ① 数值域：与开关无关（纯算术，无 IO、无路径推导）──
        if (options.MaxIndexBytes <= 0)
        {
            violations.Add(new MaintenanceOptionsViolation(
                nameof(MaxIndexBytes),
                options.MaxIndexBytes.ToString(),
                "MaxIndexBytes 必须为正（预算硬限为 0 或负值时任何局部写入都必然越界）。"));
        }

        if (options.QueueCapacity <= 0 || options.QueueCapacity > MaxQueueCapacityAllowed)
        {
            violations.Add(new MaintenanceOptionsViolation(
                nameof(QueueCapacity),
                options.QueueCapacity.ToString(),
                $"QueueCapacity 必须在 1..{MaxQueueCapacityAllowed} 之间（有界队列必须真的有界）。"));
        }

        if (options.Debounce <= TimeSpan.Zero)
        {
            violations.Add(new MaintenanceOptionsViolation(
                nameof(Debounce),
                options.Debounce.ToString(),
                "Debounce 必须为正（0 去抖等于每个事件单独成批）。"));
        }

        if (options.MaxCoalesceWait < options.Debounce)
        {
            violations.Add(new MaintenanceOptionsViolation(
                nameof(MaxCoalesceWait),
                options.MaxCoalesceWait.ToString(),
                $"MaxCoalesceWait 不得小于 Debounce（{options.Debounce}）：否则最大等待永远先到期，去抖形同不存在。"));
        }

        if (options.MaxBatchPaths <= 0 || options.MaxBatchPaths > MaxBatchPathsAllowed)
        {
            violations.Add(new MaintenanceOptionsViolation(
                nameof(MaxBatchPaths),
                options.MaxBatchPaths.ToString(),
                $"MaxBatchPaths 必须在 1..{MaxBatchPathsAllowed} 之间。"));
        }

        if (options.RecoveryScanInterval < MinRecoveryScanInterval
            || options.RecoveryScanInterval > MaxRecoveryScanInterval)
        {
            violations.Add(new MaintenanceOptionsViolation(
                nameof(RecoveryScanInterval),
                options.RecoveryScanInterval.ToString(),
                $"RecoveryScanInterval 必须在 {MinRecoveryScanInterval}..{MaxRecoveryScanInterval} 之间。"));
        }

        if (options.MTimeOverlap < TimeSpan.Zero || options.MTimeOverlap > MaxMTimeOverlap)
        {
            violations.Add(new MaintenanceOptionsViolation(
                nameof(MTimeOverlap),
                options.MTimeOverlap.ToString(),
                $"MTimeOverlap 必须在 0..{MaxMTimeOverlap} 之间（负值无意义；过大则每轮重复处理巨大时间窗）。"));
        }

        if (options.HealthCheckInterval < MinHealthCheckInterval
            || options.HealthCheckInterval > MaxHealthCheckInterval)
        {
            violations.Add(new MaintenanceOptionsViolation(
                nameof(HealthCheckInterval),
                options.HealthCheckInterval.ToString(),
                $"HealthCheckInterval 必须在 {MinHealthCheckInterval}..{MaxHealthCheckInterval} 之间。"));
        }

        if (options.HealthCheckSliceFiles <= 0 || options.HealthCheckSliceFiles > MaxHealthCheckSliceFilesAllowed)
        {
            violations.Add(new MaintenanceOptionsViolation(
                nameof(HealthCheckSliceFiles),
                options.HealthCheckSliceFiles.ToString(),
                $"HealthCheckSliceFiles 必须在 1..{MaxHealthCheckSliceFilesAllowed} 之间。"));
        }

        if (options.HealthCheckSliceDelay < TimeSpan.Zero || options.HealthCheckSliceDelay > MaxHealthCheckSliceDelay)
        {
            violations.Add(new MaintenanceOptionsViolation(
                nameof(HealthCheckSliceDelay),
                options.HealthCheckSliceDelay.ToString(),
                $"HealthCheckSliceDelay 必须在 0..{MaxHealthCheckSliceDelay} 之间。"));
        }

        if (options.CpuHighWatermarkPercent <= 0 || options.CpuHighWatermarkPercent > 100)
        {
            violations.Add(new MaintenanceOptionsViolation(
                nameof(CpuHighWatermarkPercent),
                options.CpuHighWatermarkPercent.ToString(),
                "CpuHighWatermarkPercent 必须落在 (0,100]。"));
        }

        if (options.DiskHighWatermarkPercent <= 0 || options.DiskHighWatermarkPercent > 100)
        {
            violations.Add(new MaintenanceOptionsViolation(
                nameof(DiskHighWatermarkPercent),
                options.DiskHighWatermarkPercent.ToString(),
                "DiskHighWatermarkPercent 必须落在 (0,100]。"));
        }

        if (options.PressureBackoff <= TimeSpan.Zero || options.PressureBackoff > MaxPressureBackoff)
        {
            violations.Add(new MaintenanceOptionsViolation(
                nameof(PressureBackoff),
                options.PressureBackoff.ToString(),
                $"PressureBackoff 必须在 (0, {MaxPressureBackoff}] 之间。"));
        }

        if (options.ContentCacheMaxBytes < 0)
        {
            violations.Add(new MaintenanceOptionsViolation(
                nameof(ContentCacheMaxBytes),
                options.ContentCacheMaxBytes.ToString(),
                "ContentCacheMaxBytes 不得为负（0 表示关闭内容缓存）。"));
        }

        if (options.LeaseWaitUpperBound <= TimeSpan.Zero || options.LeaseWaitUpperBound > MaxLeaseWaitAllowed)
        {
            violations.Add(new MaintenanceOptionsViolation(
                nameof(LeaseWaitUpperBound),
                options.LeaseWaitUpperBound.ToString(),
                $"LeaseWaitUpperBound 必须在 (0, {MaxLeaseWaitAllowed}] 之间：维护路径必须「有界等待」"
                + "（0 = 不等待是供给/构建路径的语义，无穷大 = 无限挂起；两者都不接受）。"));
        }

        if (options.RetainedOldWarnThreshold <= 0 || options.RetainedOldWarnThreshold > MaxRetainedOldWarnThreshold)
        {
            violations.Add(new MaintenanceOptionsViolation(
                nameof(RetainedOldWarnThreshold),
                options.RetainedOldWarnThreshold.ToString(),
                $"RetainedOldWarnThreshold 必须在 1..{MaxRetainedOldWarnThreshold} 之间（0 / 负值 = 永不告警，上限 = 让告警还有意义）。"));
        }

        // ── ② 路径与 scope 域：只在开关打开时校验（关闭即零副作用：不解析、不探测、不推导）──
        if (options.Enabled)
        {
            var resolvedScopeKeys = new List<(int Index, string Key, string Resolved)>();

            if (options.Scopes is null || options.Scopes.Count == 0)
            {
                violations.Add(new MaintenanceOptionsViolation(
                    nameof(Scopes),
                    "[]",
                    "Enabled = true 时 Scopes 不得为空（局部维护必须知道维护哪些语料根）。"));
            }
            else
            {
                var workspaceRoot = options.WorkspaceRoot?.Trim();
                var workspaceRootUsable = true;

                if (!string.IsNullOrEmpty(workspaceRoot) && !Path.IsPathFullyQualified(workspaceRoot))
                {
                    violations.Add(new MaintenanceOptionsViolation(
                        nameof(WorkspaceRoot),
                        options.WorkspaceRoot ?? string.Empty,
                        "WorkspaceRoot 必须是绝对路径（相对 scope 的解析基准不能本身又是相对的）。"));
                    workspaceRootUsable = false;
                }

                var seenKeys = new Dictionary<string, int>(StringComparer.Ordinal);

                for (var i = 0; i < options.Scopes.Count; i++)
                {
                    var raw = options.Scopes[i];
                    var label = $"{nameof(Scopes)}[{i}]";

                    if (string.IsNullOrWhiteSpace(raw))
                    {
                        violations.Add(new MaintenanceOptionsViolation(
                            label,
                            raw ?? string.Empty,
                            "scope 路径为空或仅含空白字符。"));
                        continue;
                    }

                    var trimmed = raw.Trim();
                    string resolvedCandidate;

                    if (Path.IsPathFullyQualified(trimmed))
                    {
                        resolvedCandidate = trimmed;
                    }
                    else if (!workspaceRootUsable)
                    {
                        continue;                       // WorkspaceRoot 本身非法，已记录，不重复刷屏
                    }
                    else if (string.IsNullOrEmpty(workspaceRoot))
                    {
                        violations.Add(new MaintenanceOptionsViolation(
                            label,
                            raw,
                            "相对 scope 必须配合绝对 WorkspaceRoot；解析基准不明时拒绝，而不是猜一个目录。"));
                        continue;
                    }
                    else
                    {
                        resolvedCandidate = Path.Combine(workspaceRoot, trimmed);
                    }

                    string key;
                    try
                    {
                        key = FullTextChangeCoalescer.NormalizeComparisonKey(resolvedCandidate);
                    }
                    catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                    {
                        violations.Add(new MaintenanceOptionsViolation(
                            label,
                            raw,
                            $"scope 路径无法规范化（{ex.GetType().Name}）：{resolvedCandidate}"));
                        continue;
                    }

                    if (seenKeys.TryGetValue(key, out var firstIndex))
                    {
                        violations.Add(new MaintenanceOptionsViolation(
                            label,
                            raw,
                            $"scope 与 Scopes[{firstIndex}] 规范化后相同（重复）：{resolvedCandidate}"));
                        continue;
                    }

                    seenKeys[key] = i;
                    resolvedScopeKeys.Add((i, key, resolvedCandidate));
                }
            }

            if (string.IsNullOrWhiteSpace(options.IndexRootDirectory))
            {
                violations.Add(new MaintenanceOptionsViolation(
                    nameof(IndexRootDirectory),
                    options.IndexRootDirectory ?? string.Empty,
                    "Enabled = true 时必须提供 IndexRootDirectory（否则无法推导 checkpoint 位置）。"));
            }
            else if (!Path.IsPathFullyQualified(options.IndexRootDirectory.Trim()))
            {
                violations.Add(new MaintenanceOptionsViolation(
                    nameof(IndexRootDirectory),
                    options.IndexRootDirectory,
                    "IndexRootDirectory 必须是绝对路径。"));
            }
            else if (resolvedScopeKeys.Count > 0)
            {
                string indexKey;
                try
                {
                    indexKey = FullTextChangeCoalescer.NormalizeComparisonKey(options.IndexRootDirectory);
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    indexKey = string.Empty;
                    violations.Add(new MaintenanceOptionsViolation(
                        nameof(IndexRootDirectory),
                        options.IndexRootDirectory,
                        $"IndexRootDirectory 无法规范化（{ex.GetType().Name}）。"));
                }

                if (indexKey.Length > 0)
                {
                    foreach (var (index, scopeKey, resolved) in resolvedScopeKeys)
                    {
                        if (!SupplyScopeNormalizer.IsAncestorOrSame(indexKey, scopeKey)
                            && !SupplyScopeNormalizer.IsAncestorOrSame(scopeKey, indexKey))
                            continue;

                        violations.Add(new MaintenanceOptionsViolation(
                            $"{nameof(IndexRootDirectory)}/{nameof(Scopes)}[{index}]",
                            resolved,
                            "IndexRoot 与 scope 存在包含关系（任一方向都不允许）：会把索引自身当作语料扫描，"
                            + "形成自反馈并污染索引。"));
                    }
                }
            }
        }

        return violations.Count == 0
            ? MaintenanceOptionsValidationResult.Valid
            : new MaintenanceOptionsValidationResult(false, violations);
    }
}
