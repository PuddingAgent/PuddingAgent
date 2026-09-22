using System.Reflection;
using PuddingCode.Configuration;

namespace PuddingCoreTests.Configuration;

/// <summary>
/// RSI-G8-D1 的契约层用例：潜意识节奏策略（纯类型 + 纯判定，零行为接线）。
/// <para>
/// <b>纪律</b>（沿用 <c>RSI-G4-任务书</c> §5.1 与 G7 的契约层做法）：每条不变式都要有**能变红**的用例，
/// 且负例必须断言**具体的失败面**（异常 / 理由码 / 逐字段值），只断言"没崩 / 非空"不算。
/// 每条用例的注释里写明**让它变红的变异配方**，任何人可照表复跑。
/// </para>
/// <para>
/// 本片覆盖的不变式：P1（默认零回归）、P2（无裸字面量 —— 阈值/间隔一律来自策略对象）、
/// P4（非工作时段默认关闭）、P8（理由码可解释）。P3/P5/P6 的核心载体在 D2–D5。
/// </para>
/// </summary>
[TestClass]
public sealed class SubconsciousRhythmPolicyTests
{
    private const int ConfiguredInterval = 14_400;

    // ── P1 / P4：默认即"什么都不改" ─────────────────────────────────────────────

    /// <summary>P1：默认策略在**任意**命中组合下都不得改变采用间隔（4 种组合逐字段断言）。</summary>
    /// <remarks>取红：把 <see cref="SubconsciousRhythmPolicy.Default"/> 的 <c>OffPeakPriorityEnabled</c> 改为 <c>true</c>
    /// 并给 <c>OffPeakIntervalSeconds</c> 一个值 ⇒ 本用例必须红。</remarks>
    [TestMethod]
    public void Default_ShouldNotChangeInterval_ForEveryConditionCombination()
    {
        var policy = SubconsciousRhythmPolicy.Default;

        foreach (var (isOffPeak, isBacklog) in new[] { (false, false), (true, false), (false, true), (true, true) })
        {
            var decision = policy.Resolve(ConfiguredInterval, isOffPeak, isBacklog);

            Assert.AreEqual(
                ConfiguredInterval,
                decision.IntervalSeconds,
                $"默认策略不得改变间隔（isOffPeak={isOffPeak}, isBacklog={isBacklog}）。");
            Assert.IsFalse(decision.IsShortened, "默认策略不得声称缩短。");
            Assert.AreEqual(
                SubconsciousRhythmReasonCodes.Default,
                decision.ReasonCode,
                "默认策略的理由码必须是 default。");
        }
    }

    /// <summary>P4：默认形状必须是"关闭 + 无候选值 + 无窗口"，⛔ 不得预填任何时段。</summary>
    /// <remarks>取红：给 <c>Default</c> 填一个窗口（如 22:00 → 08:00）⇒ 本用例必须红
    /// （预填时段会被读成"默认已选择该时段"这一治理结论）。</remarks>
    [TestMethod]
    public void Default_ShouldCarryNoWindowAndNoCandidateIntervals()
    {
        var policy = SubconsciousRhythmPolicy.Default;

        Assert.AreEqual(SubconsciousRhythmPolicy.ZeroRegressionPolicyId, policy.PolicyId);
        Assert.AreEqual(1, policy.Version);
        Assert.IsFalse(policy.OffPeakPriorityEnabled, "默认必须关闭非工作时段优先。");
        Assert.IsNull(policy.OffPeakWindowStart, "默认不得预填窗口起点。");
        Assert.IsNull(policy.OffPeakWindowEnd, "默认不得预填窗口终点。");
        Assert.IsNull(policy.OffPeakIntervalSeconds, "默认不得预填非工作时段间隔。");
        Assert.IsNull(policy.BacklogShortenIntervalSeconds, "默认不得预填堆积间隔。");
        Assert.IsTrue(policy.IsValid, "默认策略必须自洽（否则它会在生产首帧抛异常）。");
    }

    // ── 构造期校验（fail-closed）────────────────────────────────────────────────

