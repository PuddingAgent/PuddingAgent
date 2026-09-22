using System.Reflection;
using PuddingCode.Configuration;

namespace PuddingCoreTests.Configuration;

/// <summary>
/// RSI-G8-D2 的契约层用例：非工作时段判定器（纯类型 + 纯判定，零行为接线）。
/// <para>
/// <b>纪律</b>（沿用 G4 §5.1 / G7 / G8-D1 的契约层做法）：每条不变式都要有**能变红**的用例，
/// 且负例必须断言**具体的失败面**；只断言"没崩 / 非空"不算。每条用例的注释里写明**取红配方**。
/// </para>
/// <para>
/// 本片覆盖的不变式：P3（本地时间只能来自 <c>GetLocalNow()</c>，不得由 UTC 反推）、
/// P5（fail-closed：空窗口 / 半配置窗口一律拒绝，不得静默退化成"全天加速"或"永不加速"）、
/// P4（未启用非工作时段优先 ⇒ 不消费窗口）。
/// </para>
/// </summary>
[TestClass]
public sealed class SubconsciousOffPeakWindowTests
{
    /// <summary>同日内窗口（不跨午夜）：09:00 → 17:00。</summary>
    private static SubconsciousOffPeakWindow Daytime =>
        SubconsciousOffPeakWindow.Create(new TimeOnly(9, 0), new TimeOnly(17, 0));

    /// <summary>跨午夜窗口：22:00 → 08:00。</summary>
    private static SubconsciousOffPeakWindow Overnight =>
        SubconsciousOffPeakWindow.Create(new TimeOnly(22, 0), new TimeOnly(8, 0));

    // ── P5：空窗口必须被拒绝 ────────────────────────────────────────────────────

    /// <summary>
    /// P5：起点与终点相同的窗口没有任何合法解读（0 小时？24 小时？），必须构造期拒绝，
    /// 不得静默选一种 —— 静默选择会让"非工作时段"变成"全天加速"或"永不加速"。
    /// </summary>
    /// <remarks>取红：删掉 <c>Create</c> 里的 <c>start == end</c> 判空分支 ⇒ 本用例必须红。</remarks>
    [TestMethod]
    public void Create_ShouldRejectEmptyWindow_InsteadOfGuessingItsMeaning()
    {
        var exception = Assert.ThrowsExactly<ArgumentException>(
            () => SubconsciousOffPeakWindow.Create(new TimeOnly(22, 0), new TimeOnly(22, 0)));

        Assert.IsTrue(
            exception.Message.Contains("不得为空", StringComparison.Ordinal),
            "拒绝理由必须指向「空窗口」，而不是泛泛的参数错误（否则读者会去查别处）。");
    }

    // ── 跨午夜判定（D2 的核心语义）─────────────────────────────────────────────

    /// <summary>跨午夜由「起点晚于终点」唯一确定，不得由调用方另行传 flag（两处判断必然写反一处）。</summary>
    /// <remarks>取红：把 <c>IsCrossMidnight</c> 改成 <c>Start &lt; End</c>（写反）⇒ 本用例必须红。</remarks>
    [TestMethod]
    public void IsCrossMidnight_ShouldFollowStartEndOrder()
    {
        Assert.IsTrue(Overnight.IsCrossMidnight, "22:00 → 08:00 是跨午夜窗口。");
        Assert.IsFalse(Daytime.IsCrossMidnight, "09:00 → 17:00 不是跨午夜窗口。");
    }

    /// <summary>
    /// P5/核心：跨午夜窗口必须真的绕过去 —— 午夜前后两侧都在窗口内，而窗口外的白日时段不在。
    /// </summary>
    /// <remarks>取红：把 <c>Contains</c> 的跨午夜分支删掉（只留 <c>&gt;= Start &amp;&amp; &lt; End</c>）
    /// ⇒ 23:59 / 00:00 / 07:59 三条断言必须红。</remarks>
    [TestMethod]
    public void CrossMidnightWindow_ShouldWrapAroundMidnight()
    {
        var inside = new[] { new TimeOnly(22, 0), new TimeOnly(23, 59), new TimeOnly(0, 0), new TimeOnly(7, 59) };
        foreach (var time in inside)
        {
            Assert.IsTrue(Overnight.Contains(time), $"跨午夜窗口应包含 {time:HH\\:mm}。");
        }

        var outside = new[] { new TimeOnly(8, 0), new TimeOnly(12, 0), new TimeOnly(21, 59) };
        foreach (var time in outside)
        {
            Assert.IsFalse(Overnight.Contains(time), $"跨午夜窗口不应包含 {time:HH\\:mm}。");
        }
    }

