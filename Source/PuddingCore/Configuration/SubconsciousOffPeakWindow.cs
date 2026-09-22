using System.Globalization;

namespace PuddingCode.Configuration;

/// <summary>
/// 潜意识非工作时段窗口（RSI-G8-D2）：**本地墙钟**时间区间，支持跨午夜。
/// <para>
/// <b>为什么必须是类型而不是两个 <c>TimeOnly</c> 参数</b>：跨午夜是常规情形（22:00 → 08:00），
/// 而「起点 &gt; 终点」在两处各写一次就一定会有一处写反 —— 写反的表现是窗口静默为空（作业永不加速），
/// 而外壳看起来完全正常。把判定收进一个不可变对象后，跨午夜只有一个实现、一个用例面。
/// </para>
/// <para>
/// ⭐ <b>边界语义（半开区间 <c>[Start, End)</c>）</b>：起点<b>含</b>、终点<b>不含</b>。
/// 这样相邻窗口（如 22:00→08:00 与 08:00→22:00）恰好<b>划分</b>一天而<b>不重叠、不留缝</b>；
/// 若两端都含，08:00 会同时属于两个窗口 —— 而"重叠"在按窗口决定节奏的场景里无法被解释。
/// </para>
/// <para>
/// ⛔ <b>刻意不含星期/工作日/节假日维度</b>：潜意识域当前<b>不存在</b>任何工作日概念
/// （<c>SubconsciousWorkerService</c> 只从 <c>TimeProvider</c> 取 UTC ticks）。
/// 把「工作日」作为维度加进来等于在本片顺手发明一条治理概念，且没有需求依据
/// ⇒ 需要时另行立卡，不在本切片偷偷扩边界。
/// </para>
/// <para>
/// ⛔ <b>本类型不消费 <see cref="TimeProvider"/></b>：本地时间的获取在
/// <see cref="SubconsciousOffPeakEvaluator"/> 的入口处一次性完成（P3），
/// 判定本身只吃「已经是本地墙钟」的值 —— 这样才能在不依赖宿主机时区的前提下逐字段取红。
/// </para>
/// </summary>
public sealed record SubconsciousOffPeakWindow
{
    private SubconsciousOffPeakWindow(TimeOnly start, TimeOnly end)
    {
        Start = start;
        End = end;
    }

    /// <summary>窗口起点（含）。</summary>
    public TimeOnly Start { get; }

    /// <summary>窗口终点（不含）。</summary>
    public TimeOnly End { get; }

    /// <summary>
    /// 构造窗口。<b>空窗口（起点 == 终点）一律拒绝</b>。
    /// <para>
    /// 为什么必须拒绝而不是"当作 24 小时"或"当作 0 小时"：两种解读都存在，
    /// 且都与调用方的真实意图相反；一旦静默选一种，"非工作时段"就会在无人察觉的情况下
    /// 变成"全天加速"或"永不加速"。与 <c>SubconsciousRhythmPolicy.IsValid</c> 的窗口非空校验同源。
    /// </para>
    /// </summary>
    public static SubconsciousOffPeakWindow Create(TimeOnly start, TimeOnly end)
    {
        if (start == end)
        {
            throw new ArgumentException(
                $"非工作时段窗口不得为空：起点与终点相同（{start.ToString("HH:mm", CultureInfo.InvariantCulture)}）。"
                + "空窗口应表达为「不启用非工作时段优先」，而不是一个两端相同的区间。",
                nameof(end));
        }

        return new SubconsciousOffPeakWindow(start, end);
    }

    /// <summary>是否跨午夜（起点晚于终点，如 22:00 → 08:00）。</summary>
    public bool IsCrossMidnight => Start > End;

    /// <summary>给定的本地墙钟时刻是否落在窗口内（半开区间 <c>[Start, End)</c>）。</summary>
    public bool Contains(TimeOnly timeOfDay)
        => IsCrossMidnight
            // 跨午夜：分两段判定（[Start, 24:00) ∪ [00:00, End)）。
            ? timeOfDay >= Start || timeOfDay < End
            : timeOfDay >= Start && timeOfDay < End;

