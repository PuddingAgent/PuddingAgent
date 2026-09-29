using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using PuddingAgent.Services;
using PuddingFullTextIndex;
using PuddingFullTextIndex.Contracts;
using PuddingFullTextIndex.Infrastructure.Maintenance;
using PuddingHost.Hosting;

namespace PuddingHost.Tests.Hosting;

/// <summary>
/// S5b（2026-09-27）：**组合根级**证明 —— 宿主把 Data 目录 <c>system.json</c> 的
/// <c>FullTextIndex:Maintenance</c> 子节绑定到组件的 <see cref="MaintenanceOptions"/>，
/// 维护所需的「查询侧同一个引擎实例」接缝在真实组合根上成立，且
/// <see cref="FullTextIndexMaintenanceHostedService"/> 恰好注册一次。
/// <para>
/// 与 <see cref="PuddingApplicationHostCompositionTests"/> 同集合：组合根会重配进程级 Serilog，不能并行跑。
/// 本用例**只 Build 不 Start**（宿主未启动 ⇒ hosted service 不执行），且全程不触碰真实索引根。
/// </para>
/// </summary>
[Collection("Pudding application host composition")]
public sealed class S5bFullTextIndexMaintenanceHostBindingTests
{
    /// <summary>
    /// 配置绑定 + 单一真源 + D4 引擎实例同一性（真实组合根）：
    /// <list type="number">
    /// <item>维护节逐字绑定（含从组件默认值取不到的旋钮值）；</item>
    /// <item>维护节里写的**诱饵预算**在装配时被供给节覆盖（宿主没有第二份预算真源）；</item>
    /// <item>维护组合拿到的引擎实例与查询侧 <c>IFullTextSearchEngine</c> **引用相等**；</item>
    /// <item>维护 scope 的规范键与供给侧（真实协调器 <c>PlanAsync</c>）逐字符相同；</item>
    /// <item>装配过程不触碰索引根目录。</item>
    /// </list>
    /// </summary>
    [Fact]
    public async Task Composition_Root_Binds_Maintenance_Section_And_Wires_The_Query_Side_Engine()
    {
        var dataRoot = Path.Combine(
            Path.GetTempPath(),
            "PuddingAgent",
            $"s5b-maintenance-binding-{Guid.NewGuid():N}");
        var scopeDirectory = Path.Combine(dataRoot, "scope");

        try
        {
            var configRoot = Path.Combine(dataRoot, "config");
            Directory.CreateDirectory(configRoot);
            Directory.CreateDirectory(scopeDirectory);

            File.WriteAllText(
                Path.Combine(configRoot, "system.json"),
                JsonSerializer.Serialize(
                    new
                    {
                        FullTextIndex = new
                        {
                            Enabled = true,
                            Scopes = new[] { scopeDirectory },
                            MaxIndexBytes = 3_221_225_472L,   // 3 GiB：与两个默认值都不同，配置流入才可证
                            MinRebuildInterval = "01:00:00",
                            Maintenance = new
                            {
                                Enabled = true,
                                QueueCapacity = 1234,                 // 组件默认 4096
                                RecoveryScanInterval = "00:20:00",    // 组件默认 15 分钟
                                MaxIndexBytes = 999L,                 // 诱饵：装配时必须被供给节覆盖
                            },
                        },
                    },
                    new JsonSerializerOptions { WriteIndented = true }));

            var options = PuddingHostOptionsFactory.ForDesktopChild(
            [
                "--desktop-child",
                "--desktop-parent-pid", Environment.ProcessId.ToString(),
                "--data-root", dataRoot,
                "--urls", "http://0.0.0.0:18131",
            ]);

            var builder = PuddingApplicationHost.CreateBuilder([], options);
            await using var app = PuddingApplicationHost.Build(builder);

            // ① 维护节逐字绑定（含旋钮与诱饵预算）。
            var boundMaintenance = app.Services.GetRequiredService<IOptions<MaintenanceOptions>>().Value;
            Assert.True(boundMaintenance.Enabled);
            Assert.Equal(1234, boundMaintenance.QueueCapacity);
            Assert.Equal(TimeSpan.FromMinutes(20), boundMaintenance.RecoveryScanInterval);
            Assert.Equal(999L, boundMaintenance.MaxIndexBytes);

            var boundSupply = app.Services.GetRequiredService<IOptions<FullTextIndexSupplyOptions>>().Value;
            Assert.True(boundSupply.Enabled);
            Assert.Equal(scopeDirectory, Assert.Single(boundSupply.Scopes));

            // ② 装配时把供给同源的四项合并进来：诱饵预算被覆盖（单一真源）。
            var indexOptions = app.Services.GetRequiredService<FullTextIndexOptions>();
            var effective = FullTextIndexMaintenanceOptions.ApplySingleSource(
                boundMaintenance, boundSupply, indexOptions);

            Assert.Equal(3_221_225_472L, effective.MaxIndexBytes);
            Assert.NotEqual(boundMaintenance.MaxIndexBytes, effective.MaxIndexBytes);
            Assert.NotEqual(FullTextIndexSupplyOptions.DefaultMaxIndexBytes, effective.MaxIndexBytes);
            Assert.Equal(indexOptions.IndexRootDirectory, effective.IndexRootDirectory);
            Assert.Equal([scopeDirectory], effective.Scopes);
            var validation = MaintenanceOptions.Validate(effective);
            Assert.True(validation.IsValid, "生效维护选项必须通过组件 fail-closed 校验");

            // ③ 维护所需的引擎接缝在真实组合根上成立。
            var engine = app.Services.GetRequiredService<IFullTextSearchEngine>();
            Assert.IsAssignableFrom<IFullTextIndexRootedEngine>(engine);

            var resolution = FullTextIndexSupplyResolver.Resolve(boundSupply);
            Assert.True(resolution.Succeeded, resolution.Describe());

            var factory = app.Services.GetRequiredService<IFullTextIndexMaintenanceCompositionFactory>();
            var composition = factory.Create(effective, resolution.AcceptedScopes);

            // ★ D4：与查询侧**引用相等**（不是「同型新实例」）。
            Assert.Same(engine, composition.LiveEngine);
            Assert.Same(indexOptions, composition.IndexOptions);

            var scope = Assert.Single(composition.Scopes);
            Assert.Equal(
                FullTextChangeCoalescer.NormalizeComparisonKey(scopeDirectory),
                scope.ScopeKey,
                StringComparer.Ordinal);

            // ④ 供给侧（真实协调器）对同一语料根算出的键：逐字符相同。
            var supplyComposition = app.Services
                .GetRequiredService<IFullTextIndexSupplyCompositionFactory>()
                .Create(boundSupply);
            var plan = await supplyComposition.Coordinator.PlanAsync(new SupplyScopeRequest(scopeDirectory));
            Assert.True(plan.Accepted, "前置：供给侧必须接受该 scope");
            Assert.Equal(Assert.Single(plan.AcceptedScopes).ScopeKey, scope.ScopeKey, StringComparer.Ordinal);

            // ⑤ 维护 hosted service 恰好注册一次。
            Assert.Single(app.Services.GetServices<IHostedService>().OfType<FullTextIndexMaintenanceHostedService>());

            // ⑥ 装配不得触碰索引根（不启动宿主 ⇒ 更不会有维护写入）。
            Assert.False(
                Directory.Exists(Path.Combine(dataRoot, "fulltext-index")),
                "组合根装配与配置流入都不得触碰索引根");
        }
        finally
        {
            Serilog.Log.CloseAndFlush();
            if (Directory.Exists(dataRoot))
                Directory.Delete(dataRoot, recursive: true);
        }
    }

