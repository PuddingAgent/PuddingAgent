using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PuddingAgent.Services;
using PuddingFullTextIndex;
using PuddingFullTextIndex.Contracts;
using PuddingFullTextIndex.Infrastructure.Search;
using PuddingFullTextIndex.Infrastructure.Supply;
using PuddingHost.Hosting;
using Xunit.Abstractions;

namespace PuddingHost.Tests.Hosting;

/// <summary>
/// S5（2026-09-25）宿主接线断言 A1~A6 + 轮询超时（A 组，逐条可独立取红）。
/// <list type="bullet">
/// <item>A1：默认关闭 ⇒ 不构造组合、协调器/引擎**0 次调用**、索引根零写入；</item>
/// <item>A2：启用 ⇒ 走协调器（**真实 Lucene** 端到端可查询），**live 引擎从未被直写**；</item>
/// <item>A3：预算不足 ⇒ OverBudget 如实记录、live 逐字节不变、旧索引仍可查；</item>
/// <item>A4：Busy（真实跨进程租约被别的 owner 占住）⇒ 只提交一次、如实记录、继续下一 scope；</item>
/// <item>A5：per-scope 新鲜度 ⇒ 只提交陈旧的 scope（A 新鲜只跳 A）；</item>
/// <item>A6：配置值（预算 / 最小重建间隔）显式流入组件；</item>
/// <item>附加：轮询到终态**有超时上限**，超时如实记录且不重试、不影响后续 scope。</item>
/// </list>
/// ⚠️ 夹具只在 <see cref="Path.GetTempPath"/> 下工作，绝不触碰真实索引根（<c>D:\data\fulltext-index</c>）。
/// </summary>
public sealed class S5IndexSupplyHostWiringTests
{
    private readonly ITestOutputHelper _output;

    /// <summary>测试输出（用于把「默认关闭零写入」的前后对照与真实索引布局写进原始输出）。</summary>
    public S5IndexSupplyHostWiringTests(ITestOutputHelper output) => _output = output;

    // ── A1 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// A1：默认配置（<c>Enabled=false</c>，即使 <c>Scopes</c> 写了存在的目录）⇒
    /// <c>StartAsync</c> 立即返回；**不构造供给组合**（工厂 0 次调用）、协调器 0 次调用、
    /// 引擎 0 次调用、索引根**连目录都没建**。
    /// </summary>
    [Fact]
    public async Task A1_Default_Configuration_Supplies_Nothing_And_Touches_No_Index_State()
    {
        using var fixture = new S5Fixture();
        var corpus = fixture.NewCorpus("a1", ("note.txt", "alpha"));
        var engine = new CountingRootedEngine(fixture.IndexRoot);
        var coordinator = new ScriptedSupplyCoordinator();
        var composition = new TestSupplyComposition(
            coordinator,
            new SupplyCoordinatorOptions(),
            _ => DateTimeOffset.UtcNow);
        var factory = new StubCompositionFactory(composition);
        var logger = new SupplyRecordingLogger();

        var rootLastWriteBefore = Directory.GetLastWriteTimeUtc(fixture.Root);
        var indexRootExistedBefore = Directory.Exists(fixture.IndexRoot);

        var service = CreateService(
            engine,
            factory,
            // ⚠️ 刻意把 Scopes 写成**存在的目录**：如果门控被拆掉，这些 scope 会立刻被提交。
            new FullTextIndexSupplyOptions { Scopes = [corpus] },
            logger);

        var stopwatch = Stopwatch.StartNew();
        await service.StartAsync(CancellationToken.None);
        stopwatch.Stop();

        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(1),
            $"StartAsync 必须立即返回（默认关闭），实测 {stopwatch.ElapsedMilliseconds} ms");

        // 暴露窗口：门控若被拆掉（M2），这段等待足以让工厂/协调器计数变红。
        await Task.Delay(300);

        Assert.Equal(0, factory.CreateCalls);
        Assert.Empty(factory.CreatedWith);
        Assert.Equal(0, coordinator.BuildCalls);
        Assert.Equal(0, coordinator.GetStatusCalls);
        Assert.Equal(0, coordinator.PlanCalls);
        Assert.Equal(0, engine.HasIndexCalls);
        Assert.Equal(0, engine.ResolveCalls);
        Assert.Equal(0, engine.BuildCalls);

