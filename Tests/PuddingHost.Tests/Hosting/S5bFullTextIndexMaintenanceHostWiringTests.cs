using System.Diagnostics;
using Microsoft.Extensions.Options;
using PuddingAgent.Services;
using PuddingFullTextIndex;
using PuddingFullTextIndex.Contracts;
using PuddingFullTextIndex.Infrastructure.Maintenance;
using PuddingFullTextIndex.Infrastructure.Search;
using PuddingHost.Hosting;
using Xunit.Abstractions;

namespace PuddingHost.Tests.Hosting;

/// <summary>
/// S5b（2026-09-27）宿主接线断言 I1~I6（逐条可独立取红）+ 一条附加的 fail-closed 契约。
/// <list type="bullet">
/// <item>I1：默认关闭 ⇒ 组合 0 次构造、维护器 0 次 Start、引擎 0 次调用、索引根零写入、0 error；</item>
/// <item>I2：启动 ⇒ 维护器 <c>StartAsync</c> **恰好 1 次**（宿主重复启动不重复），且 scope 规范键与
/// <b>供给侧（真实协调器）对同一语料根算出的键逐字符相同</b>；</item>
/// <item>I3：维护组合与查询侧共用**同一个**引擎实例与**同一个**索引根选项实例（引用相等）；</item>
/// <item>I4：停止 ⇒ <c>StopAsync</c> **恰好 1 次**、无在跑的维护（Running=false + StoppedUtc 已盖）；</item>
/// <item>I5：维护节配置（与组件默认值不同的值）**显式流入**组件，而预算 / scope / 索引根仍走**单一真源**；</item>
/// <item>I6：维护配置非法（<c>QueueCapacity</c> 超上限）⇒ fail-closed：不构造、不启动、如实记 Error；</item>
/// <item>附加：维护开启但**供给**未开启 ⇒ 拒绝且不猜 scope（scope 清单的唯一真源在供给节）。</item>
/// </list>
/// ⚠️ 夹具只在 <see cref="Path.GetTempPath"/> 下工作，绝不触碰真实索引根（<c>D:\data\fulltext-index</c>）。
/// </summary>
public sealed class S5bFullTextIndexMaintenanceHostWiringTests
{
    private readonly ITestOutputHelper _output;

    /// <summary>测试输出（把「默认关闭零写入」的前后对照写进原始输出，便于人工复核）。</summary>
    public S5bFullTextIndexMaintenanceHostWiringTests(ITestOutputHelper output) => _output = output;

    // ── I1 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// I1：默认配置（<c>FullTextIndex:Maintenance:Enabled=false</c>）⇒ <c>StartAsync</c> **首句**返回：
    /// 组合 0 次构造、维护器 0 次构造 / 0 次 Start、引擎 0 次调用、索引根**连目录都没建**、无 Error 日志。
    /// <para>
    /// ⚠️ 刻意把**供给**写成「开着且 scope 存在」：只要维护门控被拆掉（M1），
    /// 解析 → 校验 → 组合构造 → StartAsync 就会真的发生，下面的计数立刻变红。
    /// </para>
    /// </summary>
    [Fact]
    public async Task I1_Default_Disabled_Maintenance_Is_A_NoOp_With_Zero_Side_Effects()
    {
        using var fixture = new S5Fixture();
        var corpus = fixture.NewCorpus("i1", ("note.txt", "alpha"));

        var supplyOptions = new FullTextIndexSupplyOptions
        {
            Enabled = true,
            Scopes = [corpus],
            MaxIndexBytes = FullTextIndexSupplyOptions.DefaultMaxIndexBytes,
            MinRebuildInterval = TimeSpan.Zero,
        };

        var engine = new CountingRootedEngine(fixture.IndexRoot);
        var counting = new CountingMaintenance();
        var stubComposition = new StubMaintenanceComposition(
            counting, [], new MaintenanceOptions(), fixture.Options, engine);
        var factory = new StubMaintenanceCompositionFactory(stubComposition);
        var logger = new MaintenanceRecordingLogger();

        var indexRootExistedBefore = Directory.Exists(fixture.IndexRoot);
        var rootLastWriteBefore = Directory.GetLastWriteTimeUtc(fixture.Root);

        var service = CreateService(
            fixture.Options,
            supplyOptions,
            new MaintenanceOptions { Enabled = false },
            factory,
            logger);

        var stopwatch = Stopwatch.StartNew();
        await service.StartAsync(CancellationToken.None);
        stopwatch.Stop();

        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(1),
            $"StartAsync 必须立即返回（默认关闭），实测 {stopwatch.ElapsedMilliseconds} ms");

