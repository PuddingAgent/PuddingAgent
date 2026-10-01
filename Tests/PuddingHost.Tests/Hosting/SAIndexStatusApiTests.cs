using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PuddingAgent.Services;
using PuddingFullTextIndex;
using PuddingFullTextIndex.Contracts;
using PuddingFullTextIndex.Infrastructure.Search;
using PuddingHost.Controllers;
using PuddingHost.Hosting;

namespace PuddingHost.Tests.Hosting;

/// <summary>
/// Slice S-A（2026-10-01）：全文索引「状态只读出口」+ 供给组合升格为宿主共享惰性单例 ——
/// 断言矩阵 **I1~I9**。
/// <para>
/// 全部夹具落在 <see cref="Path.GetTempPath"/>（<see cref="S5Fixture"/> 自带该断言），
/// 引擎 / 协调器一律用替身：既能**计数**「到底有没有构造组合 / 有没有提交供给」，
/// 又保证**零真实索引 I/O**（生产索引根 <c>D:\Data\fulltext-index</c> 不在本文件任何断言的作用域内）。
/// </para>
/// <para>
/// 「能失败」的意思：每条断言都锚在一个**可被单行变异打破**的事实上 ——
/// I2 锚「同一引用 + Create 只一次」（M1 拆缓存即红），
/// I1 锚「Create 调用数 == 0」（M2 让探针改用 GetOrCreate 即红）。
/// </para>
/// </summary>
public sealed class SAIndexStatusApiTests
{
    // ── I1：Enabled=false ⇒ 不构造组合、零副作用 ──────────────────────────

    /// <summary>
    /// I1：<c>Enabled=false</c> ⇒ 查询后 <c>compositionCreated==false</c>、
    /// 假工厂 <c>Create</c> **调用数 == 0**、索引根目录**未被创建**。
    /// </summary>
    [Fact]
    public async Task I1_Disabled_Configuration_Neither_Creates_The_Composition_Nor_The_Index_Root()
    {
        using var fixture = new S5Fixture();
        var scope = fixture.NewCorpus("i1-disabled", ("a.txt", "aaa"));

        var engine = new CountingRootedEngine(fixture.IndexRoot);
        var factory = new StubCompositionFactory(FixedComposition(new LedgerSupplyCoordinator([])));
        var accessor = new FullTextIndexSupplyAccessor(factory);

        // Enabled 默认 false（即使 Scopes 写了目录：resolver 的 Disabled 是空动作）。
        var probe = CreateProbe(
            new FullTextIndexSupplyOptions { Scopes = [scope] },
            fixture.Options,
            engine,
            accessor);

        var snapshot = await probe.BuildAsync();

        Assert.False(snapshot.FullText.Enabled);
        Assert.False(snapshot.FullText.CompositionCreated);
        Assert.Null(accessor.Current);
        Assert.Equal(0, factory.CreateCalls);
        Assert.False(
            Directory.Exists(fixture.IndexRoot),
            "默认关闭时，状态查询**不得**创建索引根目录（R2 零副作用）");
        Assert.Empty(snapshot.FullText.AcceptedScopes);
        Assert.Empty(snapshot.FullText.Scopes);
    }

    /// <summary>I1（另一半口径）：<c>Enabled=true</c> 但配置被拒（<c>Scopes</c> 为空）⇒ 同样不构造组合。</summary>
    [Fact]
    public async Task I1b_Rejected_Configuration_Neither_Creates_The_Composition_Nor_The_Index_Root()
    {
        using var fixture = new S5Fixture();
        var engine = new CountingRootedEngine(fixture.IndexRoot);
        var factory = new StubCompositionFactory(FixedComposition(new LedgerSupplyCoordinator([])));
        var accessor = new FullTextIndexSupplyAccessor(factory);

        var probe = CreateProbe(
            new FullTextIndexSupplyOptions { Enabled = true, Scopes = [] },
            fixture.Options,
            engine,
            accessor);

        var snapshot = await probe.BuildAsync();

        Assert.True(snapshot.FullText.Enabled);
        Assert.False(snapshot.FullText.CompositionCreated);
        Assert.Equal(0, factory.CreateCalls);
        Assert.False(Directory.Exists(fixture.IndexRoot));
        Assert.True(FullTextIndexSupplyResolver.Resolve(new FullTextIndexSupplyOptions { Enabled = true, Scopes = [] })
                        is { Succeeded: false }, "口径前提：Enabled=true 且 Scopes 为空必须被 resolver 拒绝");
    }

    // ── I2：共享实例 + 服务与查询共用同一个 accessor ──────────────────────