    // ── 边界语义：半开区间 [Start, End) ─────────────────────────────────────────

    /// <summary>
    /// 半开区间：起点<b>含</b>、终点<b>不含</b>。
    /// <para>
    /// 边界不是审美问题：若终点也含，则相邻窗口（22:00→08:00 与 08:00→22:00）在 08:00 重叠，
    /// 而"同一时刻同时属于两个节奏窗口"无法被解释（采用间隔会出现两解）。
    /// </para>
    /// </summary>
    /// <remarks>取红：把 <c>Contains</c> 的两个 <c>&lt; End</c> 改成 <c>&lt;= End</c>
    /// ⇒ 终点断言与下面的分割断言必须红。</remarks>
    [TestMethod]
    public void WindowBoundaries_ShouldBeStartInclusiveAndEndExclusive()
    {
        Assert.IsTrue(Daytime.Contains(new TimeOnly(9, 0)), "起点必须含（09:00 在窗口内）。");
        Assert.IsTrue(Daytime.Contains(new TimeOnly(16, 59)), "终点前一分钟在窗口内。");
        Assert.IsFalse(Daytime.Contains(new TimeOnly(17, 0)), "终点必须不含（17:00 在窗口外）。");
        Assert.IsFalse(Daytime.Contains(new TimeOnly(8, 59)), "起点前一分钟在窗口外。");

        Assert.IsTrue(Overnight.Contains(new TimeOnly(22, 0)), "跨午夜窗口起点必须含。");
        Assert.IsFalse(Overnight.Contains(new TimeOnly(8, 0)), "跨午夜窗口终点必须不含。");
    }

    /// <summary>
    /// 强断言：窗口与其互补窗口必须<b>恰好划分</b>一天 —— 每个时刻落在且仅落在一侧。
    /// <para>逐点断言（15 分钟粒度、全天 96 点）比"抽查几个时刻"更难被偶然满足：
    /// 任何边界方向的错误都会在至少一个采样点上暴露。</para>
    /// </summary>
    /// <remarks>取红：把任一处 <c>&lt; End</c> 改成 <c>&lt;= End</c>，或写反跨午夜分支 ⇒ 本用例必须红。</remarks>
    [TestMethod]
    public void WindowAndItsComplement_ShouldPartitionTheDay()
    {
        var complement = Overnight.Complement();
        Assert.IsFalse(complement.IsCrossMidnight, "交换 22:00/08:00 得到的互补窗口（08:00 → 22:00）不跨午夜。");

        for (var minutes = 0; minutes < 24 * 60; minutes += 15)
        {
            var time = new TimeOnly(minutes / 60, minutes % 60);
            Assert.AreNotEqual(
                Overnight.Contains(time),
                complement.Contains(time),
                $"{time:HH\\:mm} 必须恰好落在一侧（窗口与互补窗口不得重叠、不得留缝）。");
        }
    }

    // ── P3：本地时间只能来自 GetLocalNow() ──────────────────────────────────────

    /// <summary>
    /// P3：判定入口必须取<b>本地</b>墙钟。
    /// <para>
    /// 用例构造同一瞬间的两个墙钟：本地 23:30（+08:00）在跨午夜窗口内，UTC 15:30 在窗口外。
    /// 因此"用了 GetLocalNow 还是 GetUtcNow"是一个**可观测**差异，而不是风格偏好。
    /// </para>
    /// </summary>
    /// <remarks>取红：把 <c>IsOffPeak(TimeProvider, …)</c> 里的 <c>GetLocalNow()</c> 换成 <c>GetUtcNow()</c>
    /// ⇒ 本用例必须红（返回 false 而非 true）。</remarks>
    [TestMethod]
    public void Evaluator_ShouldUseLocalWallClockFromProvider_NotUtc()
    {
        var provider = new StubTimeProvider(DateTimeOffset.Parse("2026-09-22T23:30:00+08:00"));

        Assert.IsTrue(
            SubconsciousOffPeakEvaluator.IsOffPeak(provider, Overnight),
            "本地 23:30 落在 22:00 → 08:00 窗口内。");

        // 前置事实自证：同一瞬间的 UTC 墙钟（15:30）**不在**窗口内 ⇒ 上一条断言确实在区分两个时间源。
        Assert.IsFalse(
            SubconsciousOffPeakEvaluator.IsOffPeak(provider.GetUtcNow(), Overnight),
            "同一瞬间的 UTC 墙钟 15:30 不在窗口内（这正是本用例的判别力来源）。");
    }

