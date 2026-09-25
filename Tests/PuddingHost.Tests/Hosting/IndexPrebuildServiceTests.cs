using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PuddingAgent.Services;
using PuddingFullTextIndex;
using PuddingFullTextIndex.Contracts;
using PuddingHost.Hosting;

namespace PuddingHost.Tests.Hosting;

/// <summary>
/// U4-7 A6（+ 两条等价保护），S5 适配：<see cref="IndexPrebuildService"/> 在**默认配置下立即返回且零索引 I/O**；
/// 只有配置显式开启且校验通过时才在启动路径之外按配置 scope 供给。
/// <para>
/// S5（2026-09-25）变更：写路径不再直写引擎，改为经协调器提交 —— 因此「开了就真的建了索引」这条断言
/// 现在由**协调器收到了哪些 scope** 承载（引擎 <c>BuildIndexAsync</c> 计数必须保持 0）。
/// 引擎与协调器一律用替身计数，**不碰真实 Lucene / 真实索引目录**。
/// </para>
/// </summary>
public sealed class IndexPrebuildServiceTests
{
    // ── A6 ────────────────────────────────────────────────────────────────
    /// <summary>
    /// A6：默认配置（<c>Enabled=false</c>，即使 <c>Scopes</c> 写了目录）⇒ <c>StartAsync</c> 立即返回，
    /// 且**引擎一次都没被碰过**（<c>HasIndex</c> / <c>BuildIndexAsync</c> 计数均为 0），
    /// **连供给组合都不构造**（S5 R4）。
    /// <para>刻意把 <see cref="IndexPrebuildService.StartupDelay"/> 置零：这样「偷偷跑后台预建」会在几百毫秒内暴露。</para>
    /// </summary>
    [Fact]
    public async Task StartAsync_With_Default_Configuration_Returns_Immediately_And_Never_Touches_The_Engine()
    {
        using var fixture = new S5Fixture();
        var scope = fixture.NewCorpus("default", ("a.txt", "aaa"));

        var engine = new CountingRootedEngine(fixture.IndexRoot);
        var coordinator = new ScriptedSupplyCoordinator();
        var factory = new StubCompositionFactory(new TestSupplyComposition(
            coordinator,
            new SupplyCoordinatorOptions(),
            _ => DateTimeOffset.UtcNow));

        var service = CreateService(
            engine,
            factory,
            new FullTextIndexSupplyOptions { Scopes = [scope] });

        var stopwatch = Stopwatch.StartNew();
        await service.StartAsync(CancellationToken.None);
        stopwatch.Stop();

        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(1),
            $"StartAsync 必须立即返回（默认关闭），实测 {stopwatch.ElapsedMilliseconds} ms");

        // 暴露窗口：配置门若被拆掉，这段等待足以让计数变红。
        await Task.Delay(300);

        Assert.Equal(0, factory.CreateCalls);
        Assert.Equal(0, coordinator.BuildCalls);
        Assert.Equal(0, engine.HasIndexCalls);
        Assert.Equal(0, engine.BuildCalls);
        Assert.False(Directory.Exists(fixture.IndexRoot));
    }

    /// <summary>
    /// 开启且校验通过 ⇒ 在启动路径之外把**配置里的 scope** 提交给协调器（不是 CWD —— 历史缺陷用
    /// <c>Directory.GetCurrentDirectory()</c>，本机等于运行时 bin 目录），且引擎**没有被直写**。
    /// </summary>
    [Fact]
    public async Task Enabled_Configuration_Supplies_Exactly_The_Configured_Scopes()
    {
        using var fixture = new S5Fixture();
        var scope = fixture.NewCorpus("enabled", ("a.txt", "aaa"));

        var engine = new CountingRootedEngine(fixture.IndexRoot);
        var coordinator = new ScriptedSupplyCoordinator(
            statusBehaviour: static (jobId, _) => ScriptedSupplyCoordinator.Succeeded(jobId));
        var factory = new StubCompositionFactory(new TestSupplyComposition(
            coordinator,
            new SupplyCoordinatorOptions(),
            _ => DateTimeOffset.MinValue));

        var service = CreateService(
            engine,
            factory,
            new FullTextIndexSupplyOptions
            {
                Enabled = true,
                Scopes = [scope],
                MinRebuildInterval = TimeSpan.Zero,
            });

        await service.StartAsync(CancellationToken.None);

        Assert.True(
            await S5TestHelpers.WaitUntilAsync(() => coordinator.BuildCalls >= 1, TimeSpan.FromSeconds(10)),
            "开启且校验通过后必须真的把 scope 提交出去");

        S5TestHelpers.AssertPathsEqual([scope], coordinator.SubmittedRootPaths);
        Assert.Equal(1, factory.CreateCalls);
        Assert.Equal(0, engine.BuildCalls);

        var submitted = Assert.Single(coordinator.SubmittedRootPaths);
        Assert.False(
            string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(Directory.GetCurrentDirectory())),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(submitted)),
                StringComparison.OrdinalIgnoreCase),
            "供给目标必须来自配置，绝不能是进程 CWD（历史缺陷）");
    }

    /// <summary>
    /// 开启但校验不过（<c>Scopes</c> 为空）⇒ fail-closed：**记 Error**（不是静默空转）且什么都不做
    /// —— 连供给组合都不构造。
    /// </summary>
    [Fact]
    public async Task Enabled_But_Rejected_Configuration_Logs_An_Error_And_Builds_Nothing()
    {
        var engine = new CountingRootedEngine(Path.Combine(Path.GetTempPath(), "pudding-s5-host", "never-created"));
        var coordinator = new ScriptedSupplyCoordinator();
        var factory = new StubCompositionFactory(new TestSupplyComposition(
            coordinator,
            new SupplyCoordinatorOptions(),
            _ => DateTimeOffset.MinValue));
        var logger = new SupplyRecordingLogger();

        var service = new IndexPrebuildService(
            engine,
            Options.Create(new FullTextIndexSupplyOptions { Enabled = true }),
            factory,
            logger)
        {
            StartupDelay = TimeSpan.Zero,
        };

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(200);

        Assert.Equal(0, factory.CreateCalls);
        Assert.Equal(0, coordinator.BuildCalls);
        Assert.Equal(0, engine.HasIndexCalls);
        Assert.Equal(0, engine.BuildCalls);
        Assert.Contains(
            logger.Entries,
            static entry => entry.StartsWith("Error:", StringComparison.Ordinal)
                            && entry.Contains("rejected", StringComparison.Ordinal));
    }

    // ── 局部工具 ──────────────────────────────────────────────────────────
    private static IndexPrebuildService CreateService(
        IFullTextSearchEngine engine,
        IFullTextIndexSupplyCompositionFactory factory,
        FullTextIndexSupplyOptions supplyOptions) =>
        new(engine, Options.Create(supplyOptions), factory, new SupplyRecordingLogger())
        {
            StartupDelay = TimeSpan.Zero,
            StatusPollInterval = TimeSpan.FromMilliseconds(20),
            BuildWaitTimeout = TimeSpan.FromSeconds(30),
        };
}