    /// <summary>
    /// I2：<c>GetOrCreate</c> 两次返回**同一引用**且只 <c>Create</c> 一次；
    /// <see cref="IndexPrebuildService"/> 走同一 accessor（它写进去的实例，状态查询看得见）。
    /// </summary>
    [Fact]
    public async Task I2_The_Accessor_Is_Lazy_Shared_And_Used_By_Both_The_Service_And_The_Probe()
    {
        using var fixture = new S5Fixture();
        var scope = fixture.NewCorpus("i2-shared", ("a.txt", "aaa"));

        var engine = new CountingRootedEngine(fixture.IndexRoot);
        var composition = FixedComposition(new LedgerSupplyCoordinator([]));
        var factory = new StubCompositionFactory(composition);
        var accessor = new FullTextIndexSupplyAccessor(factory);
        var supplyOptions = new FullTextIndexSupplyOptions
        {
            Enabled = true,
            Scopes = [scope],
            MinRebuildInterval = TimeSpan.Zero,
        };

        // ① 同一实例：两次调用同一引用，且只构造一次（M1 拆缓存即在此取红）。
        Assert.Same(accessor.GetOrCreate(supplyOptions), accessor.GetOrCreate(supplyOptions));
        Assert.Same(composition, accessor.Current);
        Assert.Equal(1, factory.CreateCalls);

        // ② 预建服务与状态查询共用**同一个** accessor 实例。
        var sharedFactory = new StubCompositionFactory(composition);
        var sharedAccessor = new FullTextIndexSupplyAccessor(sharedFactory);
        var service = new IndexPrebuildService(
            engine,
            Options.Create(supplyOptions),
            sharedAccessor,
            NullLogger<IndexPrebuildService>.Instance)
        {
            StartupDelay = TimeSpan.Zero,
            StatusPollInterval = TimeSpan.FromMilliseconds(20),
            BuildWaitTimeout = TimeSpan.FromSeconds(10),
        };

        // 惰性：装配服务本身绝不可构造组合（R4：门控早返回在组合构造之前）。
        Assert.Null(sharedAccessor.Current);
        Assert.Equal(0, sharedFactory.CreateCalls);

        await service.StartAsync(CancellationToken.None);
        Assert.True(
            await S5TestHelpers.WaitUntilAsync(() => sharedAccessor.Current is not null, TimeSpan.FromSeconds(10)),
            "开启且校验通过后，预建服务必须把组合写进**共享**访问器");
        Assert.Same(composition, sharedAccessor.Current);
        Assert.Equal(1, sharedFactory.CreateCalls);

        // ③ 状态查询（同一个 accessor）看到的就是服务写入的那个实例。
        var probe = CreateProbe(supplyOptions, fixture.Options, engine, sharedAccessor);
        var snapshot = await probe.BuildAsync();

        Assert.True(snapshot.FullText.CompositionCreated);
        Assert.Same(composition, sharedAccessor.Current);
        Assert.Same(sharedAccessor.Current, sharedAccessor.GetOrCreate(supplyOptions));
        Assert.Equal(1, sharedFactory.CreateCalls);
    }

    // ── I3 / I4：台账可见 vs 如实为空 ────────────────────────────────────

    /// <summary>
    /// I3：预置一个已提交 job 的假协调器 ⇒ <c>jobs</c> 含该 job，字段与
    /// <c>ListStatusAsync</c> 的返回**逐字一致**；<c>jobsReason==null</c>。
    /// </summary>
    [Fact]
    public async Task I3_Jobs_Are_Reported_Verbatim_From_The_Ledger_And_JobsReason_Is_Null()
    {
        using var fixture = new S5Fixture();
        var scope = fixture.NewCorpus("i3-ledger", ("a.txt", "aaa"));

        var startedAt = new DateTimeOffset(2026, 10, 1, 3, 30, 0, TimeSpan.Zero);
        var finishedAt = startedAt.AddSeconds(71);
        var job = new SupplyJobStatus(
            JobId: "job-42",
            ScopeKey: "scope-key-42",
            RootPath: scope,
            State: SupplyJobState.Succeeded,
            Phase: SupplyJobPhases.Completed,
            DiscoveredFileCount: 12,
            DiscoveredBytes: 106_029_333,
            StartedAt: startedAt,
            FinishedAt: finishedAt,
            Message: "done: swapped in place",
            LeaseHolder: null);

        var coordinator = new LedgerSupplyCoordinator([job]);
        var engine = new CountingRootedEngine(fixture.IndexRoot);
        var accessor = new FullTextIndexSupplyAccessor(
            new StubCompositionFactory(FixedComposition(coordinator)));

        var supplyOptions = new FullTextIndexSupplyOptions { Enabled = true, Scopes = [scope] };

        // 模拟「预建已经跑过」：组合已构造（这是**前提**，不是查询动作）。
        accessor.GetOrCreate(supplyOptions);

        var probe = CreateProbe(supplyOptions, fixture.Options, engine, accessor);
        var snapshot = await probe.BuildAsync();

        var reported = Assert.Single(snapshot.FullText.Jobs);
        Assert.Equal(job.JobId, reported.JobId);
        Assert.Equal(job.State.ToString(), reported.State);
        Assert.Equal(job.Phase, reported.Phase);
        Assert.Equal(job.StartedAt.UtcDateTime, reported.StartedAt);
        Assert.Equal(job.FinishedAt!.Value.UtcDateTime, reported.FinishedAt);
        Assert.Equal(job.Message, reported.Message);
        Assert.Equal(job.DiscoveredFileCount, reported.IndexedFileCount);
        Assert.Equal(job.DiscoveredBytes, reported.TotalBytes);
        Assert.Equal(71_000L, reported.ElapsedMs!.Value);
        Assert.Null(snapshot.FullText.JobsReason);
        Assert.Equal(1, coordinator.ListStatusCalls);
        Assert.Equal(0, coordinator.BuildCalls);
        Assert.Equal(0, coordinator.PlanCalls);
        Assert.Equal(0, coordinator.CancelCalls);
    }