    /// <summary>启用非工作时段优先却未给间隔 ⇒ 拒绝（否则特性"已开启"但什么都不做）。</summary>
    /// <remarks>取红：从 <c>HasConsistentOffPeakInterval</c> 去掉 <c>OffPeakPriorityEnabled ||</c> 段 ⇒ 本用例必须红。</remarks>
    [TestMethod]
    public void ShouldReject_OffPeakEnabledWithoutInterval()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() => SubconsciousRhythmPolicy.Create(
            policyId: "rhythm/test",
            version: 1,
            offPeakPriorityEnabled: true,
            offPeakWindowStart: new TimeOnly(22, 0),
            offPeakWindowEnd: new TimeOnly(8, 0),
            offPeakIntervalSeconds: null,
            backlogShortenIntervalSeconds: null,
            sourceLabel: "test"));
    }

    /// <summary>启用非工作时段优先却未给窗口 ⇒ 拒绝。</summary>
    /// <remarks>取红：去掉 <c>HasConsistentWindow</c> 中的"启用时窗口齐备"分支 ⇒ 本用例必须红。</remarks>
    [TestMethod]
    public void ShouldReject_OffPeakEnabledWithoutWindow()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() => SubconsciousRhythmPolicy.Create(
            policyId: "rhythm/test",
            version: 1,
            offPeakPriorityEnabled: true,
            offPeakWindowStart: null,
            offPeakWindowEnd: null,
            offPeakIntervalSeconds: 3600,
            backlogShortenIntervalSeconds: null,
            sourceLabel: "test"));
    }

    /// <summary>窗口只给一端 ⇒ 拒绝（半配置会让"看起来配了窗口"成为事实错误）。</summary>
    /// <remarks>取红：把 <c>HasConsistentWindow</c> 的两个 <c>is null</c> 检查改成 <c>||</c> ⇒ 本用例必须红。</remarks>
    [TestMethod]
    public void ShouldReject_HalfConfiguredWindow()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() => CreateWithOffPeakWindow(new TimeOnly(22, 0), null));
        Assert.ThrowsExactly<InvalidOperationException>(() => CreateWithOffPeakWindow(null, new TimeOnly(8, 0)));
    }

    /// <summary>空窗口（起点 == 终点）⇒ 拒绝（它会让判定恒假或恒真，外表像"关闭了该判据"）。</summary>
    /// <remarks>取红：把 <c>HasConsistentWindow</c> 里的 <c>!=</c> 改成 <c>==</c> ⇒ 本用例必须红。</remarks>
    [TestMethod]
    public void ShouldReject_EmptyWindow()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() => CreateWithOffPeakWindow(new TimeOnly(9, 0), new TimeOnly(9, 0)));
    }

    /// <summary>跨午夜窗口（22:00 → 08:00）是**合法**常规情形，⛔ 不得被"起点必须早于终点"的隐含假设拒绝。</summary>
    /// <remarks>取红：在 <c>HasConsistentWindow</c> 加一条 <c>Start &lt; End</c> 约束 ⇒ 本用例必须红。</remarks>
    [TestMethod]
    public void ShouldAccept_CrossMidnightWindow()
    {
        var policy = CreateWithOffPeakWindow(new TimeOnly(22, 0), new TimeOnly(8, 0));

        Assert.IsTrue(policy.IsValid);
        Assert.AreEqual(new TimeOnly(22, 0), policy.OffPeakWindowStart);
        Assert.AreEqual(new TimeOnly(8, 0), policy.OffPeakWindowEnd);
    }

    /// <summary>0 / 负值候选间隔 ⇒ 拒绝（0 会让窗口内自旋，外壳却像"已配置"）。</summary>
    /// <remarks>取红：把 <c>IsPositiveOrNull</c> 的 <c>is null or &gt; 0</c> 改成 <c>is null or &gt;= 0</c> ⇒ 本用例必须红。</remarks>
    [TestMethod]
    public void ShouldReject_NonPositiveCandidateIntervals()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() => CreateWithOffPeakWindow(new TimeOnly(22, 0), new TimeOnly(8, 0), offPeakInterval: 0));
        Assert.ThrowsExactly<InvalidOperationException>(() => CreateWithOffPeakWindow(new TimeOnly(22, 0), new TimeOnly(8, 0), offPeakInterval: -1));
        Assert.ThrowsExactly<InvalidOperationException>(() => CreateWithOffPeakWindow(
            new TimeOnly(22, 0), new TimeOnly(8, 0), offPeakInterval: 3600, backlogInterval: 0));
    }

    /// <summary>标识面非法（空 id / 空来源标注 / 版本 &lt; 1）⇒ 拒绝：结论无法自证用的是哪一版、哪来的。</summary>
    /// <remarks>取红：把 <c>Version &gt;= 1</c> 改成 <c>Version &gt;= 0</c> ⇒ 本用例必须红。</remarks>
    [TestMethod]
    public void ShouldReject_InvalidIdentity()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() => SubconsciousRhythmPolicy.Create(
            policyId: "  ", version: 1, offPeakPriorityEnabled: false,
            offPeakWindowStart: null, offPeakWindowEnd: null,
            offPeakIntervalSeconds: null, backlogShortenIntervalSeconds: null, sourceLabel: "test"));

        Assert.ThrowsExactly<InvalidOperationException>(() => SubconsciousRhythmPolicy.Create(
            policyId: "rhythm/test", version: 1, offPeakPriorityEnabled: false,
            offPeakWindowStart: null, offPeakWindowEnd: null,
            offPeakIntervalSeconds: null, backlogShortenIntervalSeconds: null, sourceLabel: " "));

        Assert.ThrowsExactly<InvalidOperationException>(() => SubconsciousRhythmPolicy.Create(
            policyId: "rhythm/test", version: 0, offPeakPriorityEnabled: false,
            offPeakWindowStart: null, offPeakWindowEnd: null,
            offPeakIntervalSeconds: null, backlogShortenIntervalSeconds: null, sourceLabel: "test"));
    }

    // ── 纯判定：命中 ⇒ 缩短（P2：值一律来自策略对象）─────────────────────────────

    /// <summary>非工作时段命中且启用 ⇒ 采用策略给出的间隔，理由码为 off_peak。</summary>
    /// <remarks>取红：把 <c>Resolve</c> 里候选的 <c>&lt; adopted</c> 改成 <c>&lt;=</c> 不红；应改成"忽略候选、直接返回配置间隔" ⇒ 本用例必须红。</remarks>
    [TestMethod]
    public void Resolve_ShouldShorten_WhenOffPeakEnabledAndInWindow()
    {
        var policy = CreateWithOffPeakWindow(new TimeOnly(22, 0), new TimeOnly(8, 0), offPeakInterval: 3_600);

        var decision = policy.Resolve(ConfiguredInterval, isOffPeak: true, isBacklog: false);

        Assert.AreEqual(3_600, decision.IntervalSeconds);
        Assert.IsTrue(decision.IsShortened);
        Assert.AreEqual(SubconsciousRhythmReasonCodes.OffPeak, decision.ReasonCode);
        Assert.IsTrue(decision.IsOffPeak, "判定输入必须被原样记录，便于事后核对。");
    }

    /// <summary>P4：非工作时段优先**关闭**时，即使判定为"在窗口内"也<b>不得</b>缩短。</summary>
    /// <remarks>取红：去掉 <c>OffPeakPriorityEnabled &amp;&amp;</c> 这一项 ⇒ 本用例必须红。</remarks>
    [TestMethod]
    public void Resolve_ShouldNotShorten_WhenOffPeakDisabled_EvenIfInWindow()
    {
        var policy = CreateWithOffPeakWindow(
            new TimeOnly(22, 0), new TimeOnly(8, 0), offPeakInterval: 3_600, enabled: false);

        var decision = policy.Resolve(ConfiguredInterval, isOffPeak: true, isBacklog: false);

        Assert.AreEqual(ConfiguredInterval, decision.IntervalSeconds);
        Assert.IsFalse(decision.IsShortened);
        Assert.AreEqual(SubconsciousRhythmReasonCodes.Default, decision.ReasonCode);
    }

    /// <summary>堆积命中 ⇒ 采用堆积间隔（条件触发的"缩短"部分；是否命中的断言属 D5）。</summary>
    /// <remarks>取红：把 <c>backlogCandidate</c> 恒置为 <c>null</c> ⇒ 本用例必须红。</remarks>
    [TestMethod]
    public void Resolve_ShouldShorten_WhenBacklog()
    {
        var policy = CreateWithBacklogInterval(1_800);

        var decision = policy.Resolve(ConfiguredInterval, isOffPeak: false, isBacklog: true);

        Assert.AreEqual(1_800, decision.IntervalSeconds);
        Assert.AreEqual(SubconsciousRhythmReasonCodes.Backlog, decision.ReasonCode);
    }

    /// <summary>两个条件同时命中且取值不同 ⇒ 取<b>更短者</b>（⛔ 不引入"哪个条件优先"的隐含裁决）。</summary>
    /// <remarks>取红：把 backlog 的更新条件改成 <c>&lt;=</c>（或固定"后写的赢"）⇒ 本用例必须红。</remarks>
    [TestMethod]
    public void Resolve_ShouldTakeTheShorter_WhenBothConditionsHit()
    {
        var policy = CreateWithOffPeakWindow(new TimeOnly(22, 0), new TimeOnly(8, 0), offPeakInterval: 3_600, backlogInterval: 900);

        var decision = policy.Resolve(ConfiguredInterval, isOffPeak: true, isBacklog: true);

        Assert.AreEqual(900, decision.IntervalSeconds, "两条件同时命中时必须取更短者。");
        Assert.AreEqual(SubconsciousRhythmReasonCodes.Backlog, decision.ReasonCode);
    }

    /// <summary>两个条件同时命中且取值<b>相同</b> ⇒ 理由码必须同时体现两者（不得静默丢掉一个事实）。</summary>
    /// <remarks>取红：删掉 <c>OffPeakAndBacklog</c> 分支 ⇒ 本用例必须红。</remarks>
    [TestMethod]
    public void Resolve_ShouldReportBothConditions_WhenTheyBindToTheSameValue()
    {
        var policy = CreateWithOffPeakWindow(new TimeOnly(22, 0), new TimeOnly(8, 0), offPeakInterval: 1_200, backlogInterval: 1_200);

        var decision = policy.Resolve(ConfiguredInterval, isOffPeak: true, isBacklog: true);

        Assert.AreEqual(1_200, decision.IntervalSeconds);
        Assert.AreEqual(SubconsciousRhythmReasonCodes.OffPeakAndBacklog, decision.ReasonCode);
    }

    // ── 永不延长 + 幻影生效防护 ─────────────────────────────────────────────────

    /// <summary>候选间隔<b>长于</b>配置间隔 ⇒ 不得采用（配置间隔是上界，防"配置笔误被策略放大"）。</summary>
    /// <remarks>取红：去掉 <c>&lt; adopted</c> 比较（无条件采用候选）⇒ 本用例必须红。</remarks>
    [TestMethod]
    public void Resolve_ShouldNeverLengthen_BeyondConfiguredInterval()
    {
        var policy = CreateWithOffPeakWindow(new TimeOnly(22, 0), new TimeOnly(8, 0), offPeakInterval: ConfiguredInterval * 2);

        var decision = policy.Resolve(ConfiguredInterval, isOffPeak: true, isBacklog: false);

        Assert.AreEqual(ConfiguredInterval, decision.IntervalSeconds, "采用间隔不得超过配置间隔。");
        Assert.AreEqual(SubconsciousRhythmReasonCodes.Default, decision.ReasonCode);
    }

    /// <summary>候选间隔<b>恰好等于</b>配置间隔 ⇒ 未缩短，理由码必须是 default（⛔ 不得认领 off_peak 造成"窗口已生效"的错觉）。</summary>
    /// <remarks>取红：把绑定判定里的 <c>&lt; configuredIntervalSeconds</c> 去掉 ⇒ 本用例必须红。</remarks>
    [TestMethod]
    public void Resolve_ShouldNotClaimOffPeak_WhenCandidateEqualsConfiguredInterval()
    {
        var policy = CreateWithOffPeakWindow(new TimeOnly(22, 0), new TimeOnly(8, 0), offPeakInterval: ConfiguredInterval);

        var decision = policy.Resolve(ConfiguredInterval, isOffPeak: true, isBacklog: false);

        Assert.AreEqual(ConfiguredInterval, decision.IntervalSeconds);
        Assert.IsFalse(decision.IsShortened);
        Assert.AreEqual(
            SubconsciousRhythmReasonCodes.Default,
            decision.ReasonCode,
            "没改短就不许认领 off_peak：理由码要解释采用间隔为什么是这个值。");
    }

    /// <summary>未配置堆积间隔但判定为堆积 ⇒ 沿用配置间隔（⛔ 不得抛异常、不得静默丢轮）。</summary>
    /// <remarks>取红：把 <c>backlogCandidate</c> 的空值处理改成抛异常 ⇒ 本用例必须红。</remarks>
    [TestMethod]
    public void Resolve_ShouldFallBackToConfigured_WhenBacklogIntervalNotConfigured()
    {
        var policy = SubconsciousRhythmPolicy.Create(
            policyId: "rhythm/test", version: 1, offPeakPriorityEnabled: false,
            offPeakWindowStart: null, offPeakWindowEnd: null,
            offPeakIntervalSeconds: null, backlogShortenIntervalSeconds: null, sourceLabel: "test");

        var decision = policy.Resolve(ConfiguredInterval, isOffPeak: false, isBacklog: true);

        Assert.AreEqual(ConfiguredInterval, decision.IntervalSeconds);
        Assert.AreEqual(SubconsciousRhythmReasonCodes.Default, decision.ReasonCode);
    }

    /// <summary>配置间隔 &lt; 1 秒 ⇒ 拒绝（会退化成自旋，且与既有 <c>Math.Max(1, …)</c> 口径不符）。</summary>
    /// <remarks>取红：把 <c>configuredIntervalSeconds &lt; 1</c> 改成 <c>&lt; 0</c> ⇒ 本用例必须红。</remarks>
    [TestMethod]
    public void Resolve_ShouldReject_NonPositiveConfiguredInterval()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => SubconsciousRhythmPolicy.Default.Resolve(0, false, false));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => SubconsciousRhythmPolicy.Default.Resolve(-1, false, false));
    }

    /// <summary>非法策略（绕过工厂直接构造）⇒ 判定前抛异常，⛔ 不得带着非法配置给出结论。</summary>
    /// <remarks>取红：把 <c>Resolve</c> 里的 <c>EnsureValid()</c> 删掉 ⇒ 本用例必须红。</remarks>
    [TestMethod]
    public void Resolve_ShouldReject_InvalidPolicy()
    {
        var invalid = new SubconsciousRhythmPolicy
        {
            PolicyId = "rhythm/invalid",
            Version = 1,
            OffPeakPriorityEnabled = true,
            OffPeakWindowStart = new TimeOnly(22, 0),
            OffPeakWindowEnd = new TimeOnly(8, 0),
            OffPeakIntervalSeconds = 0,
            BacklogShortenIntervalSeconds = null,
            SourceLabel = "test",
        };

        Assert.IsFalse(invalid.IsValid, "夹具本身必须是非法配置（否则本用例验的不是它要验的东西）。");
        Assert.ThrowsExactly<InvalidOperationException>(() => invalid.Resolve(ConfiguredInterval, true, false));
    }

    // ── 结构闸门：决定与执行分离 ─────────────────────────────────────────────────

    /// <summary>
    /// 结构闸门：策略与决定类型**只允许**出现"判定面"，⛔ 不得出现写入/执行面
    /// （<c>Apply</c>/<c>Execute</c>/<c>Save</c>/<c>Enqueue</c> …）。
    /// </summary>
    /// <remarks>取红：① 给策略加一个 <c>public void Apply(int seconds)</c> ⇒ 红；
    /// ② 把任一属性的 <c>init</c> 改成 <c>set</c> ⇒ 红（可写 setter 会让构造期校验形同虚设）。
    /// <para>⚠️ 诚实边界：这是**声明面**级闸门，证明的是"没有可调用的写入口、且策略构造后不可变"，
    /// 不等于证明"实现里绝对无副作用"（后者由零行为接线与 P7 的调用点审计覆盖）。</para></remarks>
    [TestMethod]
    public void PolicyAndDecision_ShouldExposeNoWriteOrExecutionSurface()
    {
        // 记录类型由编译器生成的成员：不属于"我们新增的行为面"，但必须列出来，
        // 这样"新增了一个方法"就一定会撞白名单并迫使作者显式裁决。
        var compilerGenerated = new HashSet<string>(StringComparer.Ordinal)
        {
            "EnsureValid",
            "Resolve",
            "Create",
            "Equals",
            "GetHashCode",
            "ToString",
            "PrintMembers",
            "Deconstruct",
            "<Clone>$",
            "op_Equality",
            "op_Inequality",
        };

        AssertNoUnexpectedBehaviorSurface(typeof(SubconsciousRhythmPolicy), compilerGenerated);
        AssertNoUnexpectedBehaviorSurface(typeof(SubconsciousRhythmDecision), compilerGenerated);
    }

    /// <summary>理由码是下游解析的唯一货币 ⇒ 字面值不得被静默重命名。</summary>
    /// <remarks>取红：改任一理由码字符串 ⇒ 本用例必须红。</remarks>
    [TestMethod]
    public void ReasonCodes_ShouldBeStable()
    {
        Assert.AreEqual("rhythm/default", SubconsciousRhythmReasonCodes.Default);
        Assert.AreEqual("rhythm/off_peak", SubconsciousRhythmReasonCodes.OffPeak);
        Assert.AreEqual("rhythm/backlog", SubconsciousRhythmReasonCodes.Backlog);
        Assert.AreEqual("rhythm/off_peak+backlog", SubconsciousRhythmReasonCodes.OffPeakAndBacklog);
    }

    // ── 夹具 ───────────────────────────────────────────────────────────────────

    /// <summary>枚举全部声明面成员；<c>init</c> 属性存取器不算写入面（它们正是“构造后不可变”的机制）。</summary>
    private static void AssertNoUnexpectedBehaviorSurface(Type type, HashSet<string> allowed)
    {
        var offenders = new List<string>();

        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            var name = method.Name;
            if (name.StartsWith("get_", StringComparison.Ordinal))
            {
                continue;
            }

            if (name.StartsWith("set_", StringComparison.Ordinal))
            {
                // 只有 init-only 存取器是合法的：真正的可写 setter 会让"构造期校验过的策略"在事后被改掉，
                // 于是校验形同虚设（判据可被静默移动，正是宪法 C1 要防的事）。
                if (!IsInitOnlySetter(type, name["set_".Length..]))
                {
                    offenders.Add(name + "(非 init 可写属性)");
                }

                continue;
            }

            if (!allowed.Contains(name))
            {
                offenders.Add(name);
            }
        }

        var distinct = offenders.Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToArray();

        Assert.AreEqual(
            0,
            distinct.Length,
            $"{type.Name} 出现了未裁决的行为面：{string.Join(",", distinct)}"
            + "（新增成员必须显式加入白名单，以防静默写入面）。");
    }

    private static bool IsInitOnlySetter(Type type, string propertyName)
    {
        var setter = type
            .GetProperty(
                propertyName,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            ?.SetMethod;

        return setter is not null
            && setter.ReturnParameter
                .GetRequiredCustomModifiers()
                .Any(modifier => modifier.FullName == "System.Runtime.CompilerServices.IsExternalInit");
    }

    private static SubconsciousRhythmPolicy CreateWithOffPeakWindow(
        TimeOnly? start,
        TimeOnly? end,
        int? offPeakInterval = 3_600,
        int? backlogInterval = null,
        bool enabled = true)
        => SubconsciousRhythmPolicy.Create(
            policyId: "rhythm/test",
            version: 7,
            offPeakPriorityEnabled: enabled,
            offPeakWindowStart: start,
            offPeakWindowEnd: end,
            offPeakIntervalSeconds: offPeakInterval,
            backlogShortenIntervalSeconds: backlogInterval,
            sourceLabel: "test");

    private static SubconsciousRhythmPolicy CreateWithBacklogInterval(int backlogInterval)
        => SubconsciousRhythmPolicy.Create(
            policyId: "rhythm/test",
            version: 7,
            offPeakPriorityEnabled: false,
            offPeakWindowStart: null,
            offPeakWindowEnd: null,
            offPeakIntervalSeconds: null,
            backlogShortenIntervalSeconds: backlogInterval,
            sourceLabel: "test");
}
