using System.Text.Json;
using Microsoft.Extensions.Logging;
using PuddingCode.Configuration;

namespace PuddingPlatform.Services.Security;

/// <summary>
/// ADR-075: External Task API 运行策略提供者。
/// 读取 &lt;DataRoot&gt;/config/system.json 的 ExternalTaskApi 节（缺失=默认值，Enabled=false）；
/// 带 30 秒缓存，修改配置无需重启 Core。显式越界值由 Validate 报告，Host 启动期抛配置错误，
/// 不静默回默认。Secret 永不出现在此配置中。
/// </summary>
public sealed class ExternalTaskApiOptionsProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly string _configFilePath;
    private readonly ILogger<ExternalTaskApiOptionsProvider>? _logger;
    private readonly object _gate = new();
    private PuddingExternalTaskApiConfig _cached = new();
    private DateTimeOffset _cachedAt;
    private TimeSpan _cacheTtl = TimeSpan.FromSeconds(30);

    public ExternalTaskApiOptionsProvider(
        PuddingDataPaths dataPaths,
        ILogger<ExternalTaskApiOptionsProvider>? logger = null)
    {
        _configFilePath = dataPaths.SystemConfigFile("system.json");
        _logger = logger;
    }

    public PuddingExternalTaskApiConfig Current
    {
        get
        {
            lock (_gate)
            {
                if (TimeProvider.System.GetUtcNow() - _cachedAt < _cacheTtl)
                    return _cached;

                _cached = ReadFromFile() ?? new PuddingExternalTaskApiConfig();
                _cachedAt = TimeProvider.System.GetUtcNow();
                return _cached;
            }
        }
    }

    /// <summary>启动期显式校验：返回错误列表；空列表=通过。</summary>
    public static List<string> Validate(PuddingExternalTaskApiConfig config)
    {
        var errors = new List<string>();

        if (config.DefaultTokenLifetimeDays < 1 || config.DefaultTokenLifetimeDays > config.MaxTokenLifetimeDays)
            errors.Add($"ExternalTaskApi.DefaultTokenLifetimeDays ({config.DefaultTokenLifetimeDays}) 必须在 1..MaxTokenLifetimeDays 之间。");

        if (config.MaxTokenLifetimeDays is < 1 or > 365)
            errors.Add($"ExternalTaskApi.MaxTokenLifetimeDays ({config.MaxTokenLifetimeDays}) 必须在 1..365 之间（V1 冻结上限 365 天，不允许永不过期）。");

        if (config.MaxActiveTokensPerOwner is < 1 or > 100)
            errors.Add($"ExternalTaskApi.MaxActiveTokensPerOwner ({config.MaxActiveTokensPerOwner}) 必须在 1..100 之间。");

        if (config.RequestsPerMinutePerToken is < 1 or > 10_000)
            errors.Add($"ExternalTaskApi.RequestsPerMinutePerToken ({config.RequestsPerMinutePerToken}) 必须在 1..10000 之间。");

        if (config.MutationConcurrencyPerToken is < 1 or > 64)
            errors.Add($"ExternalTaskApi.MutationConcurrencyPerToken ({config.MutationConcurrencyPerToken}) 必须在 1..64 之间。");

        if (config.SseConnectionsPerToken is < 1 or > 16)
            errors.Add($"ExternalTaskApi.SseConnectionsPerToken ({config.SseConnectionsPerToken}) 必须在 1..16 之间。");

        if (config.IdempotencyRetentionDays is < 1 or > 90)
            errors.Add($"ExternalTaskApi.IdempotencyRetentionDays ({config.IdempotencyRetentionDays}) 必须在 1..90 之间。");

        if (!string.IsNullOrWhiteSpace(config.PublicBaseUrl))
        {
            if (!Uri.TryCreate(config.PublicBaseUrl, UriKind.Absolute, out var baseUri))
            {
                errors.Add("ExternalTaskApi.PublicBaseUrl 必须是 absolute URL。");
            }
            else
            {
                var hasExtraSegments = baseUri.AbsolutePath.TrimEnd('/') is { Length: > 0 }
                    || !string.IsNullOrEmpty(baseUri.Query)
                    || !string.IsNullOrEmpty(baseUri.Fragment);
                if (hasExtraSegments)
                    errors.Add("ExternalTaskApi.PublicBaseUrl 不能包含 path/query/fragment。");

                var isLoopbackHost = baseUri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                    || baseUri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
                    || baseUri.Host.Equals("::1", StringComparison.OrdinalIgnoreCase);
                if (config.Enabled && config.RequireHttps && baseUri.Scheme != "https" && !isLoopbackHost)
                    errors.Add("ExternalTaskApi.Enabled=true 且非 Loopback 时 PublicBaseUrl 必须是 HTTPS。");
            }
        }

        return errors;
    }

    private PuddingExternalTaskApiConfig? ReadFromFile()
    {
        try
        {
            if (!File.Exists(_configFilePath))
                return null;

            var json = File.ReadAllText(_configFilePath);
            var systemConfig = JsonSerializer.Deserialize<PuddingSystemConfig>(json, JsonOptions);
            return systemConfig?.ExternalTaskApi;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[ExternalTaskApiOptions] 读取 {Path} 失败，沿用默认值", _configFilePath);
            return null;
        }
    }
}