    /// <summary>
    /// I4：未构造组合 ⇒ <c>jobs</c> 为空数组**且** <c>jobsReason == "composition-not-created"**
    /// （如实说明「没有台账」，绝不伪造空台账）。
    /// </summary>
    [Fact]
    public async Task I4_Uncreated_Composition_Reports_Empty_Jobs_With_An_Honest_Reason()
    {
        using var fixture = new S5Fixture();
        var scope = fixture.NewCorpus("i4-uncreated", ("a.txt", "aaa"));

        var engine = new CountingRootedEngine(fixture.IndexRoot);
        var factory = new StubCompositionFactory(FixedComposition(new LedgerSupplyCoordinator([])));
        var accessor = new FullTextIndexSupplyAccessor(factory);
        var supplyOptions = new FullTextIndexSupplyOptions { Enabled = true, Scopes = [scope] };

        var probe = CreateProbe(supplyOptions, fixture.Options, engine, accessor);
        var snapshot = await probe.BuildAsync();

        Assert.False(snapshot.FullText.CompositionCreated);
        Assert.Empty(snapshot.FullText.Jobs);
        Assert.Equal("composition-not-created", snapshot.FullText.JobsReason);
        Assert.Equal(0, factory.CreateCalls);
        Assert.False(Directory.Exists(fixture.IndexRoot));
    }

    // ── I5：零副作用（索引根逐项不变） ───────────────────────────────────

    /// <summary>
    /// I5：一次状态查询前后，索引根的 <c>mtime</c> + 条目数 + <c>.supply-leases</c> 内容
    /// **逐项不变**（查询前先取快照）。
    /// </summary>
    [Fact]
    public async Task I5_A_Status_Query_Leaves_The_Index_Root_Byte_For_Byte_Unchanged()
    {
        using var fixture = new S5Fixture();
        var scope = fixture.NewCorpus("i5-readonly", ("a.txt", "aaa"));

        // 造一个「已存在的索引根」现场（索引目录 + 租约目录），再取快照。
        Directory.CreateDirectory(fixture.IndexRoot);
        var indexDirectory = S5Fixture.CreateIndexDirectory(
            fixture.IndexRoot, "f09e86e043ae76b0bc025ef00ed4dd5328fb947dded2a1c8f73db8e3181dbf78", DateTime.UtcNow);
        var leaseDirectory = Path.Combine(fixture.IndexRoot, ".supply-leases");
        Directory.CreateDirectory(leaseDirectory);
        File.WriteAllText(Path.Combine(leaseDirectory, "lease-1.json"), """{"ownerId":"someone"}""");

        var engine = new CountingRootedEngine(fixture.IndexRoot);
        var accessor = new FullTextIndexSupplyAccessor(
            new StubCompositionFactory(FixedComposition(new LedgerSupplyCoordinator([]))));
        var probe = CreateProbe(
            new FullTextIndexSupplyOptions { Enabled = true, Scopes = [scope] },
            fixture.Options,
            engine,
            accessor);

        var before = CaptureRootState(fixture.IndexRoot);
        await probe.BuildAsync();
        var after = CaptureRootState(fixture.IndexRoot);

        Assert.Equal(before.RootLastWriteUtc, after.RootLastWriteUtc);
        Assert.Equal(before.EntryCount, after.EntryCount);
        Assert.Equal(before.LeaseContent, after.LeaseContent);
        Assert.Equal(before.Tree, after.Tree);

        // 反向自证（活对照）：夹具本身确实「看得见变化」——同一份快照口径在一次真实写入后会变。
        File.WriteAllText(Path.Combine(leaseDirectory, "lease-2.json"), "{}");
        var mutated = CaptureRootState(fixture.IndexRoot);
        Assert.NotEqual(before.Tree, mutated.Tree);
        Assert.NotEqual(before.LeaseContent, mutated.LeaseContent);
    }

