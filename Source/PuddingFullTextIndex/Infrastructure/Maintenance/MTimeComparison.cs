namespace PuddingFullTextIndex.Infrastructure.Maintenance;

/// <summary>
/// 时钟回拨 / 基线缺失的判定结论（方案 §2.5）。
/// <para>
/// ⚠️ 本片**只做判断**，不实现校准动作（校准属 S3）；返回值只是「要不要走全范围局部校准」。
/// </para>
/// </summary>
public enum MTimeCalibrationDecision
{
    /// <summary>正常增量：有可用 watermark 且 <c>scanStart >= watermark</c>。</summary>
    Incremental = 0,

    /// <summary>
    /// 需**全范围局部校准**：<c>scanStart &lt; watermark</c>（时钟回拨，继续用旧 watermark 会漏掉新写入），
    /// 或**没有可用 watermark**（无 checkpoint / 不可读 / 版本不支持 ⇒ 按陈旧处理，绝不按「无需维护」处理）。
    /// </summary>
    FullScopeRecalibrationRequired = 1,
}

/// <summary>
/// mtime 补偿判定的**纯函数**层（方案 §2.2 / §2.3 / §2.5）：零 IO、零线程、**不读时钟**。
/// <para>
/// 所有判定完全由入参决定（含 overlap 与扫描窗口），因此可确定性测试；
/// 重叠窗口只能由调用方从 <see cref="MaintenanceOptions.MTimeOverlap"/> 注入，本类不读配置。
/// </para>
/// <para>
/// <b>安全方向恒定</b>：宁可多处理一次，绝不漏处理。因此「mtime 读不到」「没有基线」「时钟倒退」
/// 一律判「需处理」或「需全范围校准」，没有任何一条分支会返回「无需处理」。
/// </para>
/// <para>
/// 为什么用 <c>&gt;=</c> 而不是 <c>&gt;</c>（方案 §2.2）：既有引擎已使用 <c>&gt;=</c>；
/// 文件系统时间精度可能低于应用时钟；文件 mtime 可能恰好等于扫描开始时刻；
/// 而 <c>DeleteDocuments(path) + AddDocuments(path)</c> 是幂等操作，多处理一次的代价远小于漏处理。
/// </para>
/// </summary>
public static class MTimeComparison
{
    /// <summary>默认重叠窗口 = 2 秒（方案 §2.2，用于覆盖低精度文件系统的向下取整）。</summary>
    public static readonly TimeSpan DefaultMTimeOverlap = TimeSpan.FromSeconds(2);

    /// <summary>有效水位线 = <c>watermarkUtc - mtimeOverlap</c>（方案 §2.2）。</summary>
    /// <param name="watermarkUtc">checkpoint 里的 watermark（= 最近一次成功扫描的**开始**时刻）。</param>
    /// <param name="mtimeOverlap">重叠窗口；不得为负。</param>
    public static DateTimeOffset ComputeEffectiveWatermark(DateTimeOffset watermarkUtc, TimeSpan mtimeOverlap)
    {
        RequireNonNegativeOverlap(mtimeOverlap);
        return watermarkUtc - mtimeOverlap;
    }

    /// <summary>
    /// 该文件本轮是否需要处理。判定式（方案 §2.2）：<c>fileMtimeUtc &gt;= watermarkUtc - mtimeOverlap</c>。
    /// <list type="bullet">
    /// <item><description><paramref name="fileMtimeUtc"/> 为 null（mtime 读不到）⇒ **需处理**（fail-stale；绝不允许「读不到就跳过」）。</description></item>
    /// <item><description><paramref name="watermarkUtc"/> 为 null（无 checkpoint / 不可读）⇒ **需处理**。</description></item>
    /// <item><description>否则 **含等号**：<c>mtime == watermark</c> 与 <c>mtime == watermark - overlap</c> 都判需处理，
    /// 只有严格早于有效水位线的文件才跳过。</description></item>
    /// </list>
    /// </summary>
    /// <param name="fileMtimeUtc">观察到的文件最后写入时刻（UTC）；读不到为 null。</param>
    /// <param name="watermarkUtc">已有 watermark；没有为 null。</param>
    /// <param name="mtimeOverlap">重叠窗口；不得为负。</param>
    public static bool RequiresProcessing(
        DateTimeOffset? fileMtimeUtc,
        DateTimeOffset? watermarkUtc,
        TimeSpan mtimeOverlap)
    {
        RequireNonNegativeOverlap(mtimeOverlap);

        if (fileMtimeUtc is null || watermarkUtc is null)
            return true;

        return fileMtimeUtc.Value >= watermarkUtc.Value - mtimeOverlap;
    }

