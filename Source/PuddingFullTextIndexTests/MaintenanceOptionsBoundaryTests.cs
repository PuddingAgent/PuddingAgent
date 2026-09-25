using System.Reflection;
using PuddingFullTextIndex.Infrastructure.Maintenance;

namespace PuddingFullTextIndexTests;

/// <summary>
/// S2b：<see cref="MaintenanceOptions"/> 每个护栏阈值的**区间语义**矩阵 —— 测的是「**区间两端是否都含**」，
/// 而不是「当初拍的那几个数字对不对」：采样值全部**对着 <see cref="MaintenanceOptions"/> 公开的界常量**构造，
/// 界常量将来改了测试自动跟随。
/// <para>
/// 覆盖口径（3 条互相独立的断言簇）：
/// <list type="number">
/// <item><description><b>区间语义</b>：每阈值 ≥ 4 个采样点（恰好下界 / 恰好上界 ⇒ 接受；越界一档 ⇒ 拒绝），
/// 且拒绝时**点名是哪个 Option**（只断言 <c>IsValid == false</c> 会让「任何一个选项报错」混过去）。</description></item>
/// <item><description><b>覆盖对照</b>：矩阵实际覆盖的选项名集合 与 从源码（+反射）收集的界常量清单**逐项相等** —— 漏一项即失败。</description></item>
/// <item><description><b>短路边界</b>：数值域校验**不随 <c>Enabled = false</c> 短路**，在**边界值（越上界一档）上**复核。</description></item>
/// </list>
/// </para>
/// <para>
/// 本用例**零 IO**：全部路径只做字符串运算，不建目录、不写文件（<c>Enabled = true</c> 分支只做路径规范化，不触盘）。
/// 刻意**不重复** S1a <c>MaintenanceCoreTests</c> 已覆盖的面（默认值冻结 / 禁用零要求 / 合法启用通过 /
/// 非法组合拒绝 / 关闭仍校验数值 的**非边界**样本）。
/// </para>
/// </summary>
[TestClass]
public sealed class MaintenanceOptionsBoundaryTests
{
    private static readonly string TempRoot = Path.GetTempPath();
    private static readonly string CorpusRoot = Path.Combine(TempRoot, "pudding-fts-s2b-corpus");
    private static readonly string IndexRoot = Path.Combine(TempRoot, "pudding-fts-s2b-index-root");

    private static readonly TimeSpan OneTick = TimeSpan.FromTicks(1);

    /// <summary>从源码里收集到的**待测选项名**清单（矩阵必须逐项覆盖；与界常量清单一一对应）。</summary>
    private static readonly string[] ExpectedOptionNames =
    {
        nameof(MaintenanceOptions.QueueCapacity),
        nameof(MaintenanceOptions.MaxBatchPaths),
        nameof(MaintenanceOptions.HealthCheckSliceFiles),
        nameof(MaintenanceOptions.RecoveryScanInterval),
        nameof(MaintenanceOptions.HealthCheckInterval),
        nameof(MaintenanceOptions.MTimeOverlap),
        nameof(MaintenanceOptions.HealthCheckSliceDelay),
        nameof(MaintenanceOptions.PressureBackoff),
    };

    /// <summary>从源码里收集到的**界常量名**清单（与反射实测的公开静态字段清单对照）。</summary>
    private static readonly string[] ExpectedBoundConstantNames =
    {
        "MaxQueueCapacityAllowed",
        "MaxBatchPathsAllowed",
        "MaxHealthCheckSliceFilesAllowed",
        "MinRecoveryScanInterval",
        "MaxRecoveryScanInterval",
        "MinHealthCheckInterval",
        "MaxHealthCheckInterval",
        "MaxMTimeOverlap",
        "MaxHealthCheckSliceDelay",
        "MaxPressureBackoff",
    };

    /// <summary>基线配置：合法（Enabled=true + 一个绝对 scope + 一个不嵌套的绝对索引根）。</summary>
    private static MaintenanceOptions EnabledBaseline() => new()
    {
        Enabled = true,
        Scopes = new[] { CorpusRoot },
        IndexRootDirectory = IndexRoot,
    };

