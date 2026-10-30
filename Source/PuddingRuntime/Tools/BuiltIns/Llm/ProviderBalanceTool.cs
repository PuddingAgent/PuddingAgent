using System.Text.Json;
using PuddingCode.Configuration;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.Tools;

/// <summary>
/// 查询 LLM 服务商的账户余额（目前只实现 DeepSeek 的 <c>GET /user/balance</c>）。
/// <para>
/// 为什么要它：模型成本是这台机器的主要开销，而「还剩多少钱」原先只能靠人去后台看。
/// Agent 自己能查，就能在长任务前估预算、在异常时对账。
/// </para>
/// <para>
/// <b>凭据纪律</b>：apiKey 只在本进程内使用，<b>绝不</b>出现在回执、异常或日志里 ——
/// 回执只给 providerId 与 host。
/// </para>
/// </summary>
[Tool(
    id: "provider_balance",
    name: "模型余额",
    description: "查询 LLM 服务商的账户余额与可用状态。【何时用】长任务前估预算、怀疑余额不足导致调用失败时对账、需要报当前余额时。【怎么用】providerId 可选，缺省 deepseek；目前只实现 DeepSeek 的 /user/balance。【坑】余额是时点快照、不等于本轮花费；赠送余额与充值余额分开计，只报总额会掩盖「赠送额度已用完」；apiKey 永不出现在回执里，只给 providerId 与 host。")]
public sealed class ProviderBalanceTool(PuddingDataPaths paths) : PuddingToolBase<ProviderBalanceArgs>
{
    private const string DefaultProviderId = "deepseek";

    private readonly PuddingDataPaths _paths = paths ?? throw new ArgumentNullException(nameof(paths));