    /// <summary>
    /// 同上，但入参是带偏移的时间点 —— <b>取其自身墙钟</b>（<c>DateTimeOffset.TimeOfDay</c>），
    /// ⛔ <b>不做任何向 UTC 或宿主时区的换算</b>。
    /// <para>
    /// ⚠️ 因此调用方<b>必须</b>传本地时间（<c>TimeProvider.GetLocalNow()</c>）；
    /// 传 UTC 值不会抛错、只会静默判定成"另一个时段"。优先使用
    /// <see cref="SubconsciousOffPeakEvaluator.IsOffPeak(TimeProvider, SubconsciousOffPeakWindow)"/>
    /// —— 那里把"取本地时间"固定成唯一入口。
    /// </para>
    /// </summary>
    public bool Contains(DateTimeOffset localNow) => Contains(TimeOnly.FromTimeSpan(localNow.TimeOfDay));

    /// <summary>与 <see cref="Contains(TimeOnly)"/> 互补的窗口（交换两端点），用于不重叠不漏缝的分割断言。</summary>
    public SubconsciousOffPeakWindow Complement() => Create(End, Start);
}

/// <summary>
/// 非工作时段判定的唯一入口（RSI-G8-D2，纯函数、无 IO、无写盘）。
/// <para>
/// 本类型的全部职责是回答一个问题：<b>给定的（本地）时刻是否落在给定的窗口内</b>。
/// 「在窗口内」之后是否加速、加速到多少，由 <see cref="SubconsciousRhythmPolicy.Resolve"/> 决定 ——
/// 判定与策略分离，本片才能在不改动调度器的前提下把语义逐条取红（零行为接线）。
/// </para>
/// <para>
/// ⛔ <b>本片不接线</b>：不消费 <c>SubconsciousSchedulingOptions</c>、不触碰
/// <c>SubconsciousWorkerService</c>、不写盘。接线（含"窗口内加速"真正生效）属 D3/D4。
/// </para>
/// </summary>
public static class SubconsciousOffPeakEvaluator
{
    /// <summary>
    /// 判定的<b>推荐入口</b>：由 <see cref="TimeProvider"/> 取<b>本地</b>墙钟（P3）。
    /// <para>
    /// ⛔ 这里必须是 <c>GetLocalNow()</c>，不得用 <c>GetUtcNow()</c> 再自行加减偏移：
    /// 由 UTC ticks 反推本地小时会把「非工作时段」变成与宿主时区/夏令时绑定的隐式事实，
    /// 且偏移写死在代码里时会在跨时区或 DST 切换时静默错判（尤其偏移为 0 的时区，
    /// 错误表现为"看起来完全正常"）。
    /// </para>
    /// </summary>
    public static bool IsOffPeak(TimeProvider timeProvider, SubconsciousOffPeakWindow window)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(window);
        return window.Contains(timeProvider.GetLocalNow());
    }

    /// <summary>判定入口（入参必须是<b>已换算好的本地</b>时间；语义见 <see cref="SubconsciousOffPeakWindow.Contains(DateTimeOffset)"/>）。</summary>
    public static bool IsOffPeak(DateTimeOffset localNow, SubconsciousOffPeakWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return window.Contains(localNow);
    }

    /// <summary>
    /// 策略 → 窗口映射的<b>唯一实现</b>（避免 D4 接线时再写一份映射而口径分叉）。
    /// <list type="bullet">
    /// <item>未启用非工作时段优先 ⇒ <c>null</c>，<b>即使窗口已配置</b>也不得消费（P4：关闭即零行为）；</item>
    /// <item>启用且窗口齐备 ⇒ 对应窗口；</item>
    /// <item>启用但窗口不齐备 ⇒ <b>抛异常</b>（fail-closed）。该形状正常路径不可达
    /// （<c>SubconsciousRhythmPolicy.Create</c> 已拒绝），只有绕过校验直接构造策略对象才会出现；
    /// 此时静默返回 <c>null</c> 会让"已启用但永不生效"看起来像"非工作时段没到"。</item>
    /// </list>
    /// </summary>
    public static SubconsciousOffPeakWindow? From(SubconsciousRhythmPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        if (!policy.OffPeakPriorityEnabled)
        {
            return null;
        }

        if (policy.OffPeakWindowStart is not { } start || policy.OffPeakWindowEnd is not { } end)
        {
            throw new InvalidOperationException(
                "策略已启用非工作时段优先，但窗口不齐备：该策略未经校验式构造（SubconsciousRhythmPolicy.Create / EnsureValid）。"
                + "静默降级为「不受策略影响」会把配置错误伪装成「非工作时段未到」。");
        }

        return SubconsciousOffPeakWindow.Create(start, end);
    }
}