    private sealed record BoundaryCase(
        string Option,
        string Domain,
        string Sample,
        Func<MaintenanceOptions, MaintenanceOptions> Apply,
        bool ExpectAccepted,
        string Rationale);

    // ── 矩阵构造 ────────────────────────────────────────────────────────

    private static void AddIntDomain(
        List<BoundaryCase> cases,
        string option,
        string domain,
        int lowerAccepted,
        int upperAccepted,
        Func<MaintenanceOptions, int, MaintenanceOptions> apply)
    {
        var mid = lowerAccepted + (upperAccepted - lowerAccepted) / 2;

        cases.Add(new(option, domain, "at-lower", o => apply(o, lowerAccepted), true,
            $"下界 {lowerAccepted} 是闭区间端点 ⇒ 必须接受"));
        cases.Add(new(option, domain, "below-lower", o => apply(o, lowerAccepted - 1), false,
            $"{lowerAccepted - 1} 越下界一档 ⇒ 必须拒绝"));
        cases.Add(new(option, domain, "mid", o => apply(o, mid), true,
            $"区间内部 {mid} ⇒ 必须接受（对照，防「恒拒绝」）"));
        cases.Add(new(option, domain, "at-upper", o => apply(o, upperAccepted), true,
            $"上界 {upperAccepted} 是闭区间端点 ⇒ 必须接受（含端点，off-by-one 的主要靶点）"));
        cases.Add(new(option, domain, "above-upper", o => apply(o, upperAccepted + 1), false,
            $"{upperAccepted + 1} 越上界一档 ⇒ 必须拒绝"));
    }

    private static void AddClosedSpanDomain(
        List<BoundaryCase> cases,
        string option,
        string domain,
        TimeSpan lower,
        TimeSpan upper,
        Func<MaintenanceOptions, TimeSpan, MaintenanceOptions> apply)
    {
        var mid = lower + TimeSpan.FromTicks((upper - lower).Ticks / 2);

        cases.Add(new(option, domain, "at-lower", o => apply(o, lower), true,
            $"下界 {lower} 是闭区间端点 ⇒ 必须接受"));
        cases.Add(new(option, domain, "below-lower", o => apply(o, lower - OneTick), false,
            $"下界减 1 tick ⇒ 必须拒绝"));
        cases.Add(new(option, domain, "mid", o => apply(o, mid), true,
            $"区间内部 {mid} ⇒ 必须接受（对照）"));
        cases.Add(new(option, domain, "at-upper", o => apply(o, upper), true,
            $"上界 {upper} 是闭区间端点 ⇒ 必须接受（含端点）"));
        cases.Add(new(option, domain, "above-upper", o => apply(o, upper + OneTick), false,
            $"上界加 1 tick ⇒ 必须拒绝"));
    }