    /// <summary>
    /// I5（同口径，换**真实** Lucene 引擎）：状态查询对真实引擎也只读 ——
    /// 索引根仍逐项不变（生产接缝上不存在「查状态顺手写东西」）。
    /// </summary>
    [Fact]
    public async Task I5b_A_Status_Query_With_The_Real_Lucene_Engine_Also_Writes_Nothing()
    {
        using var fixture = new S5Fixture();
        var scope = fixture.NewCorpus("i5b-real-engine", ("a.txt", "aaa"));

        Directory.CreateDirectory(fixture.IndexRoot);
        File.WriteAllText(Path.Combine(fixture.IndexRoot, ".supply-leases-marker"), "marker");

        // 真实引擎 + 真实 rooted 接缝（生产 DI 里就是同一个实例）。
        var realEngine = new LuceneSearchEngine(fixture.Options);
        var accessor = new FullTextIndexSupplyAccessor(
            new StubCompositionFactory(FixedComposition(new LedgerSupplyCoordinator([]))));
        var probe = CreateProbe(
            new FullTextIndexSupplyOptions { Enabled = true, Scopes = [scope] },
            fixture.Options,
            realEngine,
            accessor);

        var before = CaptureRootState(fixture.IndexRoot);
        var snapshot = await probe.BuildAsync();
        var after = CaptureRootState(fixture.IndexRoot);

        // 真实引擎会算出真目录名（单一真源），此处只断言只读性。
        Assert.Single(snapshot.FullText.Scopes);
        Assert.Equal(before.RootLastWriteUtc, after.RootLastWriteUtc);
        Assert.Equal(before.EntryCount, after.EntryCount);
        Assert.Equal(before.Tree, after.Tree);
        Assert.Equal(before.LeaseContent, after.LeaseContent);
    }

    // ── I6：配置真值 + 换值即时可见 ─────────────────────────────────────

    /// <summary>
    /// I6：响应中的 <c>enabled/acceptedScopes/indexRoot/maxIndexBytes/minRebuildInterval</c>
    /// == 注入的 <see cref="IOptionsMonitor{T}"/> 的 <c>CurrentValue</c>；**换一个 monitor 值 ⇒ 响应随之变化**
    /// （证明不是缓存旧值）。
    /// </summary>
    [Fact]
    public async Task I6_Response_Reflects_The_Live_Monitor_Value_And_Changes_When_It_Changes()
    {
        using var fixture = new S5Fixture();
        var scopeA = fixture.NewCorpus("i6-a", ("a.txt", "aaa"));
        var scopeB = fixture.NewCorpus("i6-b", ("b.txt", "bbb"));

        var first = new FullTextIndexSupplyOptions
        {
            Enabled = true,
            Scopes = [scopeA],
            WorkspaceRoot = fixture.Root,
            MaxIndexBytes = 1_073_741_824,
            MinRebuildInterval = TimeSpan.FromHours(12),
        };
        var second = new FullTextIndexSupplyOptions
        {
            Enabled = true,
            Scopes = [scopeB],
            WorkspaceRoot = fixture.Root,
            MaxIndexBytes = 2_147_483_648,
            MinRebuildInterval = TimeSpan.FromMinutes(42),
        };

        var monitor = new StubOptionsMonitor<FullTextIndexSupplyOptions>(first);
        var probe = new FullTextIndexStatusProbe(
            BuildConfiguration(FullTextIndexSupplyOptions.SectionName),
            monitor,
            fixture.Options,
            new CountingRootedEngine(fixture.IndexRoot),
            new FullTextIndexSupplyAccessor(
                new StubCompositionFactory(FixedComposition(new LedgerSupplyCoordinator([])))),
            NullLogger<FullTextIndexStatusProbe>.Instance);

        var before = (await probe.BuildAsync()).FullText;

        Assert.True(before.Enabled);
        Assert.Equal(first.MaxIndexBytes, before.MaxIndexBytes);
        Assert.Equal(first.MinRebuildInterval, before.MinRebuildInterval);
        Assert.Equal(fixture.Options.IndexRootDirectory, before.IndexRoot);
        S5TestHelpers.AssertPathsEqual(first.Scopes, before.AcceptedScopes);

        // 换值（同一个探针实例、同一个进程）—— 响应必须跟着变。
        monitor.CurrentValue = second;
        var after = (await probe.BuildAsync()).FullText;

        Assert.Equal(second.MaxIndexBytes, after.MaxIndexBytes);
        Assert.Equal(second.MinRebuildInterval, after.MinRebuildInterval);
        S5TestHelpers.AssertPathsEqual(second.Scopes, after.AcceptedScopes);

        Assert.NotEqual(before.MaxIndexBytes, after.MaxIndexBytes);
        Assert.NotEqual(before.MinRebuildInterval, after.MinRebuildInterval);
        Assert.NotEqual(before.Scopes[0].ScopePath, after.Scopes[0].ScopePath);
    }

    // ── I7：路径单一真源 ────────────────────────────────────────────────