        // 暴露窗口：门控若被拆掉（M1），这段等待足以让组合构造 / StartAsync 计数变红。
        await Task.Delay(300);

        Assert.Equal(0, factory.CreateCalls);
        Assert.Empty(factory.CreatedWithOptions);
        Assert.Equal(0, counting.StartCalls);
        Assert.Equal(0, counting.StopCalls);
        Assert.Equal(0, engine.HasIndexCalls);
        Assert.Equal(0, engine.ResolveCalls);
        Assert.Equal(0, engine.BuildCalls);
        Assert.Equal(0, engine.InvalidateCalls);
        Assert.Equal(0, engine.SearchCalls);

        // 0 watcher / 0 线程的机制证据：组合根本没被构造 ⇒ 维护器（唯一建 watcher / 起线程的实体）
        // 连实例都不存在；「索引根连目录都不建」是它的可观测形式。
        Assert.False(
            Directory.Exists(fixture.IndexRoot),
            "默认关闭 ⇒ 索引根必须零写入（连目录都不许建）");
        Assert.Equal(rootLastWriteBefore, Directory.GetLastWriteTimeUtc(fixture.Root));

        Assert.DoesNotContain(
            logger.Entries,
            static e => e.StartsWith("Error:", StringComparison.Ordinal));

        // 停止时也不许碰组件：零副作用包括「停的时候不动它」。
        await service.StopAsync(CancellationToken.None);
        Assert.Equal(0, counting.StopCalls);