    /// <summary>
    /// 全量边界矩阵：**8 个阈值 × 每阈值 ≥4（实际 5~6）个采样点**，共 41 行。
    /// 采样值一律取自 <see cref="MaintenanceOptions"/> 公开界常量。
    /// </summary>
    private static List<BoundaryCase> BuildCases()
    {
        var cases = new List<BoundaryCase>();

        // ① 下界 1（闭）/ 上界为公开常量（闭）的三个整型域
        AddIntDomain(cases, nameof(MaintenanceOptions.QueueCapacity), "QueueCapacity 1..MaxQueueCapacityAllowed",
            1, MaintenanceOptions.MaxQueueCapacityAllowed, (o, v) => o with { QueueCapacity = v });
        AddIntDomain(cases, nameof(MaintenanceOptions.MaxBatchPaths), "MaxBatchPaths 1..MaxBatchPathsAllowed",
            1, MaintenanceOptions.MaxBatchPathsAllowed, (o, v) => o with { MaxBatchPaths = v });
        AddIntDomain(cases, nameof(MaintenanceOptions.HealthCheckSliceFiles), "HealthCheckSliceFiles 1..MaxHealthCheckSliceFilesAllowed",
            1, MaintenanceOptions.MaxHealthCheckSliceFilesAllowed, (o, v) => o with { HealthCheckSliceFiles = v });

        // ② 两端都是公开常量的时间域
        AddClosedSpanDomain(cases, nameof(MaintenanceOptions.RecoveryScanInterval), "RecoveryScanInterval Min..MaxRecoveryScanInterval",
            MaintenanceOptions.MinRecoveryScanInterval, MaintenanceOptions.MaxRecoveryScanInterval,
            (o, v) => o with { RecoveryScanInterval = v });
        AddClosedSpanDomain(cases, nameof(MaintenanceOptions.HealthCheckInterval), "HealthCheckInterval Min..MaxHealthCheckInterval",
            MaintenanceOptions.MinHealthCheckInterval, MaintenanceOptions.MaxHealthCheckInterval,
            (o, v) => o with { HealthCheckInterval = v });

        // ③ 下界为 Zero（**闭**：源码 `MTimeOverlap < TimeSpan.Zero` 才拒）的两个时间域
        AddClosedSpanDomain(cases, nameof(MaintenanceOptions.MTimeOverlap), "MTimeOverlap 0..MaxMTimeOverlap",
            TimeSpan.Zero, MaintenanceOptions.MaxMTimeOverlap, (o, v) => o with { MTimeOverlap = v });
        AddClosedSpanDomain(cases, nameof(MaintenanceOptions.HealthCheckSliceDelay), "HealthCheckSliceDelay 0..MaxHealthCheckSliceDelay",
            TimeSpan.Zero, MaintenanceOptions.MaxHealthCheckSliceDelay, (o, v) => o with { HealthCheckSliceDelay = v });

        // ④ 下界为 Zero 但**开区间**（源码 `PressureBackoff <= TimeSpan.Zero` 就拒）——刻意与 ③ 区分
        var backoff = nameof(MaintenanceOptions.PressureBackoff);
        cases.Add(new(backoff, "PressureBackoff (0, MaxPressureBackoff]", "below-lower", o => o with { PressureBackoff = -OneTick }, false,
            "负值 ⇒ 必须拒绝"));
        cases.Add(new(backoff, "PressureBackoff (0, MaxPressureBackoff]", "at-lower-exclusive", o => o with { PressureBackoff = TimeSpan.Zero }, false,
            "下界是**开区间**（源码 <= TimeSpan.Zero 即拒）⇒ 0 必须拒绝；这不是 off-by-one 而是刻意口径"));
        cases.Add(new(backoff, "PressureBackoff (0, MaxPressureBackoff]", "just-inside-lower", o => o with { PressureBackoff = OneTick }, true,
            "1 tick 是开下界的第一个可接受值 ⇒ 必须接受"));
        cases.Add(new(backoff, "PressureBackoff (0, MaxPressureBackoff]", "mid", o => o with { PressureBackoff = TimeSpan.FromMinutes(30) }, true,
            "区间内部 ⇒ 必须接受（对照）"));
        cases.Add(new(backoff, "PressureBackoff (0, MaxPressureBackoff]", "at-upper", o => o with { PressureBackoff = MaintenanceOptions.MaxPressureBackoff }, true,
            "上界是闭区间端点 ⇒ 必须接受"));
        cases.Add(new(backoff, "PressureBackoff (0, MaxPressureBackoff]", "above-upper", o => o with { PressureBackoff = MaintenanceOptions.MaxPressureBackoff + OneTick }, false,
            "上界加 1 tick ⇒ 必须拒绝"));

        return cases;
    }

    // ── ① 区间语义 + 拒绝点名 + 覆盖对照 ────────────────────────────────

    [TestMethod]
    public void OptionsBoundary_EveryThreshold_AcceptsBothEnds_RejectsOneStepBeyond_AndNamesTheOption()
    {
        var cases = BuildCases();
        var evaluated = 0;
        var coveredOptions = new SortedSet<string>(StringComparer.Ordinal);
        var samplesPerOption = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var item in cases)
        {
            evaluated++;

            var result = MaintenanceOptions.Validate(item.Apply(EnabledBaseline()));
            var violatingOptions = result.Violations.Select(v => v.Option).ToArray();

            if (item.ExpectAccepted)
            {
                Assert.IsTrue(
                    result.IsValid,
                    $"[{item.Option} / {item.Sample}] 必须被接受（{item.Rationale}）；实际违规：{Describe(result)}");
                Assert.AreEqual(0, result.Violations.Count, $"[{item.Option} / {item.Sample}] 接受时不得有任何违规项");
            }
            else
            {
                Assert.IsFalse(
                    result.IsValid,
                    $"[{item.Option} / {item.Sample}] 必须被拒绝（{item.Rationale}）");

                // ★ 不能只断言 IsValid==false：必须点名是**哪一个**选项，且不能顺带牵连别的选项。
                CollectionAssert.AreEqual(
                    new[] { item.Option },
                    violatingOptions,
                    $"[{item.Option} / {item.Sample}] 拒绝必须点名该选项且只点它（{item.Rationale}）");
                Assert.IsTrue(result.Violations[0].Message.Length > 0, "违规项必须带可读消息");
                Assert.IsTrue(result.Violations[0].Value.Length > 0, "违规项必须带违规取值");
            }

            coveredOptions.Add(item.Option);
            samplesPerOption[item.Option] = samplesPerOption.TryGetValue(item.Option, out var seen) ? seen + 1 : 1;
        }