    /// <summary>
    /// P3 补充：<c>DateTimeOffset</c> 重载取<b>入参自身的墙钟</b>，不做任何换算 ——
    /// 这正是"调用方必须给本地时间"这一契约的可执行表述。
    /// </summary>
    /// <remarks>取红：在 <c>Contains(DateTimeOffset)</c> 里改成先 <c>ToUniversalTime()</c> ⇒ 本用例必须红。</remarks>
    [TestMethod]
    public void OffsetOverload_ShouldTreatItsOwnWallClockAsLocal_WithoutConverting()
    {
        var local = DateTimeOffset.Parse("2026-09-22T23:30:00+08:00");
        var sameInstantAsUtc = local.ToUniversalTime();

        Assert.AreEqual(
            local.UtcDateTime,
            sameInstantAsUtc.UtcDateTime,
            "前置事实：两个入参是同一瞬间。");

        Assert.IsTrue(SubconsciousOffPeakEvaluator.IsOffPeak(local, Overnight), "本地墙钟 23:30 在窗口内。");
        Assert.IsFalse(
            SubconsciousOffPeakEvaluator.IsOffPeak(sameInstantAsUtc, Overnight),
            "同一瞬间以 UTC 墙钟表达（15:30）不在窗口内 ⇒ 该重载不替调用方做时区换算。");
    }

    // ── P4：未启用即不消费窗口 ─────────────────────────────────────────────────

    /// <summary>
    /// P4：非工作时段优先关闭时，即使窗口已配置也<b>不得</b>产出窗口
    /// （否则 D4 接线后会出现"配置了窗口就算启用"的幽灵生效）。
    /// </summary>
    /// <remarks>取红：把 <c>From</c> 里的 <c>!OffPeakPriorityEnabled</c> 早退删掉 ⇒ 本用例必须红。</remarks>
    [TestMethod]
    public void From_ShouldReturnNullWhenOffPeakDisabled_EvenIfWindowIsConfigured()
    {
        var policy = SubconsciousRhythmPolicy.Default with
        {
            OffPeakWindowStart = new TimeOnly(22, 0),
            OffPeakWindowEnd = new TimeOnly(8, 0),
            OffPeakIntervalSeconds = 3_600,
        };

        Assert.IsTrue(policy.IsValid, "前置事实：窗口齐备且间隔为正，仅未启用（未启用的策略是合法配置）。");
        Assert.IsNull(
            SubconsciousOffPeakEvaluator.From(policy),
            "未启用非工作时段优先 ⇒ 不消费窗口（不得因「窗口已配置」而悄悄启用）。");
    }

    /// <summary>启用且窗口齐备 ⇒ 映射出与配置逐字段一致的窗口。</summary>
    /// <remarks>取红：把 <c>From</c> 里交换 <c>Start</c>/<c>End</c> 的赋值 ⇒ 本用例必须红。</remarks>
    [TestMethod]
    public void From_ShouldReturnConfiguredWindowWhenEnabled()
    {
        var policy = SubconsciousRhythmPolicy.Default with
        {
            OffPeakPriorityEnabled = true,
            OffPeakWindowStart = new TimeOnly(22, 0),
            OffPeakWindowEnd = new TimeOnly(8, 0),
            OffPeakIntervalSeconds = 3_600,
        };

        Assert.IsTrue(policy.IsValid, "前置事实：启用时窗口与间隔齐备。");

        var window = SubconsciousOffPeakEvaluator.From(policy);

        Assert.IsNotNull(window, "启用且窗口齐备 ⇒ 必须产出窗口。");
        Assert.AreEqual(Overnight, window, "映射结果必须与配置的窗口逐字段一致（22:00 → 08:00）。");
    }

    /// <summary>
    /// P5：启用却没有窗口的策略（只有绕过校验式构造才可能出现）必须<b>抛异常</b>，
    /// 不得静默返回 <c>null</c> —— 静默降级会让配置错误伪装成"非工作时段还没到"，
    /// 而这是最难被发现的失效形态（看起来一切正常）。
    /// </summary>
    /// <remarks>取红：把 <c>From</c> 的 <c>throw</c> 换成 <c>return null</c> ⇒ 本用例必须红。</remarks>
    [TestMethod]
    public void From_ShouldFailClosedWhenEnabledWithoutWindow()
    {
        // 绕过校验式构造：正常路径不可达，正是为了证明该形状被 fail-closed 拦住。
        var policy = SubconsciousRhythmPolicy.Default with
        {
            OffPeakPriorityEnabled = true,
            OffPeakWindowStart = null,
            OffPeakWindowEnd = null,
            OffPeakIntervalSeconds = 3_600,
        };

        Assert.IsFalse(policy.IsValid, "前置事实：该形状本身即非法（IsValid=false）。");

        var exception = Assert.ThrowsExactly<InvalidOperationException>(
            () => SubconsciousOffPeakEvaluator.From(policy));

        Assert.IsTrue(
            exception.Message.Contains("窗口不齐备", StringComparison.Ordinal),
            "拒绝理由必须点名「窗口不齐备」，而不是让读者去猜是间隔还是开关的问题。");
    }