    /// <summary>
    /// 反例（**默认即关闭**的机械证明）：<c>system.json</c> 里只有 <c>FullTextIndex</c> 顶层
    /// （供给开着、scopes 有值）而**没有** <c>Maintenance</c> 子节 ⇒ 维护选项就是组件默认值（关闭），
    /// 且生效选项的 <c>Enabled</c> 仍是 false（不会因为供给开着就被打开）。
    /// </summary>
    [Fact]
    public async Task Without_A_Maintenance_Section_The_Maintenance_Options_Stay_Disabled()
    {
        var dataRoot = Path.Combine(
            Path.GetTempPath(),
            "PuddingAgent",
            $"s5b-maintenance-binding-default-{Guid.NewGuid():N}");
        var scopeDirectory = Path.Combine(dataRoot, "scope");

        try
        {
            var configRoot = Path.Combine(dataRoot, "config");
            Directory.CreateDirectory(configRoot);
            Directory.CreateDirectory(scopeDirectory);

            File.WriteAllText(
                Path.Combine(configRoot, "system.json"),
                JsonSerializer.Serialize(
                    new
                    {
                        FullTextIndex = new
                        {
                            Enabled = true,
                            Scopes = new[] { scopeDirectory },
                        },
                    },
                    new JsonSerializerOptions { WriteIndented = true }));

            var options = PuddingHostOptionsFactory.ForDesktopChild(
            [
                "--desktop-child",
                "--desktop-parent-pid", Environment.ProcessId.ToString(),
                "--data-root", dataRoot,
                "--urls", "http://0.0.0.0:18132",
            ]);

            var builder = PuddingApplicationHost.CreateBuilder([], options);
            await using var app = PuddingApplicationHost.Build(builder);

            var boundMaintenance = app.Services.GetRequiredService<IOptions<MaintenanceOptions>>().Value;
            var defaults = new MaintenanceOptions();

            Assert.False(boundMaintenance.Enabled);
            Assert.Equal(defaults.QueueCapacity, boundMaintenance.QueueCapacity);
            Assert.Equal(defaults.RecoveryScanInterval, boundMaintenance.RecoveryScanInterval);
            Assert.Equal(defaults.MaxIndexBytes, boundMaintenance.MaxIndexBytes);
            Assert.Empty(boundMaintenance.Scopes);

            // 生效选项：供给同源四项被填进来，但开关仍是关闭（默认关闭不被供给状态「顶开」）。
            var effective = FullTextIndexMaintenanceOptions.ApplySingleSource(
                boundMaintenance,
                app.Services.GetRequiredService<IOptions<FullTextIndexSupplyOptions>>().Value,
                app.Services.GetRequiredService<FullTextIndexOptions>());

            Assert.False(effective.Enabled);
            Assert.Equal(scopeDirectory, Assert.Single(effective.Scopes));

            // hosted service 仍注册（门控在 StartAsync 首句），且宿主未启动 ⇒ 零副作用。
            Assert.Single(app.Services.GetServices<IHostedService>().OfType<FullTextIndexMaintenanceHostedService>());
            Assert.False(Directory.Exists(Path.Combine(dataRoot, "fulltext-index")));
        }
        finally
        {
            Serilog.Log.CloseAndFlush();
            if (Directory.Exists(dataRoot))
                Directory.Delete(dataRoot, recursive: true);
        }
    }
}