    /// <summary>
    /// I7：响应中的 <c>indexDirectory</c> == 假引擎 <c>ResolveIndexDirectory(scope)</c> 的返回值
    /// （**逐字节相同**，宿主不复刻命名哈希）。
    /// </summary>
    [Fact]
    public async Task I7_IndexDirectory_Comes_Verbatim_From_The_Engine_Mapping()
    {
        using var fixture = new S5Fixture();
        var scope = fixture.NewCorpus("i7-single-source", ("a.txt", "aaa"));

        var engine = new CountingRootedEngine(fixture.IndexRoot);
        var probe = CreateProbe(
            new FullTextIndexSupplyOptions { Enabled = true, Scopes = [scope] },
            fixture.Options,
            engine,
            new FullTextIndexSupplyAccessor(
                new StubCompositionFactory(FixedComposition(new LedgerSupplyCoordinator([])))));

        var snapshot = await probe.BuildAsync();
        var reported = Assert.Single(snapshot.FullText.Scopes);

        var engineSays = engine.ResolveIndexDirectory(scope);

        Assert.Equal(scope, reported.ScopePath);
        Assert.Equal(engineSays, reported.IndexDirectory);
        Assert.Contains(scope, engine.ResolvedScopes);
        Assert.Equal(engineSays, Path.Combine(fixture.IndexRoot, Path.GetFileName(scope)));
    }

    // ── I8：降级不崩 ───────────────────────────────────────────────────

    /// <summary>
    /// I8-a：<c>ResolveIndexDirectory</c> 抛异常 ⇒ 该 scope 条目**仍出现**，
    /// 目录类字段全为 <c>null</c>，且控制器响应仍是 **200**（不是 500、不是整块省略）。
    /// </summary>
    [Fact]
    public async Task I8a_Resolver_Throws_The_Scope_Entry_Survives_With_Nulls_And_Http_Is_200()
    {
        using var fixture = new S5Fixture();
        var scope = fixture.NewCorpus("i8a-throw", ("a.txt", "aaa"));

        var engine = new ScriptedRootedEngine(_ => throw new InvalidOperationException("resolve failed (I8a)"));
        var probe = CreateProbe(
            new FullTextIndexSupplyOptions { Enabled = true, Scopes = [scope] },
            fixture.Options,
            engine,
            new FullTextIndexSupplyAccessor(
                new StubCompositionFactory(FixedComposition(new LedgerSupplyCoordinator([])))));

        var snapshot = await probe.BuildAsync();
        var reported = Assert.Single(snapshot.FullText.Scopes);

        Assert.Equal(scope, reported.ScopePath);
        Assert.Null(reported.IndexDirectory);
        Assert.Null(reported.IndexDirectoryExists);
        Assert.Null(reported.IndexEntryCount);
        Assert.Null(reported.IndexBytes);
        Assert.Null(reported.IndexDirectoryLastWriteUtc);

        Assert.Equal(200, await InvokeControllerStatusAsync(probe));
    }

    /// <summary>
    /// I8-b：索引目录**读不出来**（该路径被一个文件占住）⇒ 同样全 <c>null</c>，HTTP 仍 200。
    /// </summary>
    [Fact]
    public async Task I8b_Unreadable_Index_Path_Degrades_To_Nulls_And_Http_Is_200()
    {
        using var fixture = new S5Fixture();
        var scope = fixture.NewCorpus("i8b-unreadable", ("a.txt", "aaa"));

        Directory.CreateDirectory(fixture.IndexRoot);
        var occupied = Path.Combine(fixture.IndexRoot, "occupied-by-a-file");
        File.WriteAllText(occupied, "not a directory");

        var engine = new ScriptedRootedEngine(_ => occupied);
        var probe = CreateProbe(
            new FullTextIndexSupplyOptions { Enabled = true, Scopes = [scope] },
            fixture.Options,
            engine,
            new FullTextIndexSupplyAccessor(
                new StubCompositionFactory(FixedComposition(new LedgerSupplyCoordinator([])))));

        var snapshot = await probe.BuildAsync();
        var reported = Assert.Single(snapshot.FullText.Scopes);

        Assert.Equal(scope, reported.ScopePath);
        Assert.Null(reported.IndexDirectory);
        Assert.Null(reported.IndexDirectoryExists);
        Assert.Null(reported.IndexEntryCount);
        Assert.Null(reported.IndexBytes);
        Assert.Null(reported.IndexDirectoryLastWriteUtc);

        Assert.Equal(200, await InvokeControllerStatusAsync(probe));
    }