    // ── 入参守卫 ───────────────────────────────────────────────────────────────

    /// <summary>null 入参一律拒绝，不得静默当作"不在窗口内"（那会把接线 bug 变成静默不加速）。</summary>
    /// <remarks>取红：删掉任一处 <c>ArgumentNullException.ThrowIfNull</c> ⇒ 本用例必须红。</remarks>
    [TestMethod]
    public void Evaluator_ShouldRejectNullArguments()
    {
        var provider = new StubTimeProvider(DateTimeOffset.Parse("2026-09-22T23:30:00+08:00"));

        Assert.ThrowsExactly<ArgumentNullException>(
            () => SubconsciousOffPeakEvaluator.IsOffPeak((TimeProvider)null!, Overnight));
        Assert.ThrowsExactly<ArgumentNullException>(
            () => SubconsciousOffPeakEvaluator.IsOffPeak(provider, null!));
        Assert.ThrowsExactly<ArgumentNullException>(
            () => SubconsciousOffPeakEvaluator.From(null!));
    }

    // ── 结构闸门：本片只判定，不执行 ────────────────────────────────────────────

    /// <summary>
    /// 结构闸门：本片两个类型不得出现写盘/执行面（无 IO、无委托字段）。
    /// <para>⚠️ 诚实边界：这是<b>声明面级</b>证据，证明"公开面没有写盘句柄"，
    /// 不等于证明实现绝对无副作用；后者由"本片零行为接线"（不改任何既有文件）承担。</para>
    /// </summary>
    /// <remarks>取红：给任一类型加一个 <c>ApplyAsync</c>/<c>SaveAsync</c> 公开方法，或加一个 <c>Func&lt;&gt;</c> 字段 ⇒ 本用例必须红。</remarks>
    [TestMethod]
    public void Types_ShouldExposeNoWriteOrExecutionSurface()
    {
        var forbiddenVerbs = new[]
        {
            "Write", "Save", "Persist", "Apply", "Execute", "Run", "Enqueue", "Delete", "Commit", "Schedule",
        };

        foreach (var type in new[] { typeof(SubconsciousOffPeakWindow), typeof(SubconsciousOffPeakEvaluator) })
        {
            foreach (var method in type.GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (method.IsSpecialName)
                {
                    // 属性访问器 / 运算符：不是执行面。
                    continue;
                }

                foreach (var verb in forbiddenVerbs)
                {
                    Assert.IsFalse(
                        method.Name.Contains(verb, StringComparison.Ordinal),
                        $"{type.Name}.{method.Name} 命中写盘/执行面动词「{verb}」：本片只判定、不执行。");
                }
            }

            foreach (var field in type.GetFields(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                Assert.IsFalse(
                    typeof(Delegate).IsAssignableFrom(field.FieldType),
                    $"{type.Name}.{field.Name} 是委托字段：判定类型不得持有可执行句柄。");
            }
        }
    }

    /// <summary>
    /// 固定时间的测试替身：把同一瞬间的<b>本地墙钟</b>与 <b>UTC 墙钟</b>造成两个不同值，
    /// 使「用了哪个时间源」成为可观测差异（这正是 P3 的取红装置）。
    /// <para>
    /// ⚠️ <c>TimeProvider.GetLocalNow()</c> 在本版 .NET 中<b>不是 virtual</b>
    /// （只有 <c>GetUtcNow()</c> 与 <c>LocalTimeZone</c> 可重写）⇒ 只能重写这两个，
    /// 由基类的 <c>GetLocalNow()</c> 自行换算。这也顺带证明了真实运行时确实是
    /// 「UTC + LocalTimeZone」推导本地墙钟，而不是把 UTC 当本地用。
    /// </para>
    /// </summary>
    private sealed class StubTimeProvider : TimeProvider
    {
        /// <summary>固定 +08:00 的自定义时区：不依赖宿主时区库与夏令时规则，结果可复现。</summary>
        private static readonly TimeZoneInfo FixedZone = TimeZoneInfo.CreateCustomTimeZone(
            "test/utc+08", TimeSpan.FromHours(8), "UTC+08", "UTC+08");

        private readonly DateTimeOffset _utcNow;

        public StubTimeProvider(DateTimeOffset localNow) => _utcNow = localNow.ToUniversalTime();

        public override TimeZoneInfo LocalTimeZone => FixedZone;

        public override DateTimeOffset GetUtcNow() => _utcNow;
    }
}