        Assert.False(
            Directory.Exists(fixture.IndexRoot),
            "默认关闭 ⇒ 索引根必须零写入（连目录都不许建）");
        Assert.Equal(rootLastWriteBefore, Directory.GetLastWriteTimeUtc(fixture.Root));

        // 完成标准 #4 后半：默认关闭 ⇒ 索引根零写入的**前后对照**（写进原始输出，便于人工复核）。
        _output.WriteLine($"[DEMO/A1] temp root               : {fixture.Root}");
        _output.WriteLine($"[DEMO/A1] index root              : {fixture.IndexRoot}");
        _output.WriteLine(
            $"[DEMO/A1] index root exists      : before={indexRootExistedBefore} after={Directory.Exists(fixture.IndexRoot)}");
        _output.WriteLine(
            $"[DEMO/A1] temp root mtime (UTC)  : before={rootLastWriteBefore:o} after={Directory.GetLastWriteTimeUtc(fixture.Root):o} "
            + $"equal={rootLastWriteBefore == Directory.GetLastWriteTimeUtc(fixture.Root)}");
        _output.WriteLine(
            $"[DEMO/A1] entries under temp root: {string.Join(", ", Directory.GetFileSystemEntries(fixture.Root).Select(Path.GetFileName))}");

        Assert.DoesNotContain(logger.Entries, static e => e.StartsWith("Error:", StringComparison.Ordinal));
    }

    // ── A2 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// A2（核心）：启用 + 合法 scope ⇒ 真实协调器被调用、**live 引擎的 <c>BuildIndexAsync</c> 一次都没被调用**
    /// （写路径只剩协调器 → staged builder），且真实 Lucene 在 <c>%TEMP%</c> 索引根建成的索引**可被查询命中**。
    /// </summary>
    [Fact]
    public async Task A2_Enabled_Supplies_Through_The_Coordinator_And_The_Live_Engine_Is_Never_Written_Directly()
    {
        using var fixture = new S5Fixture();
        var corpus = fixture.NewCorpus(
            "a2",
            ("alpha.txt", "class Alpha { public const string Token = \"berryneedle\"; } // filler ".PadRight(200, 'x')));

        using var lucene = new LuceneSearchEngine(fixture.Options);
        var engine = new SpyRootedEngine(lucene);
        var logger = new SupplyRecordingLogger();

        var supplyOptions = ProductionSupplyOptions(corpus, FullTextIndexSupplyOptions.DefaultMaxIndexBytes, TimeSpan.Zero);
        var production = CreateProductionFactory(fixture, engine).Create(supplyOptions);
        var (service, recorded) = CreateServiceOverProductionComposition(
            engine, production, supplyOptions, logger);

        await service.StartAsync(CancellationToken.None);
        var terminal = await recorded.FirstTerminal.Task.WaitAsync(TimeSpan.FromSeconds(120));

        Assert.True(
            terminal.State == SupplyJobState.Succeeded,
            $"供给 job 必须成功；实际 {terminal.State}：{terminal.Message}");
        Assert.Contains("outcome=Swapped", terminal.Message, StringComparison.Ordinal);

        // ① 确实走了协调器，且**每个 scope 只提交一次**
        S5TestHelpers.AssertPathsEqual([corpus], recorded.SubmittedRootPaths);
        Assert.True(recorded.GetStatusCalls >= 1, "必须轮询 job 状态到终态");

        // ② ★ 核心：live 引擎从未被直写（直写会绕过租约/预算/暂存/原子切换）
        Assert.Equal(0, engine.BuildCalls);
        Assert.Empty(engine.BuiltScopes);

        // ③ 真实索引在临时索引根就位，且可被查询命中
        var liveIndexDirectory = engine.ResolveIndexDirectory(corpus);
        Assert.True(Directory.Exists(liveIndexDirectory), $"live 索引目录必须存在：{liveIndexDirectory}");
        Assert.Equal(
            Path.TrimEndingDirectorySeparator(fixture.IndexRoot),
            Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(liveIndexDirectory)!));
        Assert.Equal(64, Path.GetFileName(liveIndexDirectory).Length);

        var search = await engine.SearchAsync("berryneedle", corpus);
        Assert.True(search.Success, search.Error);
        Assert.NotEmpty(search.Matches);

        // 完成标准 #4 前半：真实 Lucene 在临时索引根建成的索引**可被查询命中**（原始输出里的端到端证据）。
        _output.WriteLine($"[DEMO/A2] scope                    : {corpus}");
        _output.WriteLine($"[DEMO/A2] index root               : {fixture.IndexRoot}");
        _output.WriteLine(
            $"[DEMO/A2] index root entries      : {string.Join(", ", Directory.GetFileSystemEntries(fixture.IndexRoot).Select(Path.GetFileName))}");
        _output.WriteLine(
            $"[DEMO/A2] live index dir           : {Path.GetFileName(liveIndexDirectory)} "
            + $"({S5TestHelpers.SnapshotTree(liveIndexDirectory).Count} entries)");
        _output.WriteLine($"[DEMO/A2] terminal job message      : {terminal.Message}");
        _output.WriteLine($"[DEMO/A2] live engine BuildIndexAsync calls (must be 0): {engine.BuildCalls}");
        _output.WriteLine(
            $"[DEMO/A2] query \"berryneedle\"       : success={search.Success} matches={search.Matches.Count}");

        // ④ 暂存供给不留残留
        AssertNoEntries(Path.Combine(fixture.IndexRoot, ".staging"), ".staging");
    }

    // ── A3 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// A3：预算不足（1 字节）⇒ 组件侧 <c>OverBudget</c> 被**如实记录**（宿主日志里原因原文照登），
    /// live 目录**逐字节不变**、旧内容仍可查询命中、新内容不可见。
    /// </summary>
    [Fact]
    public async Task A3_OverBudget_Is_Recorded_Truthfully_And_The_Old_Index_Stays_Queryable()
    {
        using var fixture = new S5Fixture();
        var corpus = fixture.NewCorpus(
            "a3",
            ("alpha.txt", "class Alpha { public const string Token = \"berryneedle\"; } // filler ".PadRight(200, 'x')));

        using var lucene = new LuceneSearchEngine(fixture.Options);
        var engine = new SpyRootedEngine(lucene);

        // ① 预算充足 ⇒ 建成并原子切换到 live
        var generousOptions = ProductionSupplyOptions(corpus, FullTextIndexSupplyOptions.DefaultMaxIndexBytes, TimeSpan.Zero);
        var generous = CreateProductionFactory(fixture, engine).Create(generousOptions);
        var (generousService, generousRecorded) = CreateServiceOverProductionComposition(
            engine, generous, generousOptions, new SupplyRecordingLogger());

        await generousService.StartAsync(CancellationToken.None);
        var built = await generousRecorded.FirstTerminal.Task.WaitAsync(TimeSpan.FromSeconds(120));
        Assert.True(built.State == SupplyJobState.Succeeded, $"前置：首次供给必须成功；实际 {built.State}：{built.Message}");

        var liveIndexDirectory = engine.ResolveIndexDirectory(corpus);
        var snapshotBefore = S5TestHelpers.SnapshotTree(liveIndexDirectory);
        Assert.NotEmpty(snapshotBefore);

        // ② 极小预算 + 间隔为零（必须真的再试一次）⇒ 必被 OverBudget 拒绝
        var crampedLogger = new SupplyRecordingLogger();
        var crampedOptions = ProductionSupplyOptions(corpus, maxIndexBytes: 1, minRebuildInterval: TimeSpan.Zero);
        var cramped = CreateProductionFactory(fixture, engine).Create(crampedOptions);
        var (crampedService, crampedRecorded) = CreateServiceOverProductionComposition(
            engine, cramped, crampedOptions, crampedLogger);

        await crampedService.StartAsync(CancellationToken.None);
        var rejected = await crampedRecorded.FirstTerminal.Task.WaitAsync(TimeSpan.FromSeconds(120));

        Assert.True(rejected.State == SupplyJobState.Failed, $"预算不足必须失败；实际 {rejected.State}：{rejected.Message}");
        Assert.Contains("OverBudget", rejected.Message, StringComparison.Ordinal);
        Assert.Contains("outcome=RejectedOverBudget", rejected.Message, StringComparison.Ordinal);

        // ③ 宿主**如实记录**（jobId + 原因原文），不是一句含糊的 failed
        var failureLine = Assert.Single(crampedLogger.Entries, static e => e.Contains("Supply job", StringComparison.Ordinal)
                                                                          && e.Contains("failed", StringComparison.Ordinal));
        Assert.Contains(rejected.JobId, failureLine, StringComparison.Ordinal);
        Assert.Contains("OverBudget", failureLine, StringComparison.Ordinal);

        // ④ live 逐字节不变 + 旧索引仍可查询命中 + 新内容不可见
        Assert.Equal(snapshotBefore, S5TestHelpers.SnapshotTree(liveIndexDirectory));

        var stillQueryable = await engine.SearchAsync("berryneedle", corpus);
        Assert.True(stillQueryable.Success, stillQueryable.Error);
        Assert.NotEmpty(stillQueryable.Matches);

        var notIndexed = await engine.SearchAsync("cinnamontoken", corpus);
        Assert.Empty(notIndexed.Matches);
    }

    // ── A4 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// A4：一个 scope 被**别的 owner** 用真实跨进程租约占住 ⇒ 组件返回 <c>Busy</c>；
    /// 宿主只提交一次、如实记录（含 owner/PID + 不重试）、然后继续下一个 scope 并成功。
    /// </summary>
    [Fact]
    public async Task A4_Busy_Is_Submitted_Once_Recorded_And_The_Next_Scope_Still_Runs()
    {
        using var fixture = new S5Fixture();
        var busyCorpus = fixture.NewCorpus("a4-busy", ("a.txt", "aaa"));
        var freeCorpus = fixture.NewCorpus("a4-free", ("b.txt", "bbb"));

        var componentOptions = new SupplyCoordinatorOptions
        {
            DefaultBudgetBytes = FullTextIndexSupplyOptions.DefaultMaxIndexBytes,
            MinRebuildInterval = TimeSpan.Zero,
        };

        var builder = new CallCountingBuilder();
        var realCoordinator = new FullTextIndexSupplyCoordinator(
            new CallCountingInventory(),
            builder,
            new FileSupplyLease(fixture.Options),
            componentOptions);

        // 用**公开 API** 拿到 scope 键（宿主测试工程看不到组件的 internal 规范化函数）。
        var plan = await realCoordinator.PlanAsync(new SupplyScopeRequest(busyCorpus));
        var busyScopeKey = Assert.Single(plan.AcceptedScopes).ScopeKey;

        // 另一个 owner 先占住租约 ⇒ 真实 Busy（不构建、不写索引）。
        var foreignLease = new FileSupplyLease(fixture.Options);
        var acquired = await foreignLease.TryAcquireAsync(
            busyScopeKey,
            new SupplyLeaseOwner("otherhost#4242", 4242, "OTHERHOST"));
        Assert.True(acquired.Acquired, acquired.Message);

        var recorded = new RecordingSupplyCoordinator(realCoordinator);
        var composition = new TestSupplyComposition(recorded, componentOptions, _ => DateTimeOffset.MinValue);
        var logger = new SupplyRecordingLogger();

        var supplyOptions = new FullTextIndexSupplyOptions
        {
            Enabled = true,
            Scopes = [busyCorpus, freeCorpus],
            MaxIndexBytes = FullTextIndexSupplyOptions.DefaultMaxIndexBytes,
            MinRebuildInterval = TimeSpan.Zero,
        };

        var service = CreateService(
            new CountingRootedEngine(fixture.IndexRoot),
            new StubCompositionFactory(composition),
            supplyOptions,
            logger);

        await service.StartAsync(CancellationToken.None);

        Assert.True(
            await S5TestHelpers.WaitUntilAsync(() => recorded.TerminalStates.Count >= 1, TimeSpan.FromSeconds(60)),
            "被占住的那个 scope 之后的 scope 必须仍被提交并跑完");

        // ① 两个 scope 各提交一次（Busy 不重试）
        S5TestHelpers.AssertPathsEqual([busyCorpus, freeCorpus], recorded.SubmittedRootPaths);
        Assert.Equal(2, recorded.BuildRequests.Count);

        // ② 只有没被占住的那个真的构建了
        Assert.Equal(1, builder.BuildCalls);

        // ③ Busy 如实记录：一次、带 owner/PID、且说明不重试
        var busyLine = Assert.Single(logger.Entries, static e => e.Contains("Supply busy", StringComparison.Ordinal));
        Assert.Contains("otherhost#4242", busyLine, StringComparison.Ordinal);
        Assert.Contains("PID 4242", busyLine, StringComparison.Ordinal);
        Assert.Contains("no job created", busyLine, StringComparison.Ordinal);
        Assert.Contains("not retried", busyLine, StringComparison.Ordinal);
    }

    // ── A5 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// A5：三个 scope —— A 新鲜（自己的索引目录 mtime = 现在）/ B 陈旧（mtime = 3 天前）/
    /// C 根本没有索引目录 ⇒ **只提交 B 与 C**（A 新鲜只跳 A，不影响 B）。
    /// </summary>
    [Fact]
    public async Task A5_Per_Scope_Freshness_Only_Submits_The_Stale_And_The_Missing_Scope()
    {
        using var fixture = new S5Fixture();
        var freshCorpus = fixture.NewCorpus("a5-fresh", ("a.txt", "aaa"));
        var staleCorpus = fixture.NewCorpus("a5-stale", ("b.txt", "bbb"));
        var missingCorpus = fixture.NewCorpus("a5-missing", ("c.txt", "ccc"));

        var engine = new CountingRootedEngine(fixture.IndexRoot);

        // A 新鲜、B 陈旧（只改 B 自己的 mtime —— **不动**索引根 mtime）
        S5Fixture.CreateIndexDirectory(fixture.IndexRoot, Path.GetFileName(freshCorpus), DateTime.UtcNow);
        S5Fixture.CreateIndexDirectory(fixture.IndexRoot, Path.GetFileName(staleCorpus), DateTime.UtcNow.AddDays(-3));

        var supplyOptions = new FullTextIndexSupplyOptions
        {
            Enabled = true,
            Scopes = [freshCorpus, staleCorpus, missingCorpus],
            MaxIndexBytes = FullTextIndexSupplyOptions.DefaultMaxIndexBytes,
            MinRebuildInterval = TimeSpan.FromHours(12),
        };

        // 新鲜度探针**委托给生产实现**（只把协调器换成脚本替身），因此 M3 变异能真正影响本用例。
        var production = CreateProductionFactory(fixture, engine).Create(supplyOptions);
        var scripted = new ScriptedSupplyCoordinator(
            statusBehaviour: static (jobId, _) => ScriptedSupplyCoordinator.Succeeded(jobId));
        var recorded = new RecordingSupplyCoordinator(scripted);
        var composition = new TestSupplyComposition(
            recorded,
            production.ComponentOptions,
            production.LiveIndexLastWriteUtc);
        var logger = new SupplyRecordingLogger();

        var service = CreateService(engine, new StubCompositionFactory(composition), supplyOptions, logger);

        await service.StartAsync(CancellationToken.None);

        Assert.True(
            await S5TestHelpers.WaitUntilAsync(() => recorded.TerminalStates.Count >= 2, TimeSpan.FromSeconds(60)),
            "陈旧的与缺失的 scope 都必须被提交并跑完；实际提交了："
            + $"[{string.Join(", ", recorded.SubmittedRootPaths)}]");

        // 收尾窗口：若新鲜的 scope 也被提交，会在这段时间内被观测到。
        await Task.Delay(150);

        S5TestHelpers.AssertPathsEqual([staleCorpus, missingCorpus], recorded.SubmittedRootPaths);

        // A 必须被跳过（且只跳 A 一个）
        var freshLine = Assert.Single(logger.Entries, static e => e.Contains("is fresh", StringComparison.Ordinal));
        Assert.Contains("a5-fresh", freshLine, StringComparison.Ordinal);
        Assert.DoesNotContain("a5-stale", freshLine, StringComparison.Ordinal);

        // per-scope 口径的机械证据：探针按 scope 逐个问过（3 次），而不是问了一次根
        Assert.Equal(3, engine.HasIndexCalls);
        S5TestHelpers.AssertPathsEqual(
            [freshCorpus, staleCorpus, missingCorpus],
            engine.ResolvedScopes.ToArray());

        // 仍然没有直写
        Assert.Equal(0, engine.BuildCalls);
    }

    // ── A6 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// A6：配置里的 <c>MaxIndexBytes</c> / <c>MinRebuildInterval</c> **显式流入组件** ——
    /// 既反映在组合策略快照上，也反映在真实协调器**实际生效**的预算上（<c>PlanAsync.BudgetBytes</c>）。
    /// </summary>
    [Fact]
    public async Task A6_Configured_Budget_And_Interval_Reach_The_Component()
    {
        using var fixture = new S5Fixture();
        var corpus = fixture.NewCorpus("a6", ("a.txt", "aaa"));

        // 刻意选一个与宿主默认值、组件默认常量都不同的值：否则「配置流入」不可证。
        const long configuredBudget = 3_221_225_472L; // 3 GiB
        var configuredInterval = TimeSpan.FromMinutes(42);

        var supplyOptions = new FullTextIndexSupplyOptions
        {
            Enabled = true,
            Scopes = [corpus],
            MaxIndexBytes = configuredBudget,
            MinRebuildInterval = configuredInterval,
        };

        var factory = CreateProductionFactory(fixture, new CountingRootedEngine(fixture.IndexRoot));
        var composition = factory.Create(supplyOptions);

        Assert.Equal(configuredBudget, composition.ComponentOptions.DefaultBudgetBytes);
        Assert.Equal(configuredInterval, composition.ComponentOptions.MinRebuildInterval);
        Assert.NotEqual(SupplyCoordinatorOptions.DefaultMaxIndexBytes, composition.ComponentOptions.DefaultBudgetBytes);
        Assert.NotEqual(FullTextIndexSupplyOptions.DefaultMaxIndexBytes, composition.ComponentOptions.DefaultBudgetBytes);
        Assert.True(composition.ComponentOptions.UseStaging, "默认必须是暂存（安全）路径");

        // 组件侧**实际生效**的预算：PlanAsync 的 BudgetBytes = 请求值 ?? 组件默认值 ⇒ 必须等于配置值。
        var plan = await composition.Coordinator.PlanAsync(new SupplyScopeRequest(corpus));
        Assert.True(plan.Accepted, "前置：scope 必须被接受");
        Assert.Equal(configuredBudget, plan.BudgetBytes);
        Assert.Equal(configuredBudget, Assert.Single(plan.AcceptedScopes).BudgetBytes);
    }

    // ── 附加：轮询超时必须有界 ──────────────────────────────────────────────

    /// <summary>
    /// R1「必须有超时上限」：job 永不终态 ⇒ 宿主在有界超时后**如实记录**（带 jobId 与最后状态）、
    /// **不重试**该 scope，并继续处理下一个 scope（每个 scope 仍只提交一次）。
    /// </summary>
    [Fact]
    public async Task Poll_Timeout_Is_Bounded_Recorded_And_The_Next_Scope_Still_Runs()
    {
        using var fixture = new S5Fixture();
        var firstCorpus = fixture.NewCorpus("t-first", ("a.txt", "aaa"));
        var secondCorpus = fixture.NewCorpus("t-second", ("b.txt", "bbb"));

        var engine = new CountingRootedEngine(fixture.IndexRoot);
        var coordinator = new ScriptedSupplyCoordinator(
            buildBehaviour: static (request, call) => ScriptedSupplyCoordinator.Started(request.RootPaths[0], $"job-{call}"),
            statusBehaviour: static (jobId, _) => ScriptedSupplyCoordinator.Running(jobId));

        var composition = new TestSupplyComposition(
            coordinator,
            new SupplyCoordinatorOptions(),
            _ => DateTimeOffset.MinValue);
        var logger = new SupplyRecordingLogger();

        var supplyOptions = new FullTextIndexSupplyOptions
        {
            Enabled = true,
            Scopes = [firstCorpus, secondCorpus],
            MaxIndexBytes = FullTextIndexSupplyOptions.DefaultMaxIndexBytes,
            MinRebuildInterval = TimeSpan.Zero,
        };

        var service = CreateService(
            engine,
            new StubCompositionFactory(composition),
            supplyOptions,
            logger,
            TimeSpan.FromMilliseconds(200));

        await service.StartAsync(CancellationToken.None);

        Assert.True(
            await S5TestHelpers.WaitUntilAsync(
                () => logger.Entries.Any(static e => e.Contains("Timed out after", StringComparison.Ordinal)
                                                      && e.Contains("job-2", StringComparison.Ordinal)),
                TimeSpan.FromSeconds(30)),
            "第二个 scope 也必须被提交并超时（超时不得中断循环）");

        // 每个 scope 恰好提交一次（超时不算失败重试）
        Assert.Equal(2, coordinator.BuildCalls);
        Assert.Equal(2, coordinator.SubmittedRootPaths.Distinct().Count());

        var firstTimeout = Assert.Single(logger.Entries, static e => e.Contains("Timed out after", StringComparison.Ordinal)
                                                                     && e.Contains("job-1", StringComparison.Ordinal));
        Assert.Contains("last state Running", firstTimeout, StringComparison.Ordinal);
        Assert.Contains("not retried", firstTimeout, StringComparison.Ordinal);

        Assert.True(
            coordinator.GetStatusCalls >= 4,
            $"必须真的轮询了多次才超时（实际 {coordinator.GetStatusCalls} 次）");
        Assert.Equal(0, engine.BuildCalls);
    }

    // ── 局部工具 ──────────────────────────────────────────────────────────

    private static FullTextIndexSupplyOptions ProductionSupplyOptions(
        string scope,
        long maxIndexBytes,
        TimeSpan minRebuildInterval) => new()
        {
            Enabled = true,
            Scopes = [scope],
            MaxIndexBytes = maxIndexBytes,
            MinRebuildInterval = minRebuildInterval,
        };

    private static IFullTextIndexSupplyCompositionFactory CreateProductionFactory(
        S5Fixture fixture,
        IFullTextIndexRootedEngine liveEngine) =>
        new LuceneFullTextIndexSupplyCompositionFactory(
            fixture.Options,
            liveEngine,
            stagingOptions => new LuceneSearchEngine(stagingOptions));

    /// <summary>
    /// 装配被测服务：协调器用记录装饰器包住**生产组合**，新鲜度探针**委托生产实现**，
    /// 于是断言既覆盖宿主行为（提交/轮询/日志），又覆盖生产的 per-scope 口径。
    /// </summary>
    private static (IndexPrebuildService Service, RecordingSupplyCoordinator Recorded) CreateServiceOverProductionComposition(
        IFullTextIndexRootedEngine engine,
        IFullTextIndexSupplyComposition production,
        FullTextIndexSupplyOptions supplyOptions,
        SupplyRecordingLogger logger)
    {
        var recorded = new RecordingSupplyCoordinator(production.Coordinator);
        var composition = new TestSupplyComposition(
            recorded,
            production.ComponentOptions,
            production.LiveIndexLastWriteUtc);

        return (CreateService(engine, new StubCompositionFactory(composition), supplyOptions, logger), recorded);
    }

    private static IndexPrebuildService CreateService(
        IFullTextSearchEngine engine,
        IFullTextIndexSupplyCompositionFactory factory,
        FullTextIndexSupplyOptions supplyOptions,
        ILogger<IndexPrebuildService> logger,
        TimeSpan? buildWaitTimeout = null) =>
        new(engine, Options.Create(supplyOptions), factory, logger)
        {
            StartupDelay = TimeSpan.Zero,
            StatusPollInterval = TimeSpan.FromMilliseconds(20),
            BuildWaitTimeout = buildWaitTimeout ?? TimeSpan.FromSeconds(60),
        };

    private static void AssertNoEntries(string directory, string label)
    {
        if (!Directory.Exists(directory))
            return;

        Assert.True(
            Directory.GetFileSystemEntries(directory).Length == 0,
            $"{label} 不得有残留（暂存供给成功后会清空）");
    }
}