    /// <summary>
    /// I8-c（同族边界）：解析得到、但目录**确实不存在** ⇒ 这是「读到了事实」：
    /// <c>indexDirectory</c> 有值、<c>indexDirectoryExists==false</c>，计数类如实为 <c>null</c>。
    /// </summary>
    [Fact]
    public async Task I8c_Resolved_But_Missing_Index_Directory_Reports_The_Path_And_Null_Counts()
    {
        using var fixture = new S5Fixture();
        var scope = fixture.NewCorpus("i8c-missing", ("a.txt", "aaa"));
        var missing = Path.Combine(fixture.IndexRoot, "never-created");

        var engine = new ScriptedRootedEngine(_ => missing);
        var probe = CreateProbe(
            new FullTextIndexSupplyOptions { Enabled = true, Scopes = [scope] },
            fixture.Options,
            engine,
            new FullTextIndexSupplyAccessor(
                new StubCompositionFactory(FixedComposition(new LedgerSupplyCoordinator([])))));

        var snapshot = await probe.BuildAsync();
        var reported = Assert.Single(snapshot.FullText.Scopes);

        Assert.Equal(missing, reported.IndexDirectory);
        Assert.False(reported.IndexDirectoryExists);
        Assert.Null(reported.IndexEntryCount);
        Assert.Null(reported.IndexBytes);
        Assert.Null(reported.IndexDirectoryLastWriteUtc);
        Assert.Equal(200, await InvokeControllerStatusAsync(probe));
    }

    /// <summary>I8（台账读取失败的降级）：端点仍 200，且如实报 <c>ledger-read-failed</c>（不是「空台账」）。</summary>
    [Fact]
    public async Task I8d_Ledger_Read_Failure_Degrades_To_A_Honest_Reason_And_Http_Is_200()
    {
        using var fixture = new S5Fixture();
        var scope = fixture.NewCorpus("i8d-ledger-failure", ("a.txt", "aaa"));

        var supplyOptions = new FullTextIndexSupplyOptions { Enabled = true, Scopes = [scope] };
        var accessor = new FullTextIndexSupplyAccessor(new StubCompositionFactory(
            FixedComposition(new LedgerSupplyCoordinator([], throwOnList: true))));
        accessor.GetOrCreate(supplyOptions);

        var probe = CreateProbe(supplyOptions, fixture.Options, new CountingRootedEngine(fixture.IndexRoot), accessor);

        var snapshot = await probe.BuildAsync();

        Assert.True(snapshot.FullText.CompositionCreated);
        Assert.Empty(snapshot.FullText.Jobs);
        Assert.Equal("ledger-read-failed", snapshot.FullText.JobsReason);
        Assert.Equal(200, await InvokeControllerStatusAsync(probe));
    }

    // ── I9：控制器可被 DI 构造（轻量组合根 smoke） ───────────────────────

