using System.Collections.Generic;
using PuddingHost.Hosting;

namespace PuddingHost.Tests.Hosting;

/// <summary>
/// ADR-094 C-1 守卫：生效来源审计本身必须**零行为变更**。
/// 关键断言见 <see cref="ResolveUrlBinding_IsBehaviorIdenticalToLegacyLogic_AcrossAllInputCombinations"/> ——
/// 它用一个 8 组合矩阵把新实现与**旧实现谓词**逐项对拍，因此 C-1 不会静默改变监听地址。
/// </summary>
public sealed class StartupConfigurationAuditTests
{
    private static readonly string[] Options = ["http://127.0.0.1:9000"];
    private static readonly string[] Empty = [];

    [Fact]
    public void ResolveUrlBinding_PrefersOptionsOverEverything()
    {
        var decision = StartupConfigurationAudit.ResolveUrlBinding(
            Options, "http://env:1234", "http://config:5678");

        Assert.True(decision.ShouldCallUseUrls);
        Assert.Equal(Options, decision.ExplicitUrls);
        Assert.Equal("PuddingHostOptions.Urls", decision.Source);
        Assert.Equal("http://127.0.0.1:9000", decision.EffectiveUrls);
        Assert.Contains(decision.Warnings, w => w.Contains("覆盖", StringComparison.Ordinal));
    }

    [Fact]
    public void ResolveUrlBinding_UsesEnvironmentWithoutExplicitBinding()
    {
        var decision = StartupConfigurationAudit.ResolveUrlBinding(Empty, "http://env:1234", null);

        Assert.False(decision.ShouldCallUseUrls);
        Assert.Null(decision.ExplicitUrls);
        Assert.Equal("ASPNETCORE_URLS", decision.Source);
        Assert.Equal("http://env:1234", decision.EffectiveUrls);
        Assert.Empty(decision.Warnings);
    }

    /// <summary>
    /// 去噪守卫：`ASPNETCORE_URLS` 会被宿主配置链映射为 urls 键，两者取到同一值属**同一来源**，
    /// 不得报“多来源冲突”（否则每个设了该变量的启动都会刷假告警，把审计噪声化）。
    /// </summary>
    [Fact]
    public void ResolveUrlBinding_DoesNotWarnWhenConfigurationEchoesTheEnvironmentValue()
    {
        var decision = StartupConfigurationAudit.ResolveUrlBinding(Empty, "http://env:1234", "http://env:1234");

        Assert.Equal("ASPNETCORE_URLS", decision.Source);
        Assert.Empty(decision.Warnings);
    }

    [Fact]
    public void ResolveUrlBinding_WarnsWhenConfigurationDiffersFromEnvironment()
    {
        var decision = StartupConfigurationAudit.ResolveUrlBinding(Empty, "http://env:1234", "http://config:5678");

        Assert.Contains(decision.Warnings, w => w.Contains("取值不同", StringComparison.Ordinal));
    }

    /// <summary>
    /// C-1 的**证据性**断言：配置链里明明有 urls，却仍被硬编码默认值覆盖（既有行为）。
    /// 本测试锁定该事实并保留告警，但不修正它 —— 修正属 C-2。
    /// </summary>
    [Fact]
    public void ResolveUrlBinding_FallsBackToDefault_AndReportsConfigurationOverride()
    {
        var decision = StartupConfigurationAudit.ResolveUrlBinding(Empty, null, "http://config:5678");

        Assert.True(decision.ShouldCallUseUrls);
        Assert.Equal(new[] { StartupConfigurationAudit.DefaultUrls }, decision.ExplicitUrls);
        Assert.Equal("default(8080)", decision.Source);
        Assert.Equal("http://config:5678", decision.ConfigurationUrls);
        Assert.Contains(decision.Warnings, w => w.Contains("覆盖", StringComparison.Ordinal));
        Assert.Contains(decision.Warnings, w => w.Contains("C-2", StringComparison.Ordinal));
    }

