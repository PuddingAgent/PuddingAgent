using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PuddingAgent.Services;
using PuddingFullTextIndex;
using PuddingFullTextIndex.Contracts;
using PuddingHost.Hosting;

namespace PuddingHost.Tests.Hosting;

/// <summary>
/// S5 R2：**组合根级**证明 —— 宿主把 Data 目录 <c>system.json</c> 的 <c>FullTextIndex</c> 配置值
/// 显式传给组件（预算 / 最小重建间隔），且供给所需的引擎接缝
/// （<c>IFullTextIndexRootedEngine</c>）在真实组合根上成立。
/// <para>
/// 与 <see cref="PuddingApplicationHostCompositionTests"/> 同集合：组合根会重配进程级 Serilog，不能并行跑。
/// 本用例保持 <c>Enabled=false</c>：只证接线，**不触发任何索引写入**。
/// </para>
/// </summary>
[Collection("Pudding application host composition")]
public sealed class S5FullTextIndexSupplyHostCompositionTests
{
    /// <summary>配置值逐字流入组件，且索引根不被触碰。</summary>
    [Fact]
    public async Task Composition_Root_Passes_Configured_Budget_And_Interval_Into_The_Component()
    {
        var dataRoot = Path.Combine(
            Path.GetTempPath(),
            "PuddingAgent",
            $"s5-host-composition-{Guid.NewGuid():N}");
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
                            Enabled = false,
                            Scopes = new[] { scopeDirectory },
                            MaxIndexBytes = 3_221_225_472L, // 3 GiB：与两个默认值都不同，配置流入才可证
                            MinRebuildInterval = "00:42:00",
                        },
                    },
                    new JsonSerializerOptions { WriteIndented = true }));

            var options = PuddingHostOptionsFactory.ForDesktopChild(
            [
                "--desktop-child",
                "--desktop-parent-pid", Environment.ProcessId.ToString(),
                "--data-root", dataRoot,
                "--urls", "http://0.0.0.0:18121",
            ]);

            var builder = PuddingApplicationHost.CreateBuilder([], options);
            await using var app = PuddingApplicationHost.Build(builder);

            var bound = app.Services.GetRequiredService<IOptions<FullTextIndexSupplyOptions>>().Value;
            Assert.False(bound.Enabled);
            Assert.Equal(3_221_225_472L, bound.MaxIndexBytes);
            Assert.Equal(TimeSpan.FromMinutes(42), bound.MinRebuildInterval);

            // ① 供给所需的引擎接缝在真实组合根上成立：
            //    staged 切换要「语料根 → 索引目录」映射 + reader 缓存失效，缺一即不可供给。
            var engine = app.Services.GetRequiredService<IFullTextSearchEngine>();
            Assert.IsAssignableFrom<IFullTextIndexRootedEngine>(engine);

            // ② 工厂按配置把预算/间隔传给组件（不是组件自带的 1 GiB 兜底常量）
            var factory = app.Services.GetRequiredService<IFullTextIndexSupplyCompositionFactory>();
            var composition = factory.Create(bound);

            Assert.Equal(3_221_225_472L, composition.ComponentOptions.DefaultBudgetBytes);
            Assert.Equal(TimeSpan.FromMinutes(42), composition.ComponentOptions.MinRebuildInterval);
            Assert.True(composition.ComponentOptions.UseStaging, "默认必须是暂存（安全）路径");
            Assert.NotNull(composition.Coordinator);

            // ③ 全程不得产生索引写入（连索引根目录都不许出现）
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
}
