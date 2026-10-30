using System.Globalization;
using PuddingCode.Configuration;
using PuddingLlmUsage.Contracts;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntime.Services.Llm;

/// <summary>
/// <see cref="ILlmBalanceProbe"/> 的生产实现：读 <c>llm.providers.json</c> → 打 DeepSeek
/// <c>GET /user/balance</c> → 把结果规范成 <see cref="LlmBalanceObservation"/>。
/// <para>
/// <b>与 <see cref="ProviderBalanceTool"/> 的关系</b>：工具要「原样字符串」以保住回执的金额格式
/// （<c>"0.00"</c> 不能变成 <c>"0"</c>）；消耗分析要「decimal 才能相减」。两者共用同一条底层查询
/// （<see cref="ProviderBalanceTool.TryResolveProvider"/> + <see cref="ProviderBalanceTool.QueryAsync"/>），
/// 各自的出口在这里转换 —— <b>逻辑只有一份，视图有两个</b>。
/// </para>
/// <para>
/// <b>失败不抛</b>：按端口约定，凡能描述成一次观测的失败都返回 Failure 观测。
/// </para>
/// </summary>
public sealed class LlmProviderBalanceProbe(
    PuddingDataPaths paths,
    HttpClient http) : ILlmBalanceProbe
{
    private readonly PuddingDataPaths _paths = paths ?? throw new ArgumentNullException(nameof(paths));

    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));

    /// <inheritdoc />
    public async Task<IReadOnlyList<LlmBalanceObservation>> QueryAsync(
        string providerId,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return [LlmBalanceObservation.Failure(
                "unknown", "unknown", "providerId 为空，无法查询余额。", observedAtUtc)];
        }

        var normalizedProviderId = providerId.Trim().ToLowerInvariant();

        var configPath = _paths.SystemConfigFile("llm.providers.json");
        if (!File.Exists(configPath))
        {
            return [LlmBalanceObservation.Failure(
                normalizedProviderId, "unknown",
                $"LLM 配置不存在（{configPath}）。", observedAtUtc)];
        }

        string configJson;
        try
        {
            configJson = await File.ReadAllTextAsync(configPath, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException exception)
        {
            return [LlmBalanceObservation.Failure(
                normalizedProviderId, "unknown",
                $"读取 LLM 配置失败：{exception.Message}", observedAtUtc)];
        }

        if (!ProviderBalanceTool.TryResolveProvider(configJson, normalizedProviderId, out var endpoint, out var resolveError))
            return [LlmBalanceObservation.Failure(normalizedProviderId, "unknown", resolveError, observedAtUtc)];

        ProviderBalanceSnapshot snapshot;
        try
        {
            snapshot = await ProviderBalanceTool.QueryAsync(_http, endpoint, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException exception)
        {
            return [LlmBalanceObservation.Failure(
                normalizedProviderId, "unknown",
                $"查询余额时网络失败：{exception.Message}", observedAtUtc)];
        }
        catch (InvalidOperationException exception)
        {
            // QueryAsync 用 InvalidOperationException 表达「HTTP 非 2xx / 响应形状不对」。
            return [LlmBalanceObservation.Failure(normalizedProviderId, "unknown", exception.Message, observedAtUtc)];
        }

        if (snapshot.Balances.Count == 0)
        {
            return [LlmBalanceObservation.Failure(
                normalizedProviderId, "unknown",
                "余额接口未返回 balance_infos —— 该 provider 可能不支持 /user/balance。",
                observedAtUtc)];
        }

        var observations = new List<LlmBalanceObservation>(snapshot.Balances.Count);

        foreach (var entry in snapshot.Balances)
        {
            if (!TryParse(entry.TotalBalance, out var total))
            {
                observations.Add(LlmBalanceObservation.Failure(
                    normalizedProviderId, entry.Currency,
                    $"余额字段无法解析为金额：total_balance='{entry.TotalBalance}'",
                    observedAtUtc));
                continue;
            }

            // 赠送/充值字段缺失是可接受的（服务商不给），但给了却解析不了就是数据问题 ——
            // 记为 null 而不是 0，避免把「不知道」写成「没有赠送额度」。
            var granted = TryParse(entry.GrantedBalance, out var grantedValue) ? grantedValue : (decimal?)null;
            var toppedUp = TryParse(entry.ToppedUpBalance, out var toppedUpValue) ? toppedUpValue : (decimal?)null;

            observations.Add(LlmBalanceObservation.Success(
                normalizedProviderId,
                entry.Currency,
                total,
                observedAtUtc,
                granted,
                toppedUp));
        }

        return observations;
    }

    /// <summary>
    /// 金额解析。用 <see cref="CultureInfo.InvariantCulture"/>：服务商返回的是 <c>"254.92"</c> 这种
    /// 点号小数，若跟随当前区域性（例如逗号小数位的语言），会解析失败或解析成完全不同的数。
    /// </summary>
    private static bool TryParse(string? text, out decimal value)
    {
        value = 0m;
        return !string.IsNullOrWhiteSpace(text)
               && decimal.TryParse(
                   text.Trim(),
                   NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                   CultureInfo.InvariantCulture,
                   out value);
    }
}
