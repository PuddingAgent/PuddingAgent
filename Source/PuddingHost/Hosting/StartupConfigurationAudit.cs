using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace PuddingHost.Hosting;

/// <summary>
/// ADR-094 C-1：启动期「生效来源审计」。
/// <para>
/// <b>严格零行为变更</b>：本类型只<em>观测与记录</em>，不参与任何解析决策。
/// 唯一带判断的方法 <see cref="ResolveUrlBinding"/> 以<b>逐字等价</b>方式复刻既有 URL 绑定逻辑，
/// 并把「配置链里的 urls 被硬编码默认值覆盖」这一<b>既有事实</b>作为审计行与告警输出，
/// 而<b>不修正它</b>（修正属 C-2，需二次批准）。
/// </para>
/// <para>
/// 目的：让「哪个值最终生效、来自哪一层」在启动日志里可查。此前该问题只能靠读代码推断，
/// 且入站请求日志未启用 ⇒ 事后无法回溯。
/// </para>
/// </summary>
public static class StartupConfigurationAudit
{
    /// <summary>与 `PuddingApplicationHost` 中的硬编码默认值保持一致（该字面量属既有行为，C-1 不改）。</summary>
    public const string DefaultUrls = "http://0.0.0.0:8080";

    private const string Header = "[StartupConfig] 生效来源审计（ADR-094 C-1：观测专用，不改变任何解析结果）";

    /// <summary>只输出指纹、不输出原值的键片段。</summary>
    private static readonly string[] SensitiveKeyFragments =
        ["DataRoot", "Jwt:Key", "ApiKey", "Secret", "Password", "ConnectionString"];

    /// <summary>审计行：键 / 来源 / 触发层 / 已脱敏的值。</summary>
    public sealed record Row(string Key, string Source, string Layer, string Value);

    /// <summary>URL 绑定决策 —— 与既有实现逐字等价（见类型注释）。</summary>
    public sealed record UrlBindingDecision(
        IReadOnlyList<string>? ExplicitUrls,
        string Source,
        string? EffectiveUrls,
        string? ConfigurationUrls,
        IReadOnlyList<string> Warnings)
    {
        /// <summary>是否应调用 <c>UseUrls</c>（null = 交由宿主配置链处理，与既有行为一致）。</summary>
        public bool ShouldCallUseUrls => ExplicitUrls is not null;
    }

    /// <summary>
    /// 复刻既有 URL 绑定判断（`PuddingApplicationHost`）：
    /// options 优先 → 否则环境变量为空时用默认 8080 → 否则不显式绑定（交宿主配置链）。
    /// </summary>
    public static UrlBindingDecision ResolveUrlBinding(
        IReadOnlyList<string>? optionUrls,
        string? environmentUrls,
        string? configurationUrls)
    {
        var hasOptions = optionUrls is { Count: > 0 };
        var hasEnv = !string.IsNullOrEmpty(environmentUrls);
        var hasConfig = !string.IsNullOrEmpty(configurationUrls);
        var warnings = new List<string>();

        if (hasOptions)
        {
            if (hasConfig)
            {
                warnings.Add(
                    $"配置链中的 urls（{configurationUrls}）被 PuddingHostOptions.Urls 覆盖（options 优先）。");
            }

            return new UrlBindingDecision(
                optionUrls,
                "PuddingHostOptions.Urls",
                string.Join(";", optionUrls!),
                configurationUrls,
                warnings);
        }

        if (!hasEnv)
        {
            if (hasConfig)
            {
                warnings.Add(
                    $"配置链中的 urls（{configurationUrls}）被硬编码默认值 {DefaultUrls} 覆盖；"
                    + "ADR-094 C-2 待裁决。");
            }

            return new UrlBindingDecision(
                [DefaultUrls],
                "default(8080)",
                DefaultUrls,
                configurationUrls,
                warnings);
        }

        if (hasConfig)
        {
            warnings.Add(
                $"配置链 urls（{configurationUrls}）与环境变量 ASPNETCORE_URLS 同时存在，"
                + "最终由宿主配置链决定；请以 urls.effective 为准。");
        }

        return new UrlBindingDecision(
            null,
            "ASPNETCORE_URLS",
            environmentUrls,
            configurationUrls,
            warnings);
    }

    /// <summary>敏感键判定（按片段匹配，大小写不敏感）。</summary>
    public static bool IsSensitive(string key)
        => SensitiveKeyFragments.Any(f => key.Contains(f, StringComparison.OrdinalIgnoreCase));

    /// <summary>值指纹：长度 + SHA-256 前 12 位十六进制。</summary>
    public static string Fingerprint(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return $"len={value.Length} sha256:{Convert.ToHexString(hash)[..12].ToLowerInvariant()}";
    }

    /// <summary>渲染审计值：<c>null</c> → <c>&lt;unset&gt;</c>；敏感键 → 指纹；其余 → 原值。</summary>
    public static string RenderValue(string key, string? value)
    {
        if (value is null)
            return "<unset>";

        return IsSensitive(key) ? Fingerprint(value) : value;
    }

    /// <summary>组装审计行（纯函数，供测试与启动期共用同一份口径）。</summary>
    public static IReadOnlyList<Row> Build(
        UrlBindingDecision urlBinding,
        IReadOnlyList<string>? optionUrls,
        string? environmentUrls,
        IReadOnlyList<string> resolvedCorsOrigins,
        string? corsOriginsFromConfiguration,
        string? aspnetcoreEnvironment,
        string? dataRoot,
        string dataRootSource,
        string? serilogMinimumLevel)
    {
        var rows = new List<Row>
        {
            new("urls.effective", urlBinding.Source, "resolved", RenderValue("urls", urlBinding.EffectiveUrls)),
            new("urls.options", "PuddingHostOptions.Urls", "host-options",
                RenderValue("urls", optionUrls is { Count: > 0 } ? string.Join(";", optionUrls) : null)),
            new("urls.env", "ASPNETCORE_URLS", "environment", RenderValue("urls", environmentUrls)),
            new("urls.configuration", "configuration-chain", "config",
                RenderValue("urls", urlBinding.ConfigurationUrls)),
            new("Cors:AllowedOrigins",
                corsOriginsFromConfiguration is null ? "inline-default" : "configuration-chain",
                "config",
                RenderValue("Cors:AllowedOrigins", string.Join(";", resolvedCorsOrigins))),
            new("ASPNETCORE_ENVIRONMENT",
                aspnetcoreEnvironment is null ? "default(Production)" : "environment",
                "environment",
                RenderValue("ASPNETCORE_ENVIRONMENT", aspnetcoreEnvironment ?? "Production")),
            new("DataRoot", dataRootSource, "host-options", RenderValue("DataRoot", dataRoot)),
            new("Serilog:MinimumLevel",
                serilogMinimumLevel is null ? "unset" : "bootstrap-config",
                "bootstrap-config",
                RenderValue("Serilog:MinimumLevel", serilogMinimumLevel)),
        };

        return rows;
    }

    /// <summary>把审计行 + 告警写入给定的输出（启动期实际使用 <c>Console.WriteLine</c>）。</summary>
    public static void Emit(Action<string> write, IReadOnlyList<Row> rows, IReadOnlyList<string> warnings)
    {
        write(Header);
        foreach (var row in rows)
            write($"[StartupConfig]   {row.Key} <- {row.Source} ({row.Layer}) = {row.Value}");

        foreach (var warning in warnings)
            write($"[StartupConfig]   WARN {warning}");
    }
}