    /// <summary>
    /// 行为等价性证明：新实现与旧实现的谓词/取值在所有输入组合下逐项一致。
    /// 旧实现原文（`PuddingApplicationHost`）：
    /// <c>options.Count &gt; 0 → UseUrls(options)</c>；
    /// <c>else if (string.IsNullOrEmpty(env)) → UseUrls("http://0.0.0.0:8080")</c>；否则不调用。
    /// </summary>
    [Fact]
    public void ResolveUrlBinding_IsBehaviorIdenticalToLegacyLogic_AcrossAllInputCombinations()
    {
        string?[] envValues = [null, "", "http://env:1234"];
        string?[] configValues = [null, "http://config:5678"];

        var checkedCombinations = 0;
        foreach (var optionUrls in new IReadOnlyList<string>[] { Empty, Options })
        {
            foreach (var env in envValues)
            {
                foreach (var config in configValues)
                {
                    // 旧实现：是否调用 UseUrls
                    var legacyShouldCall = optionUrls.Count > 0 || string.IsNullOrEmpty(env);
                    // 旧实现：调用时传入的值
                    IReadOnlyList<string>? legacyUrls = optionUrls.Count > 0
                        ? optionUrls
                        : string.IsNullOrEmpty(env)
                            ? ["http://0.0.0.0:8080"]
                            : null;

                    var decision = StartupConfigurationAudit.ResolveUrlBinding(optionUrls, env, config);

                    Assert.Equal(legacyShouldCall, decision.ShouldCallUseUrls);
                    Assert.Equal(legacyUrls, decision.ExplicitUrls);
                    checkedCombinations++;
                }
            }
        }

        Assert.Equal(12, checkedCombinations);
    }

    [Fact]
    public void Build_MarksSources_AndRedactsSensitiveKeys()
    {
        var binding = StartupConfigurationAudit.ResolveUrlBinding(Empty, null, null);
        var rows = StartupConfigurationAudit.Build(
            binding,
            Empty,
            null,
            ["http://localhost:8080"],
            corsOriginsFromConfiguration: null,
            aspnetcoreEnvironment: null,
            dataRoot: @"D:\data",
            dataRootSource: "PuddingDataRootBootstrapper.ResolveDataRoot(args)",
            serilogMinimumLevel: "Information");

        Assert.Contains(rows, r => r.Key == "urls.effective" && r.Source == "default(8080)");
        Assert.Contains(rows, r => r.Key == "Cors:AllowedOrigins" && r.Source == "inline-default");
        Assert.Contains(rows, r => r.Key == "ASPNETCORE_ENVIRONMENT" && r.Source == "default(Production)");
        Assert.Contains(rows, r => r.Key == "Serilog:MinimumLevel" && r.Source == "bootstrap-config");

        // DataRoot 属敏感键：只出指纹，不得出现原值。
        var dataRootRow = Assert.Single(rows, r => r.Key == "DataRoot");
        Assert.Contains("sha256:", dataRootRow.Value, StringComparison.Ordinal);
        Assert.DoesNotContain(@"D:\data", dataRootRow.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderValue_RedactsSensitive_AndKeepsOperationalValues()
    {
        Assert.Equal("<unset>", StartupConfigurationAudit.RenderValue("urls", null));
        Assert.Equal("http://x:1", StartupConfigurationAudit.RenderValue("urls", "http://x:1"));

        var redacted = StartupConfigurationAudit.RenderValue("Jwt:Key", "super-secret-value");
        Assert.Contains("sha256:", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("super-secret-value", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Emit_WritesHeaderRowsAndWarnings()
    {
        var binding = StartupConfigurationAudit.ResolveUrlBinding(Empty, null, "http://config:5678");
        var rows = StartupConfigurationAudit.Build(
            binding, Empty, null, ["http://localhost:8080"], null, "Production",
            @"D:\data", "PuddingHostOptions.DataRoot", null);

        var lines = new List<string>();
        StartupConfigurationAudit.Emit(lines.Add, rows, binding.Warnings);

        Assert.Contains(lines, l => l.Contains("ADR-094 C-1", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("urls.effective <- default(8080)", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("WARN", StringComparison.Ordinal));
    }
}
