using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using PuddingAgent.Services;
using PuddingHost.Hosting;

namespace PuddingHost.Tests.Hosting;

/// <summary>
/// U4-7 R4：宿主接线证明 —— 供给参数真的从 **Data 目录的 <c>system.json</c>**（<c>&lt;DataRoot&gt;/config/system.json</c>）
/// 的 <c>FullTextIndex</c> 节绑定到 DI，且 <see cref="IndexPrebuildService"/> 已被注册为 hosted service。
/// <para>
/// 与 <see cref="PuddingApplicationHostCompositionTests"/> 同集合：组合根会重配进程级 Serilog，不能并行跑。
/// </para>
/// </summary>
[Collection("Pudding application host composition")]
public sealed class FullTextIndexSupplyHostBindingTests
{
    /// <summary>
    /// 写一份带 <c>FullTextIndex</c> 节的 <c>system.json</c> ⇒ 选项必须逐字段绑定（含 <c>Scopes</c> 列表
    /// 与 <c>TimeSpan</c>），并且 <c>IndexPrebuildService</c> 恰好注册一次为 hosted service。
    /// </summary>
    [Fact]
    public async Task SystemJson_FullTextIndex_Section_Binds_Into_Options_And_The_Service_Is_Registered()
    {
        var dataRoot = Path.Combine(
            Path.GetTempPath(),
            "PuddingAgent",
            $"u4-7-host-binding-{Guid.NewGuid():N}");
        var scopeDirectory = Path.Combine(dataRoot, "scope");

        try
        {
            var configRoot = Path.Combine(dataRoot, "config");
            Directory.CreateDirectory(configRoot);
            Directory.CreateDirectory(scopeDirectory);

            var systemJson = new
            {
                FullTextIndex = new
                {
                    Enabled = true,
                    Scopes = new[] { scopeDirectory },
                    WorkspaceRoot = dataRoot,
                    MaxIndexBytes = 2_147_483_648L,
                    MinRebuildInterval = "06:00:00",
                },
            };
            File.WriteAllText(
                Path.Combine(configRoot, "system.json"),
                JsonSerializer.Serialize(systemJson, new JsonSerializerOptions { WriteIndented = true }));

            var options = PuddingHostOptionsFactory.ForDesktopChild(
            [
                "--desktop-child",
                "--desktop-parent-pid", Environment.ProcessId.ToString(),
                "--data-root", dataRoot,
                "--urls", "http://0.0.0.0:18081",
            ]);

            var builder = PuddingApplicationHost.CreateBuilder([], options);
            await using var app = PuddingApplicationHost.Build(builder);

            var bound = app.Services.GetRequiredService<IOptions<FullTextIndexSupplyOptions>>().Value;
            Assert.True(bound.Enabled);
            Assert.Equal(scopeDirectory, Assert.Single(bound.Scopes));
            Assert.Equal(dataRoot, bound.WorkspaceRoot);
            Assert.Equal(2_147_483_648L, bound.MaxIndexBytes);
            Assert.Equal(TimeSpan.FromHours(6), bound.MinRebuildInterval);

            // 门禁：校验器对这份真实配置必须放行（Enabled=true 且 scope 目录存在）。
            var resolution = FullTextIndexSupplyResolver.Resolve(bound);
            Assert.True(resolution.Succeeded, resolution.Describe());
            Assert.Equal([scopeDirectory], resolution.AcceptedScopes);

            Assert.Single(
                app.Services.GetServices<IHostedService>().OfType<IndexPrebuildService>());
        }
        finally
        {
            Serilog.Log.CloseAndFlush();
            if (Directory.Exists(dataRoot))
                Directory.Delete(dataRoot, recursive: true);
        }
    }

    /// <summary>
    /// 反例（**默认即关闭**的机械证明）：Data 目录里**没有** <c>FullTextIndex</c> 节 ⇒
    /// 选项就是默认值（关闭 / 1 GiB / 12h），且解析结果是空动作。
    /// </summary>
    [Fact]
    public async Task Without_A_FullTextIndex_Section_The_Options_Stay_Disabled()
    {
        var dataRoot = Path.Combine(
            Path.GetTempPath(),
            "PuddingAgent",
            $"u4-7-host-binding-default-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(Path.Combine(dataRoot, "config"));
            File.WriteAllText(
                Path.Combine(dataRoot, "config", "system.json"),
                """{ "environment": "test" }""");

            var options = PuddingHostOptionsFactory.ForDesktopChild(
            [
                "--desktop-child",
                "--desktop-parent-pid", Environment.ProcessId.ToString(),
                "--data-root", dataRoot,
                "--urls", "http://0.0.0.0:18082",
            ]);

            var builder = PuddingApplicationHost.CreateBuilder([], options);
            await using var app = PuddingApplicationHost.Build(builder);

            var bound = app.Services.GetRequiredService<IOptions<FullTextIndexSupplyOptions>>().Value;
            Assert.False(bound.Enabled);
            Assert.Empty(bound.Scopes);
            Assert.Equal(FullTextIndexSupplyOptions.DefaultMaxIndexBytes, bound.MaxIndexBytes);
            Assert.True(FullTextIndexSupplyResolver.Resolve(bound).IsNoOp);
        }
        finally
        {
            Serilog.Log.CloseAndFlush();
            if (Directory.Exists(dataRoot))
                Directory.Delete(dataRoot, recursive: true);
        }
    }
}