        _output.WriteLine($"[DEMO/I1] temp root              : {fixture.Root}");
        _output.WriteLine($"[DEMO/I1] index root             : {fixture.IndexRoot}");
        _output.WriteLine(
            $"[DEMO/I1] index root exists      : before={indexRootExistedBefore} after={Directory.Exists(fixture.IndexRoot)}");
        _output.WriteLine(
            $"[DEMO/I1] temp root mtime (UTC)  : before={rootLastWriteBefore:o} after={Directory.GetLastWriteTimeUtc(fixture.Root):o} "
            + $"equal={rootLastWriteBefore == Directory.GetLastWriteTimeUtc(fixture.Root)}");
        _output.WriteLine($"[DEMO/I1] composition creates    : {factory.CreateCalls}");
        _output.WriteLine($"[DEMO/I1] maintenance starts     : {counting.StartCalls}");
        _output.WriteLine($"[DEMO/I1] log entries            : {logger.Entries.Count}");
    }

    // ── I2 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// I2：开启 + 合法 scope ⇒ 维护器 <c>StartAsync</c> **恰好 1 次**（宿主重复启动被合并，不重复建 watcher），
    /// 且传入的 <see cref="FullTextMaintenanceScope.ScopeKey"/> 与**供给侧真实协调器**对同一语料根算出的键
    /// **逐字符相同**（D1 单一真源的可测形式）。
    /// </summary>
    [Fact]
    public async Task I2_Start_Is_Called_Exactly_Once_And_Scope_Keys_Match_The_Supply_Side()
    {
        using var fixture = new S5Fixture();
        var corpus = fixture.NewCorpus("i2", ("alpha.txt", "hello berryneedle world"));

        using var lucene = new LuceneSearchEngine(fixture.Options);
        var logger = new MaintenanceRecordingLogger();

        var supplyOptions = new FullTextIndexSupplyOptions
        {
            Enabled = true,
            Scopes = [corpus],
            MaxIndexBytes = FullTextIndexSupplyOptions.DefaultMaxIndexBytes,
            MinRebuildInterval = TimeSpan.Zero,
        };

        var productionFactory = new LuceneFullTextIndexMaintenanceCompositionFactory(
            fixture.Options, lucene, new UnavailablePressureProbe(), TimeProvider.System);
        var recording = new RecordingMaintenanceCompositionFactory(productionFactory);

        var service = CreateService(
            fixture.Options, supplyOptions, new MaintenanceOptions { Enabled = true }, recording, logger);

        try
        {
            await service.StartAsync(CancellationToken.None);
            // 幂等：宿主重复启动（重复注册 / 重启同一实例）不得让组件重复建 watcher。
            await service.StartAsync(CancellationToken.None);

            Assert.True(
                await S5TestHelpers.WaitUntilAsync(
                    () => !recording.Created.IsEmpty && recording.Created.First().Maintenance.StartCalls == 1,
                    TimeSpan.FromSeconds(30)),
                "维护器必须在有界时间内收到恰好一次 StartAsync");

            // 收尾窗口：若发生第二次 StartAsync，会在这段时间内被观测到。
            await Task.Delay(200);

            Assert.Equal(1, recording.CreateCalls);
            var composition = Assert.Single(recording.Created);
            Assert.Equal(1, composition.Maintenance.StartCalls);

            var scopes = Assert.Single(composition.Maintenance.StartScopes);
            var scope = Assert.Single(scopes);

            Assert.Equal(supplyOptions.MaxIndexBytes, scope.MaxIndexBytes);
            Assert.Equal(
                FullTextChangeCoalescer.NormalizeComparisonKey(corpus),
                scope.ScopeKey,
                StringComparer.Ordinal);

            // 供给侧（**真实协调器**）对同一语料根算出的键：必须逐字符相同。
            var supplyFactory = new LuceneFullTextIndexSupplyCompositionFactory(
                fixture.Options, lucene, stagingOptions => new LuceneSearchEngine(stagingOptions));
            var plan = await supplyFactory.Create(supplyOptions)
                .Coordinator.PlanAsync(new SupplyScopeRequest(corpus));

            Assert.True(plan.Accepted, "前置：供给侧必须接受该 scope");
            var supplyScope = Assert.Single(plan.AcceptedScopes);
            Assert.Equal(supplyScope.ScopeKey, scope.ScopeKey, StringComparer.Ordinal);
            Assert.Equal(supplyScope.RootPath, scope.RootPath, StringComparer.Ordinal);

            _output.WriteLine($"[DEMO/I2] scope root             : {scope.RootPath}");
            _output.WriteLine($"[DEMO/I2] maintenance scope key  : {scope.ScopeKey}");
            _output.WriteLine($"[DEMO/I2] supply-side scope key  : {supplyScope.ScopeKey}");
            _output.WriteLine($"[DEMO/I2] start calls            : {composition.Maintenance.StartCalls}");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    // ── I3 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// I3：维护组合必须与**查询侧**共用同一个引擎实例（D4；引用相等）与同一个索引根选项实例，
    /// 且生效 scope 的预算逐字来自供给同源值。
    /// <para>
    /// ⚠️ 引擎实例同一性是**功能性的**：维护 commit 后要经 <c>IScopeReaderInvalidation</c> 失效该语料根的
    /// reader 缓存，失效打在别的实例上等于没失效（查询会一直看到陈旧文档）。
    /// </para>
    /// </summary>
    [Fact]
    public void I3_Maintenance_Composition_Reuses_The_Query_Side_Engine_And_Index_Options()
    {
        using var fixture = new S5Fixture();
        var corpus = fixture.NewCorpus("i3", ("a.txt", "aaa"));

        using var querySideEngine = new LuceneSearchEngine(fixture.Options);
        var factory = new LuceneFullTextIndexMaintenanceCompositionFactory(
            fixture.Options, querySideEngine, new UnavailablePressureProbe(), TimeProvider.System);

        const long configuredBudget = 3_221_225_472L;
        var supplyOptions = new FullTextIndexSupplyOptions
        {
            Enabled = true,
            Scopes = [corpus],
            MaxIndexBytes = configuredBudget,
        };

        var effective = FullTextIndexMaintenanceOptions.ApplySingleSource(
            new MaintenanceOptions { Enabled = true }, supplyOptions, fixture.Options);

        var composition = factory.Create(effective, [corpus]);

        // ★ D4：与查询侧同一个**实例**（M2 变异：组合根新建实例 ⇒ 这里必须变红）。
        Assert.Same(querySideEngine, composition.LiveEngine);
        Assert.Same(fixture.Options, composition.IndexOptions);

        var scope = Assert.Single(composition.Scopes);
        Assert.Equal(configuredBudget, scope.MaxIndexBytes);
        Assert.Equal(configuredBudget, composition.ComponentOptions.MaxIndexBytes);
        Assert.Equal(
            FullTextChangeCoalescer.NormalizeComparisonKey(corpus),
            scope.ScopeKey,
            StringComparer.Ordinal);
        Assert.Null(scope.IndexDirectory);   // 宿主不复刻命名哈希（D5）：让维护器经同一真源推导

        // R4：硬断言不得落在真实索引根上。
        Assert.StartsWith(Path.GetTempPath(), fixture.Root, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(
            Path.GetTempPath(), composition.IndexOptions.IndexRootDirectory!, StringComparison.OrdinalIgnoreCase);
    }

    // ── I4 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// I4：宿主停止 ⇒ 维护器 <c>StopAsync</c> **恰好 1 次**（重复停止被合并），且维护不再运行、
    /// 已盖停止时刻（组件 <c>StopAsync</c> 是释放 watcher / 体检线程的**唯一**出口）。
    /// </summary>
    [Fact]
    public async Task I4_Host_Stop_Calls_Stop_Exactly_Once_And_Leaves_No_Running_Maintenance()
    {
        using var fixture = new S5Fixture();
        var corpus = fixture.NewCorpus("i4", ("a.txt", "aaa"));

        using var lucene = new LuceneSearchEngine(fixture.Options);
        var logger = new MaintenanceRecordingLogger();

        var supplyOptions = new FullTextIndexSupplyOptions
        {
            Enabled = true,
            Scopes = [corpus],
            MaxIndexBytes = FullTextIndexSupplyOptions.DefaultMaxIndexBytes,
            MinRebuildInterval = TimeSpan.Zero,
        };

        var productionFactory = new LuceneFullTextIndexMaintenanceCompositionFactory(
            fixture.Options, lucene, new UnavailablePressureProbe(), TimeProvider.System);
        var recording = new RecordingMaintenanceCompositionFactory(productionFactory);

        var service = CreateService(
            fixture.Options, supplyOptions, new MaintenanceOptions { Enabled = true }, recording, logger);

        await service.StartAsync(CancellationToken.None);
        Assert.True(
            await S5TestHelpers.WaitUntilAsync(
                () => !recording.Created.IsEmpty && recording.Created.First().Maintenance.StartCalls == 1,
                TimeSpan.FromSeconds(30)),
            "前置：维护必须已启动");

        var composition = Assert.Single(recording.Created);
        Assert.True(composition.Maintenance.GetSnapshot().Running, "前置：维护必须在运行");

        await service.StopAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);   // 幂等：宿主重复停止

        Assert.Equal(1, composition.Maintenance.StopCalls);

        var snapshot = composition.Maintenance.GetSnapshot();
        Assert.False(snapshot.Running, "停止后维护不得仍在运行（Running=false 即 watcher / 体检线程已释放）");
        Assert.NotNull(snapshot.StoppedUtc);
        Assert.Contains(
            logger.Entries,
            static e => e.Contains("局部维护已停止", StringComparison.Ordinal));

        _output.WriteLine($"[DEMO/I4] stop calls             : {composition.Maintenance.StopCalls}");
        _output.WriteLine($"[DEMO/I4] running after stop     : {snapshot.Running}");
        _output.WriteLine($"[DEMO/I4] started/stopped (UTC)  : {snapshot.StartedUtc:o} / {snapshot.StoppedUtc:o}");
        _output.WriteLine($"[DEMO/I4] applied/failed batches : {snapshot.AppliedBatchCount} / {snapshot.FailedBatchCount}");
    }

    // ── I5 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// I5：维护节的旋钮（与组件默认值**不同**：<c>QueueCapacity</c> / <c>RecoveryScanInterval</c>）
    /// **显式流入**组件；而预算 / scope / 索引根仍走单一真源 —— 维护节里故意写一个**诱饵预算**，
    /// 生效值必须来自供给节（否则「两处真源」立刻可证）。
    /// </summary>
    [Fact]
    public async Task I5_Configured_Maintenance_Values_Flow_In_While_Budget_Stays_Single_Sourced()
    {
        using var fixture = new S5Fixture();
        var corpus = fixture.NewCorpus("i5", ("a.txt", "aaa"));

        using var lucene = new LuceneSearchEngine(fixture.Options);
        var logger = new MaintenanceRecordingLogger();

        const int configuredQueueCapacity = 1234;                        // 组件默认 4096
        var configuredRecoveryScanInterval = TimeSpan.FromMinutes(20);   // 组件默认 15 分钟
        const long configuredSupplyBudget = 3_221_225_472L;              // 3 GiB：与两个默认值都不同
        const long decoyMaintenanceBudget = 999L;                        // 诱饵：维护节里的预算必须被覆盖

        var supplyOptions = new FullTextIndexSupplyOptions
        {
            Enabled = true,
            Scopes = [corpus],
            MaxIndexBytes = configuredSupplyBudget,
            MinRebuildInterval = TimeSpan.Zero,
        };

        var bound = new MaintenanceOptions
        {
            Enabled = true,
            QueueCapacity = configuredQueueCapacity,
            RecoveryScanInterval = configuredRecoveryScanInterval,
            MaxIndexBytes = decoyMaintenanceBudget,
        };

        var productionFactory = new LuceneFullTextIndexMaintenanceCompositionFactory(
            fixture.Options, lucene, new UnavailablePressureProbe(), TimeProvider.System);
        var recording = new RecordingMaintenanceCompositionFactory(productionFactory);

        var service = CreateService(fixture.Options, supplyOptions, bound, recording, logger);

        try
        {
            await service.StartAsync(CancellationToken.None);
            Assert.True(
                await S5TestHelpers.WaitUntilAsync(
                    () => !recording.CreatedWithOptions.IsEmpty, TimeSpan.FromSeconds(30)),
                "维护组合必须被构造（配置合法）");

            var effective = Assert.Single(recording.CreatedWithOptions);
            var componentOptions = Assert.Single(recording.Created).ComponentOptions;

            // ① 配置显式流入（照 A6 的做法：必须与组件默认值不同，否则不可证）。
            Assert.Equal(configuredQueueCapacity, effective.QueueCapacity);
            Assert.NotEqual(new MaintenanceOptions().QueueCapacity, effective.QueueCapacity);
            Assert.Equal(configuredRecoveryScanInterval, effective.RecoveryScanInterval);
            Assert.NotEqual(new MaintenanceOptions().RecoveryScanInterval, effective.RecoveryScanInterval);
            Assert.Equal(configuredQueueCapacity, componentOptions.QueueCapacity);
            Assert.Equal(configuredRecoveryScanInterval, componentOptions.RecoveryScanInterval);

            // ② 单一真源：预算来自供给节，诱饵被覆盖 —— 宿主没有第二份预算真源。
            Assert.Equal(configuredSupplyBudget, effective.MaxIndexBytes);
            Assert.NotEqual(decoyMaintenanceBudget, effective.MaxIndexBytes);
            Assert.NotEqual(FullTextIndexSupplyOptions.DefaultMaxIndexBytes, effective.MaxIndexBytes);
            Assert.NotEqual(new MaintenanceOptions().MaxIndexBytes, effective.MaxIndexBytes);
            Assert.Equal(fixture.Options.IndexRootDirectory, effective.IndexRootDirectory);

            // ③ scope 清单同样来自供给节（第一个 scope 即语料根）。
            S5TestHelpers.AssertPathsEqual(
                [corpus],
                Assert.Single(recording.Created).Scopes.Select(static s => s.RootPath).ToArray());

            _output.WriteLine($"[DEMO/I5] queue capacity (in)    : {effective.QueueCapacity}");
            _output.WriteLine($"[DEMO/I5] recovery interval (in) : {effective.RecoveryScanInterval}");
            _output.WriteLine($"[DEMO/I5] budget (in)            : {effective.MaxIndexBytes} (decoy was {decoyMaintenanceBudget})");
            _output.WriteLine($"[DEMO/I5] index root (in)        : {effective.IndexRootDirectory}");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    // ── I6 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// I6：维护配置非法（<c>QueueCapacity</c> 超上限）⇒ **fail-closed**：不构造组合、不启动维护、
    /// 如实记 Error（含违规项名与值）、不静默取默认值（无任何组件被构造），其余功能不受影响
    /// （服务照常返回，宿主启动不被阻塞）。
    /// </summary>
    [Fact]
    public async Task I6_Invalid_Maintenance_Config_Fails_Closed_And_Reports_The_Reason()
    {
        using var fixture = new S5Fixture();
        var corpus = fixture.NewCorpus("i6", ("a.txt", "aaa"));

        var supplyOptions = new FullTextIndexSupplyOptions
        {
            Enabled = true,
            Scopes = [corpus],
            MaxIndexBytes = FullTextIndexSupplyOptions.DefaultMaxIndexBytes,
            MinRebuildInterval = TimeSpan.Zero,
        };

        var engine = new CountingRootedEngine(fixture.IndexRoot);
        var counting = new CountingMaintenance();
        var stubComposition = new StubMaintenanceComposition(
            counting, [], new MaintenanceOptions(), fixture.Options, engine);
        var factory = new StubMaintenanceCompositionFactory(stubComposition);
        var logger = new MaintenanceRecordingLogger();

        var invalid = new MaintenanceOptions
        {
            Enabled = true,
            QueueCapacity = MaintenanceOptions.MaxQueueCapacityAllowed + 1,
        };

        // 前置：这份配置确实是**非法**的（组件的 fail-closed 校验必须判出来）。
        Assert.False(MaintenanceOptions.Validate(invalid).IsValid, "前置：QueueCapacity 超上限必须被判非法");

        var service = CreateService(fixture.Options, supplyOptions, invalid, factory, logger);

        await service.StartAsync(CancellationToken.None);

        Assert.True(
            await S5TestHelpers.WaitUntilAsync(
                () => logger.Entries.Any(
                    static e => e.StartsWith("Error:", StringComparison.Ordinal)
                                && e.Contains("QueueCapacity", StringComparison.Ordinal)),
                TimeSpan.FromSeconds(30)),
            "非法配置必须在有界时间内如实记 Error（含违规项名）");

        Assert.Equal(0, factory.CreateCalls);
        Assert.Empty(factory.CreatedWithOptions);   // ⇒ 默认值不可能被静默用上（没有组件被构造）
        Assert.Equal(0, counting.StartCalls);
        Assert.Equal(0, engine.ResolveCalls);
        Assert.Equal(0, engine.HasIndexCalls);
        Assert.False(Directory.Exists(fixture.IndexRoot), "fail-closed ⇒ 索引根零写入");

        await service.StopAsync(CancellationToken.None);
        Assert.Equal(0, counting.StopCalls);

        _output.WriteLine($"[DEMO/I6] log entries            : {logger.Entries.Count}");
        foreach (var entry in logger.Entries)
            _output.WriteLine($"[DEMO/I6] {entry}");
    }

    // ── 附加契约：供给未开启 ⇒ 拒绝（不猜 scope）────────────────────────────

    /// <summary>
    /// 附加：维护开启但**供给**未开启 ⇒ fail-closed 拒绝，且**不**自行解析 scope ——
    /// scope 清单 / 基准 / 预算的唯一真源在供给节，猜一份出来等于第二处真源。
    /// </summary>
    [Fact]
    public async Task Supply_Disabled_Makes_Maintenance_Refuse_Instead_Of_Guessing_Scopes()
    {
        using var fixture = new S5Fixture();
        var corpus = fixture.NewCorpus("supply-off", ("a.txt", "aaa"));

        var supplyOptions = new FullTextIndexSupplyOptions
        {
            Enabled = false,          // ★ 供给关闭（即使 Scopes 写着存在的目录）
            Scopes = [corpus],
        };

        var engine = new CountingRootedEngine(fixture.IndexRoot);
        var counting = new CountingMaintenance();
        var stubComposition = new StubMaintenanceComposition(
            counting, [], new MaintenanceOptions(), fixture.Options, engine);
        var factory = new StubMaintenanceCompositionFactory(stubComposition);
        var logger = new MaintenanceRecordingLogger();

        var service = CreateService(
            fixture.Options, supplyOptions, new MaintenanceOptions { Enabled = true }, factory, logger);

        var stopwatch = Stopwatch.StartNew();
        await service.StartAsync(CancellationToken.None);
        stopwatch.Stop();

        Assert.True(
            await S5TestHelpers.WaitUntilAsync(
                () => logger.Entries.Any(
                    static e => e.StartsWith("Error:", StringComparison.Ordinal)
                                && e.Contains("供给未开启", StringComparison.Ordinal)),
                TimeSpan.FromSeconds(30)),
            "供给未开启必须如实记 Error（而不是静默不维护）");

        Assert.Equal(0, factory.CreateCalls);
        Assert.Equal(0, counting.StartCalls);
        Assert.Equal(0, engine.ResolveCalls);
        Assert.Equal(0, engine.HasIndexCalls);
        Assert.False(Directory.Exists(fixture.IndexRoot));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), "StartAsync 仍必须立即返回");

        await service.StopAsync(CancellationToken.None);
    }

    // ── 局部工具 ──────────────────────────────────────────────────────────

    private static FullTextIndexMaintenanceHostedService CreateService(
        FullTextIndexOptions indexOptions,
        FullTextIndexSupplyOptions supplyOptions,
        MaintenanceOptions maintenanceOptions,
        IFullTextIndexMaintenanceCompositionFactory factory,
        MaintenanceRecordingLogger logger) =>
        new(
            Options.Create(maintenanceOptions),
            Options.Create(supplyOptions),
            indexOptions,
            factory,
            logger)
        {
            StartCompletionTimeout = TimeSpan.FromSeconds(10),
        };
}