    /// <summary>
    /// 共享的 HTTP 客户端。<c>internal</c> 而不是 <c>private</c>：余额采样器（<c>LlmProviderBalanceProbe</c>）
    /// 必须用同一个实例，否则每条采样路径各建一个客户端，连接池与 DNS 缓存都会退化；
    /// 测试也要能注入假的 handler，所以可见性必须到程序集内。
    /// </summary>
    internal static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(20) };

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        ProviderBalanceArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        var providerId = string.IsNullOrWhiteSpace(args.ProviderId)
            ? DefaultProviderId
            : args.ProviderId.Trim().ToLowerInvariant();

        var configPath = _paths.SystemConfigFile("llm.providers.json");
        if (!File.Exists(configPath))
        {
            return ToolExecutionResult.Fail(
                $"无法查询余额：LLM 配置不存在（{configPath}）。请先配置 providers 再重试。");
        }

        string configJson;
        try
        {
            configJson = await File.ReadAllTextAsync(configPath, ct).ConfigureAwait(false);
        }
        catch (IOException exception)
        {
            return ToolExecutionResult.Fail($"无法读取 LLM 配置（{configPath}）：{exception.Message}");
        }

        if (!TryResolveProvider(configJson, providerId, out var endpoint, out var resolveError))
        {
            return ToolExecutionResult.Fail(resolveError);
        }

        try
        {
            var snapshot = await QueryAsync(SharedHttp, endpoint, ct).ConfigureAwait(false);
            return ToolExecutionResult.Ok(ToJson(snapshot));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException exception)
        {
            return ToolExecutionResult.Fail(
                $"查询 {endpoint.ProviderId} 余额时网络失败：{exception.Message}");
        }
        catch (InvalidOperationException exception)
        {
            return ToolExecutionResult.Fail(exception.Message);
        }
    }

    /// <summary>
    /// 从 <c>llm.providers.json</c> 取出指定 provider 的端点与密钥。
    /// <para><c>apiKey</c> 为空时支持 <c>apiKeyRef</c>（按环境变量名解析）；解析不到就明确报错，不猜。</para>
    /// </summary>
    public static bool TryResolveProvider(
        string configJson,
        string providerId,
        out ProviderBalanceEndpoint endpoint,
        out string error)
    {
        endpoint = default!;
        error = string.Empty;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(configJson);
        }
        catch (JsonException exception)
        {
            error = $"LLM 配置不是合法 JSON：{exception.Message}";
            return false;
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("providers", out var providers)
                || providers.ValueKind != JsonValueKind.Array)
            {
                error = "LLM 配置缺少 providers 数组，无法定位服务商。";
                return false;
            }

            foreach (var provider in providers.EnumerateArray())
            {
                var id = provider.TryGetProperty("providerId", out var idElement)
                    ? idElement.GetString()
                    : null;

                if (!string.Equals(id, providerId, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (provider.TryGetProperty("isEnabled", out var enabled)
                    && enabled.ValueKind == JsonValueKind.False)
                {
                    error = $"provider '{providerId}' 已被禁用（isEnabled=false），余额查询同样不发起。";
                    return false;
                }

                var baseUrl = provider.TryGetProperty("baseUrl", out var baseUrlElement)
                    ? baseUrlElement.GetString()
                    : null;

                if (string.IsNullOrWhiteSpace(baseUrl))
                {
                    error = $"provider '{providerId}' 缺少 baseUrl，无法定位余额端点。";
                    return false;
                }

                var apiKey = provider.TryGetProperty("apiKey", out var keyElement)
                    ? keyElement.GetString()
                    : null;

                var keySource = "apiKey";

                if (string.IsNullOrWhiteSpace(apiKey)
                    && provider.TryGetProperty("apiKeyRef", out var refElement)
                    && refElement.GetString() is { Length: > 0 } envName)
                {
                    apiKey = Environment.GetEnvironmentVariable(envName);
                    keySource = $"env:{envName}";
                }

                if (string.IsNullOrWhiteSpace(apiKey))
                {
                    error = $"provider '{providerId}' 没有可用凭据（apiKey 与 apiKeyRef 均为空或环境变量未设置）。";
                    return false;
                }

                endpoint = new ProviderBalanceEndpoint(providerId, baseUrl.Trim(), apiKey.Trim(), keySource);
                return true;
            }
        }

        error = $"LLM 配置里没有 provider '{providerId}'。可用值见 providers[].providerId。";
        return false;
    }

    /// <summary>
    /// 调 <c>{baseUrl}/user/balance</c> 并解析。
    /// <para>HTTP 与解析分开，测试用假的 <see cref="HttpMessageHandler"/> 覆盖，不必真联网。</para>
    /// </summary>
    public static async Task<ProviderBalanceSnapshot> QueryAsync(
        HttpClient http,
        ProviderBalanceEndpoint endpoint,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(endpoint);

        var url = endpoint.BaseUrl.TrimEnd('/') + "/user/balance";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", endpoint.ApiKey);
        request.Headers.Accept.ParseAdd("application/json");

        using var response = await http
            .SendAsync(request, HttpCompletionOption.ResponseContentRead, ct)
            .ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var snippet = body.Length > 200 ? body[..200] : body;
            throw new InvalidOperationException(
                $"余额接口返回 {(int)response.StatusCode} {response.ReasonPhrase}：{snippet}（凭据无效或端点不支持该接口）");
        }

        var host = new Uri(endpoint.BaseUrl).Host;

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            var available = root.TryGetProperty("is_available", out var availableElement)
                            && availableElement.ValueKind == JsonValueKind.True;

            var balances = new List<ProviderBalanceEntry>();
            if (root.TryGetProperty("balance_infos", out var infos) && infos.ValueKind == JsonValueKind.Array)
            {
                foreach (var info in infos.EnumerateArray())
                {
                    balances.Add(new ProviderBalanceEntry(
                        ReadString(info, "currency") ?? "?",
                        ReadString(info, "total_balance") ?? "?",
                        ReadString(info, "granted_balance") ?? "?",
                        ReadString(info, "topped_up_balance") ?? "?"));
                }
            }

            return new ProviderBalanceSnapshot(
                endpoint.ProviderId,
                host,
                available,
                balances,
                DateTimeOffset.UtcNow.ToString("O"));
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"余额接口返回的不是预期 JSON：{exception.Message}");
        }
    }

    /// <summary>回执序列化。<b>只含 providerId / host / 余额</b>，永不含凭据。</summary>
    public static string ToJson(ProviderBalanceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return JsonSerializer.Serialize(new
        {
            providerId = snapshot.ProviderId,
            host = snapshot.Host,
            isAvailable = snapshot.IsAvailable,
            balances = snapshot.Balances.Select(b => new
            {
                currency = b.Currency,
                totalBalance = b.TotalBalance,
                grantedBalance = b.GrantedBalance,
                toppedUpBalance = b.ToppedUpBalance,
            }).ToList(),
            queriedAtUtc = snapshot.QueriedAtUtc,
            note = snapshot.Balances.Count == 0
                ? "余额接口未返回 balance_infos —— 该 provider 可能不支持 /user/balance。"
                : "余额是时点快照；赠送余额与充值余额分开计。",
        });
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

/// <summary>余额查询参数。</summary>
public sealed record ProviderBalanceArgs
{
    /// <summary>providerId（缺省 deepseek）。</summary>
    public string? ProviderId { get; init; }
}

/// <summary>解析出的 provider 端点。<b>含凭据</b>，只在进程内使用，不得序列化。</summary>
public sealed record ProviderBalanceEndpoint(string ProviderId, string BaseUrl, string ApiKey, string ApiKeySource);

/// <summary>单币种余额。</summary>
public sealed record ProviderBalanceEntry(
    string Currency,
    string TotalBalance,
    string GrantedBalance,
    string ToppedUpBalance);

/// <summary>余额快照（<b>不含凭据</b>）。</summary>
public sealed record ProviderBalanceSnapshot(
    string ProviderId,
    string Host,
    bool IsAvailable,
    IReadOnlyList<ProviderBalanceEntry> Balances,
    string QueriedAtUtc);