    /// <summary>
    /// I9：照既有模式用 <c>ServiceCollection</c> + <c>ActivatorUtilities</c> ——
    /// 断言 DI 能造出 <see cref="IndexAdminController"/>，且依赖齐全（缺注册即失败）。
    /// </summary>
    [Fact]
    public void I9_Controller_Is_Constructible_By_Dependency_Injection()
    {
        using var fixture = new S5Fixture();
        var scope = fixture.NewCorpus("i9-di", ("a.txt", "aaa"));

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(BuildConfiguration(FullTextIndexSupplyOptions.SectionName));
        services.AddOptions();
        services.Configure<FullTextIndexSupplyOptions>(_ => { });
        services.AddSingleton(fixture.Options);
        services.AddSingleton<IFullTextSearchEngine>(new CountingRootedEngine(fixture.IndexRoot));
        services.AddSingleton<IFullTextIndexSupplyCompositionFactory>(
            new StubCompositionFactory(FixedComposition(new LedgerSupplyCoordinator([]))));
        services.AddSingleton<IFullTextIndexSupplyAccessor, FullTextIndexSupplyAccessor>();
        services.AddSingleton<FullTextIndexStatusProbe>();

        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });

        var controller = ActivatorUtilities.CreateInstance<IndexAdminController>(provider);

        Assert.NotNull(controller);
        Assert.IsType<IndexAdminController>(controller);
        Assert.NotNull(provider.GetRequiredService<FullTextIndexStatusProbe>());

        // 期望「多一层保护」：解析出的探针依赖齐全（不是靠 MVC 在首个请求时才炸）。
        Assert.NotNull(provider.GetRequiredService<IFullTextIndexSupplyAccessor>());
        Assert.False(string.IsNullOrWhiteSpace(scope));
    }

    // ── 公共夹具 ────────────────────────────────────────────────────────

    private static FullTextIndexStatusProbe CreateProbe(
        FullTextIndexSupplyOptions supplyOptions,
        FullTextIndexOptions indexOptions,
        IFullTextSearchEngine engine,
        IFullTextIndexSupplyAccessor accessor) =>
        new(
            BuildConfiguration(FullTextIndexSupplyOptions.SectionName),
            new StubOptionsMonitor<FullTextIndexSupplyOptions>(supplyOptions),
            indexOptions,
            engine,
            accessor,
            NullLogger<FullTextIndexStatusProbe>.Instance);

    private static IConfigurationRoot BuildConfiguration(params string[] presentSections)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var section in presentSections)
            values[$"{section}:Enabled"] = "true";

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static IFullTextIndexSupplyComposition FixedComposition(IFullTextIndexSupplyCoordinator coordinator) =>
        new TestSupplyComposition(coordinator, new SupplyCoordinatorOptions(), _ => DateTimeOffset.MinValue);

    /// <summary>走控制器动作（<c>OkObjectResult</c> = HTTP 200；异常会在此直接冒泡成测试失败）。</summary>
    private static async Task<int> InvokeControllerStatusAsync(FullTextIndexStatusProbe probe)
    {
        var controller = new IndexAdminController(probe);
        var result = await controller.GetStatus(CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.IsType<FullTextIndexStatusSnapshot>(ok.Value);
        return ok.StatusCode ?? 0;
    }

    /// <summary>索引根状态快照（mtime / 顶层条目数 / <c>.supply-leases</c> 内容 / 整棵树）。</summary>
    private static (DateTime RootLastWriteUtc, int EntryCount, string LeaseContent, IReadOnlyList<string> Tree)
        CaptureRootState(string indexRoot)
    {
        var leaseDirectory = Path.Combine(indexRoot, ".supply-leases");
        var leaseContent = Directory.Exists(leaseDirectory)
            ? string.Join(
                "\n",
                Directory.GetFiles(leaseDirectory)
                    .OrderBy(static f => f, StringComparer.Ordinal)
                    .Select(static f => $"{Path.GetFileName(f)}={File.ReadAllText(f)}"))
            : string.Empty;

        return (
            Directory.GetLastWriteTimeUtc(indexRoot),
            Directory.Exists(indexRoot) ? Directory.GetFileSystemEntries(indexRoot).Length : 0,
            leaseContent,
            S5TestHelpers.SnapshotTree(indexRoot));
    }

    // ── 测试替身 ────────────────────────────────────────────────────────

    /// <summary>可变 <see cref="IOptionsMonitor{T}"/> 替身：证明「响应跟着 monitor 值走」（I6）。</summary>
    private sealed class StubOptionsMonitor<T>(T currentValue) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; set; } = currentValue;

        public T Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    /// <summary>
    /// 台账替身：只暴露 <c>ListStatusAsync</c>；<c>BuildAsync</c> **直接抛**
    /// —— 于是「状态查询顺手触发一次供给」会被立刻抓住（比断言计数更硬）。
    /// </summary>
    private sealed class LedgerSupplyCoordinator(
        IReadOnlyList<SupplyJobStatus> jobs,
        bool throwOnList = false) : IFullTextIndexSupplyCoordinator
    {
        internal int ListStatusCalls;

        internal int BuildCalls;

        internal int PlanCalls;

        internal int CancelCalls;

        public Task<SupplyPlanResult> PlanAsync(SupplyScopeRequest request, CancellationToken ct = default)
        {
            Interlocked.Increment(ref PlanCalls);
            return Task.FromResult(new SupplyPlanResult(false, [], [], 0, 0));
        }

        public Task<SupplyRequestOutcome> BuildAsync(SupplyScopeRequest request, CancellationToken ct = default)
        {
            Interlocked.Increment(ref BuildCalls);
            throw new NotSupportedException("状态只读出口不得提交供给（S-A 纯只读）。");
        }

        public Task<SupplyJobStatus?> GetStatusAsync(string jobId, CancellationToken ct = default) =>
            Task.FromResult<SupplyJobStatus?>(null);

        public Task<IReadOnlyList<SupplyJobStatus>> ListStatusAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref ListStatusCalls);
            return throwOnList
                ? Task.FromException<IReadOnlyList<SupplyJobStatus>>(new InvalidOperationException("ledger unavailable"))
                : Task.FromResult(jobs);
        }

        public Task<bool> CancelAsync(string jobId, CancellationToken ct = default)
        {
            Interlocked.Increment(ref CancelCalls);
            return Task.FromResult(false);
        }
    }

    /// <summary>
    /// 可编排映射的 rooted 引擎替身：<c>ResolveIndexDirectory</c> 的行为（抛异常 / 指向被占路径 / 指向不存在路径）
    /// 由调用方给定；<c>BuildIndexAsync</c> 直接抛（只读出口不得建索引）。
    /// </summary>
    private sealed class ScriptedRootedEngine(Func<string, string> resolve) : IFullTextIndexRootedEngine
    {
        internal int ResolveCalls;

        internal int HasIndexCalls;

        public bool HasIndex(string directoryPath)
        {
            Interlocked.Increment(ref HasIndexCalls);
            return false;
        }

        public Task<FullTextSearchResult> SearchAsync(
            string query,
            string directoryPath,
            int maxResults = 30,
            string? fileExtensionFilter = null,
            string? subDirectoryFilter = null,
            CancellationToken ct = default,
            FullTextSearchScope? scope = null) =>
            Task.FromResult(new FullTextSearchResult(false, [], "test double", 0, 0));

        public Task<FullTextIndexResult> BuildIndexAsync(
            string directoryPath,
            string? filePatterns = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException("状态只读出口不得建索引（S-A 纯只读）。");

        public bool RemoveIndex(string directoryPath) => false;

        public string ResolveIndexDirectory(string corpusRootPath)
        {
            Interlocked.Increment(ref ResolveCalls);
            return resolve(corpusRootPath);
        }

        public void InvalidateScope(string corpusRootPath)
        {
        }

        public IndexDocumentProbe ProbeDocuments(string corpusRootPath) => new(false, null);
    }
}