    /// <summary>
    /// 时钟回拨 / 基线缺失判定（方案 §2.5）：<c>scanStart &lt; watermark</c> ⇒ 需全范围局部校准。
    /// <para>
    /// 注意这里用的是**严格小于**：<c>scanStart == watermark</c> 是合法增量基线（不是回拨）。
    /// </para>
    /// </summary>
    /// <param name="scanStartedUtc">本轮扫描开始时刻（本机时钟）。</param>
    /// <param name="watermarkUtc">已有 watermark；没有为 null ⇒ 同样需全范围校准。</param>
    public static MTimeCalibrationDecision DecideCalibration(
        DateTimeOffset scanStartedUtc,
        DateTimeOffset? watermarkUtc)
        => watermarkUtc is null || scanStartedUtc < watermarkUtc.Value
            ? MTimeCalibrationDecision.FullScopeRecalibrationRequired
            : MTimeCalibrationDecision.Incremental;

    /// <summary>
    /// 下一轮的 watermark（方案 §2.3）：**取扫描开始时刻，绝不取结束时刻**。
    /// <para>
    /// 理由（方案 §2.3）：若某文件在「已经枚举过它」之后、扫描结束之前被写入，
    /// 记录结束时刻会让该文件的新 mtime 小于 checkpoint，于是**下一轮永久跳过它**；
    /// 记录开始时刻则保证这类写入在下一轮满足 <c>mtime &gt;= scanStart</c> 而被处理。
    /// </para>
    /// <para>
    /// ⚠️ 参数刻意接收两个时刻，就是为了让「取哪一个」成为**唯一一行**的可测决策
    /// （本片变异取红 M2 即改这一行）。
    /// </para>
    /// </summary>
    /// <param name="scanStartedUtc">本轮扫描开始时刻。</param>
    /// <param name="scanFinishedUtc">本轮扫描结束时刻（<b>不</b>用作 watermark，仅作对照与诊断）。</param>
    public static DateTimeOffset ComputeNextWatermark(DateTimeOffset scanStartedUtc, DateTimeOffset scanFinishedUtc)
        => scanStartedUtc;

    /// <summary>
    /// 读取前后两次 stat 是否**逐位一致**（方案 §2.5「文件提取期间再次修改」/ §3.6 第 3 步的最终 stat）。
    /// <list type="bullet">
    /// <item><description>任一侧读不到（null）⇒ **不稳定**（fail-closed：放弃本次内容并重新入队，绝不把可能已变的内容写进索引）。</description></item>
    /// <item><description>mtime 或 length 任一不同 ⇒ 不稳定。</description></item>
    /// </list>
    /// </summary>
    /// <param name="beforeUtc">读取前的 mtime。</param>
    /// <param name="beforeLength">读取前的长度。</param>
    /// <param name="afterUtc">读取后的 mtime。</param>
    /// <param name="afterLength">读取后的长度。</param>
    public static bool IsStatStable(
        DateTimeOffset? beforeUtc,
        long? beforeLength,
        DateTimeOffset? afterUtc,
        long? afterLength)
        => beforeUtc is not null
            && afterUtc is not null
            && beforeLength is not null
            && afterLength is not null
            && beforeUtc.Value == afterUtc.Value
            && beforeLength.Value == afterLength.Value;

    private static void RequireNonNegativeOverlap(TimeSpan mtimeOverlap)
    {
        if (mtimeOverlap < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(mtimeOverlap),
                mtimeOverlap,
                "mtimeOverlap 不得为负；非法值应在配置校验层（MaintenanceOptions.Validate）被 fail-closed 拒绝。");
        }
    }
}