        // 行数自证
        Assert.AreEqual(cases.Count, evaluated, "矩阵必须逐行执行（行数自证）");
        Assert.AreEqual(ExpectedOptionNames.Length * 5 + 1, cases.Count, "8 个域各 5 采样点 + PressureBackoff 多 1 行 = 41（行数自证）");

        // 覆盖对照（防静默漏项）：矩阵实际覆盖到的选项名集合 与 源码收集清单逐项相等
        CollectionAssert.AreEquivalent(
            ExpectedOptionNames,
            coveredOptions.ToArray(),
            "覆盖对照失败：矩阵未覆盖到全部待测阈值（或覆盖了清单外的项）");

        foreach (var option in ExpectedOptionNames)
        {
            Assert.IsTrue(samplesPerOption.TryGetValue(option, out var count), $"{option} 未被矩阵覆盖");
            Assert.IsTrue(count >= 4, $"{option} 采样点必须 >= 4（实际 {count}）");
        }
    }

    // ── ② 采样值确实锚定在公开界常量上（防「清单抄了名字但没用常量」）──

    [TestMethod]
    public void OptionsBound_SamplesAreAnchoredToThePublicBoundConstants()
    {
        var cases = BuildCases();

        static MaintenanceOptions AtUpper(IReadOnlyList<BoundaryCase> all, string option)
            => all.Single(c => c.Option == option && c.Sample == "at-upper").Apply(EnabledBaseline());

        static MaintenanceOptions AtLower(IReadOnlyList<BoundaryCase> all, string option)
            => all.Single(c => c.Option == option && c.Sample == "at-lower").Apply(EnabledBaseline());

        Assert.AreEqual(MaintenanceOptions.MaxQueueCapacityAllowed, AtUpper(cases, nameof(MaintenanceOptions.QueueCapacity)).QueueCapacity);
        Assert.AreEqual(MaintenanceOptions.MaxBatchPathsAllowed, AtUpper(cases, nameof(MaintenanceOptions.MaxBatchPaths)).MaxBatchPaths);
        Assert.AreEqual(MaintenanceOptions.MaxHealthCheckSliceFilesAllowed, AtUpper(cases, nameof(MaintenanceOptions.HealthCheckSliceFiles)).HealthCheckSliceFiles);

        Assert.AreEqual(MaintenanceOptions.MinRecoveryScanInterval, AtLower(cases, nameof(MaintenanceOptions.RecoveryScanInterval)).RecoveryScanInterval);
        Assert.AreEqual(MaintenanceOptions.MaxRecoveryScanInterval, AtUpper(cases, nameof(MaintenanceOptions.RecoveryScanInterval)).RecoveryScanInterval);
        Assert.AreEqual(MaintenanceOptions.MinHealthCheckInterval, AtLower(cases, nameof(MaintenanceOptions.HealthCheckInterval)).HealthCheckInterval);
        Assert.AreEqual(MaintenanceOptions.MaxHealthCheckInterval, AtUpper(cases, nameof(MaintenanceOptions.HealthCheckInterval)).HealthCheckInterval);

        Assert.AreEqual(TimeSpan.Zero, AtLower(cases, nameof(MaintenanceOptions.MTimeOverlap)).MTimeOverlap);
        Assert.AreEqual(MaintenanceOptions.MaxMTimeOverlap, AtUpper(cases, nameof(MaintenanceOptions.MTimeOverlap)).MTimeOverlap);
        Assert.AreEqual(TimeSpan.Zero, AtLower(cases, nameof(MaintenanceOptions.HealthCheckSliceDelay)).HealthCheckSliceDelay);
        Assert.AreEqual(MaintenanceOptions.MaxHealthCheckSliceDelay, AtUpper(cases, nameof(MaintenanceOptions.HealthCheckSliceDelay)).HealthCheckSliceDelay);

        Assert.AreEqual(TimeSpan.Zero, cases.Single(c => c.Option == nameof(MaintenanceOptions.PressureBackoff) && c.Sample == "at-lower-exclusive").Apply(EnabledBaseline()).PressureBackoff);
        Assert.AreEqual(MaintenanceOptions.MaxPressureBackoff, AtUpper(cases, nameof(MaintenanceOptions.PressureBackoff)).PressureBackoff);
    }

    // ── ③ 界常量清单与磁盘逐项一致 ──────────────────────────────────────

    [TestMethod]
    public void OptionsBound_PublicStaticBoundConstants_MatchTheCollectedList()
    {
        var boundFields = typeof(MaintenanceOptions)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral || f.IsInitOnly)
            .ToArray();

        var intConsts = boundFields.Where(f => f.IsLiteral).ToArray();
        var spanStatics = boundFields.Where(f => f.IsInitOnly).ToArray();

        Assert.AreEqual(ExpectedBoundConstantNames.Length, boundFields.Length,
            "公开静态界常量清单必须与磁盘逐项一致（新增界常量而未纳入矩阵即失败）："
            + string.Join(", ", boundFields.Select(f => f.Name)));

        CollectionAssert.AreEquivalent(
            new[] { "MaxQueueCapacityAllowed", "MaxBatchPathsAllowed", "MaxHealthCheckSliceFilesAllowed" },
            intConsts.Select(f => f.Name).ToArray(),
            "整型容量上限必须是 const int");
        Assert.IsTrue(intConsts.All(f => f.FieldType == typeof(int)), "容量上限必须是 int");

        CollectionAssert.AreEquivalent(
            new[]
            {
                "MinRecoveryScanInterval", "MaxRecoveryScanInterval",
                "MinHealthCheckInterval", "MaxHealthCheckInterval",
                "MaxMTimeOverlap", "MaxHealthCheckSliceDelay", "MaxPressureBackoff",
            },
            spanStatics.Select(f => f.Name).ToArray(),
            "时间域界必须是 static readonly TimeSpan");
        Assert.IsTrue(spanStatics.All(f => f.FieldType == typeof(TimeSpan)), "时间域界必须是 TimeSpan");

        CollectionAssert.AreEquivalent(
            ExpectedBoundConstantNames,
            boundFields.Select(f => f.Name).ToArray(),
            "界常量清单与矩阵引用的清单不一致");
    }

    // ── ④ 关闭状态下的**边界值**短路复核 ───────────────────────────────

    [TestMethod]
    public void OptionsBound_AboveUpperSample_IsStillRejectedWhenDisabled()
    {
        var beyondUpper = BuildCases().Where(c => c.Sample == "above-upper").ToArray();

        Assert.AreEqual(ExpectedOptionNames.Length, beyondUpper.Length,
            "每个阈值必须恰好有一个「越上界一档」采样点（8 个域 ⇒ 8 行）");

        foreach (var item in beyondUpper)
        {
            var disabled = item.Apply(EnabledBaseline() with { Enabled = false });
            var result = MaintenanceOptions.Validate(disabled);

            Assert.IsFalse(
                result.IsValid,
                $"[{item.Option}] Enabled=false 只短路路径/scope 域；数值域越界仍必须拒绝（{item.Rationale}）");
            CollectionAssert.AreEqual(
                new[] { item.Option },
                result.Violations.Select(v => v.Option).ToArray(),
                $"[{item.Option}] 关闭状态下拒绝仍必须点名该选项");
        }
    }

    private static string Describe(MaintenanceOptionsValidationResult result)
        => result.Violations.Count == 0
            ? "(none)"
            : string.Join(" | ", result.Violations.Select(v => $"{v.Option}={v.Value}"));
}