/// <summary>
/// Slice S-A：**真实组合根**级证据 —— 控制器能被宿主 DI 造出来、访问器是单例且与预建服务共享、
/// 且 <c>Enabled=false</c> 时状态查询不构造组合、不创建索引根。
/// <para>与 <see cref="PuddingApplicationHostCompositionTests"/> 同集合：组合根会重配进程级 Serilog，不能并行跑。</para>
/// </summary>
[Collection("Pudding application host composition")]
public sealed class SAIndexStatusApiHostCompositionTests
{
    [Fact]
    public async Task Composition_Root_Resolves_The_Controller_And_Shares_One_Accessor_Without_Constructing_Anything()
    {
        var dataRoot = Path.Combine(
            Path.GetTempPath(),
            "PuddingAgent",
            $"sa-index-status-{Guid.NewGuid():N}");
        var scopeDirectory = Path.Combine(dataRoot, "scope");

        try
        {
            Directory.CreateDirectory(Path.Combine(dataRoot, "config"));
            Directory.CreateDirectory(scopeDirectory);
            File.WriteAllText(
                Path.Combine(dataRoot, "config", "system.json"),
                JsonSerializer.Serialize(
                    new
                    {
                        FullTextIndex = new
                        {
                            Enabled = false,
                            Scopes = new[] { scopeDirectory },
                            MaxIndexBytes = 1_073_741_824L,
                            MinRebuildInterval = "12:00:00",
                        },
                    },
                    new JsonSerializerOptions { WriteIndented = true }));

            var options = PuddingHostOptionsFactory.ForDesktopChild(
            [
                "--desktop-child",
                "--desktop-parent-pid", Environment.ProcessId.ToString(),
                "--data-root", dataRoot,
                "--urls", "http://0.0.0.0:18161",
            ]);

            var builder = PuddingApplicationHost.CreateBuilder([], options);
            await using var app = PuddingApplicationHost.Build(builder);

            // I9（组合根级）：控制器可被 DI 构造，且它确实是被反射防护网覆盖的那个类型。
            var controller = ActivatorUtilities.CreateInstance<IndexAdminController>(app.Services);
            Assert.IsType<IndexAdminController>(controller);
            Assert.Contains(
                typeof(PuddingApplicationHost).Assembly.GetTypes(),
                static type => type == typeof(IndexAdminController));

            // I2（组合根级）：访问器是单例，预建服务看到的是同一个实例。
            var accessor = app.Services.GetRequiredService<IFullTextIndexSupplyAccessor>();
            Assert.Same(accessor, app.Services.GetRequiredService<IFullTextIndexSupplyAccessor>());

            var prebuildService = Assert.Single(
                app.Services.GetServices<IHostedService>().OfType<PuddingAgent.Services.IndexPrebuildService>());
            Assert.Same(accessor, prebuildService.SupplyAccessor);

            // R2 / I1（组合根级）：Enabled=false ⇒ 查询不构造组合、不创建索引根。
            var probe = app.Services.GetRequiredService<FullTextIndexStatusProbe>();
            var snapshot = await probe.BuildAsync();

            Assert.False(snapshot.FullText.Enabled);
            Assert.False(snapshot.FullText.CompositionCreated);
            Assert.Null(accessor.Current);
            Assert.Empty(snapshot.FullText.Jobs);
            Assert.Equal("composition-not-created", snapshot.FullText.JobsReason);
            Assert.False(
                Directory.Exists(Path.Combine(dataRoot, "fulltext-index")),
                "默认关闭时，连组合根级的状态查询也不得创建索引根");

            // I9：控制器动作返回 200（HTTP 状态由 OkObjectResult 承载）。
            var actionResult = await controller.GetStatus(CancellationToken.None);
            var ok = Assert.IsType<OkObjectResult>(actionResult.Result);
            Assert.Equal(StatusCodes.Status200OK, ok.StatusCode);
            var body = Assert.IsType<FullTextIndexStatusSnapshot>(ok.Value);
            Assert.False(body.FullText.CompositionCreated);
        }
        finally
        {
            Serilog.Log.CloseAndFlush();
            if (Directory.Exists(dataRoot))
                Directory.Delete(dataRoot, recursive: true);
        }
    }
}
