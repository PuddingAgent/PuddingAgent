using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using PuddingCode.Configuration;
using PuddingDeepSeekWeb;

namespace PuddingRuntime.Services.DeepSeekWeb;

/// <summary>
/// 解析后的 DeepSeek Search / Fetch 连接设置。
/// <para><see cref="Source"/> 是设置来源的稳定标识（<c>search.providers.json</c> 或 <c>llm.providers.json</c>），
/// 用于日志与诊断；不包含任何凭据。</para>
/// </summary>
internal sealed record DeepSeekWebSettings(DeepSeekWebConnection Connection, string Source);

/// <summary>
/// Runtime 侧的 DeepSeek Search / Fetch 凭据解析器。组件（<c>PuddingDeepSeekWeb</c>）本身不读配置，
/// 由本类负责从 data 目录的两处配置解析出 <see cref="DeepSeekWebConnection"/>：
/// <list type="number">
/// <item><c>&lt;DataRoot&gt;/config/search.providers.json</c> 的 <c>deepseek_search</c> 节
///       （enabled / baseUrl / apiKey / timeoutSeconds）—— 显式门户优先；</item>
/// <item>回退到 <c>&lt;DataRoot&gt;/config/llm.providers.json</c> 中 <c>providerId</c> 忽略大小写等于
///       <c>deepseek</c> 的 provider（baseUrl + apiKey；apiKey 支持 <c>${ENV_VAR}</c> 占位）；
///       <c>isEnabled=false</c> 视为不可用；<c>apiKeyRef</c>（KeyVault）在 Runtime 工具链内无法解析，视为未配置。</item>
/// </list>
/// <para>
/// 错误一律以返回值表达（不抛异常）：文件读取失败的错误带绝对路径；「未配置」错误同时给出两个配置位置，
/// 且<b>绝不回显 API Key</b>。关闭（disabled）是终态，不回退到另一处配置。
/// </para>
/// <para>
/// 可见性说明：类型本身 <c>public</c> 仅因为两个 <c>public</c> 工具类的构造参数需要它（C# 不允许 public
/// 构造暴露 internal 参数类型）。成员 <see cref="TryResolve"/> 与 <see cref="DeepSeekWebSettings"/> 仍是
/// <c>internal</c>：这是 Runtime 内部的配置解析细节，不构成对外契约。
/// </para>
/// </summary>
public sealed partial class DeepSeekWebSettingsProvider
{
    /// <summary>search.providers.json 中的 DeepSeek Search/Fetch 节名。</summary>
    internal const string SearchProviderSectionName = "deepseek_search";

    /// <summary>llm.providers.json 中 DeepSeek provider 的 providerId（忽略大小写比较）。</summary>
    internal const string LlmProviderId = "deepseek";

    /// <summary>search.providers.json 来源标识。</summary>
    internal const string SearchProvidersSource = "search.providers.json";

    /// <summary>llm.providers.json 来源标识。</summary>
    internal const string LlmProvidersSource = "llm.providers.json";

    internal const string SearchDisabledError =
        "DeepSeek Search/Fetch provider is disabled in search.providers.json.";

    internal const string LlmProviderDisabledError =
        "The deepseek provider is disabled in llm.providers.json.";

    private const int DefaultTimeoutSeconds = 30;
    private const int MinTimeoutSeconds = 1;
    private const int MaxTimeoutSeconds = 120;

    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly PuddingDataPaths _paths;
    private readonly ILogger<DeepSeekWebSettingsProvider>? _logger;

    public DeepSeekWebSettingsProvider(
        PuddingDataPaths paths,
        ILogger<DeepSeekWebSettingsProvider>? logger = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _logger = logger;
    }

    /// <summary><c>&lt;DataRoot&gt;/config/search.providers.json</c> 的绝对路径。</summary>
    internal string SearchProvidersPath => _paths.SystemConfigFile("search.providers.json");

    /// <summary><c>&lt;DataRoot&gt;/config/llm.providers.json</c> 的绝对路径。</summary>
    internal string LlmProvidersPath => _paths.SystemConfigFile("llm.providers.json");

    /// <summary>
    /// 按优先级 <c>search.providers.json → llm.providers.json</c> 解析连接参数。
    /// 成功返回 true；失败返回 false 并给出可直接展示给用户的错误（含路径，不含 Key）。
    /// </summary>
    internal bool TryResolve(out DeepSeekWebSettings settings, out string error)
    {
        settings = null!;
        error = string.Empty;

        // ── (a) search.providers.json 的 deepseek_search 节（显式配置优先） ──
        if (!TryReadConfigFile<DeepSeekSearchProvidersConfigFile>(
                SearchProvidersPath, out var searchConfigFile, out var searchReadError))
        {
            error = searchReadError!;
            return false;
        }

        var searchSection = searchConfigFile?.DeepSeekSearch;
        if (searchSection is not null)
        {
            // 显式关闭是终态：不回退到 llm.providers.json（避免"关掉却仍能联网"）。
            if (!searchSection.Enabled)
            {
                error = SearchDisabledError;
                return false;
            }

            var searchApiKey = searchSection.ApiKey?.Trim() ?? string.Empty;
            if (searchApiKey.Length > 0)
            {
                settings = CreateSettings(
                    searchApiKey,
                    searchSection.BaseUrl,
                    searchSection.TimeoutSeconds ?? DefaultTimeoutSeconds,
                    SearchProvidersSource);
                return true;
            }

            // apiKey 为空 ⇒ 继续回退到 llm.providers.json。
        }

        // ── (b) llm.providers.json 中 providerId == "deepseek" 的 provider ──
        if (!TryReadConfigFile<PuddingLlmProvidersConfig>(
                LlmProvidersPath, out var llmConfigFile, out var llmReadError))
        {
            error = llmReadError!;
            return false;
        }

        var provider = llmConfigFile?.Providers.FirstOrDefault(candidate => string.Equals(
            candidate.ProviderId?.Trim(),
            LlmProviderId,
            StringComparison.OrdinalIgnoreCase));
        if (provider is not null)
        {
            if (!provider.IsEnabled)
            {
                error = LlmProviderDisabledError;
                return false;
            }

            // apiKey 允许 ${ENV_VAR} 占位；环境变量缺失 ⇒ 展开为空 ⇒ 视为未配置（继续走合并错误）。
            var llmApiKey = ExpandEnvironmentPlaceholders(provider.ApiKey ?? string.Empty).Trim();
            if (llmApiKey.Length > 0)
            {
                settings = CreateSettings(
                    llmApiKey,
                    provider.BaseUrl,
                    DefaultTimeoutSeconds,
                    LlmProvidersSource);
                return true;
            }

            // apiKeyRef（KeyVault 引用）无法在 Runtime 工具链内解析；不静默当成 Key 使用。
            if (!string.IsNullOrWhiteSpace(provider.ApiKeyRef))
            {
                _logger?.LogWarning(
                    "[DeepSeekWebSettingsProvider] providerId=deepseek 的凭据是 apiKeyRef（KeyVault 引用），"
                    + "Runtime 工具链无法解析；请改用 apiKey（或 ${{ENV_VAR}} 占位）。");
            }
        }

        // ── (c) 两处都没有可用配置 ⇒ 合并错误（同时给出两个绝对路径，永不回显 Key） ──
        error = BuildNotConfiguredError();
        return false;
    }

    /// <summary>
    /// 默认数据目录解析（与 <c>AnySearchSearchTool.ResolveDefaultDataPaths</c> 同语义）：
    /// 环境变量 <c>PUDDING_DATA_ROOT</c>，否则 <c>AppContext.BaseDirectory/data</c>。
    /// 供 DI 工厂在容器未注册 <see cref="PuddingDataPaths"/> 时回退使用。
    /// </summary>
    internal static PuddingDataPaths ResolveDefaultDataPaths()
    {
        var root = Environment.GetEnvironmentVariable("PUDDING_DATA_ROOT");
        if (string.IsNullOrWhiteSpace(root))
            root = Path.Combine(AppContext.BaseDirectory, "data");

        return PuddingDataPaths.FromRoot(root);
    }

    /// <summary>
    /// 读取并反序列化配置文件。<b>文件不存在是合法状态</b>（返回 true / config=null）；
    /// JSON/IO/权限失败返回 false 并给出带路径的错误（不抛异常）。
    /// </summary>
    private bool TryReadConfigFile<T>(
        string path,
        out T? config,
        out string? error)
        where T : class
    {
        config = null;
        error = null;

        if (!File.Exists(path))
            return true;

        try
        {
            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json))
                return true;

            config = JsonSerializer.Deserialize<T>(json, s_jsonOptions);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(
                ex, "[DeepSeekWebSettingsProvider] failed to read config path={Path}", path);
            error = $"Failed to read DeepSeek Search/Fetch config file '{path}': {ex.Message}";
            return false;
        }
    }

    private static DeepSeekWebSettings CreateSettings(
        string apiKey,
        string? baseUrl,
        int timeoutSeconds,
        string source)
    {
        var normalizedBaseUrl = string.IsNullOrWhiteSpace(baseUrl)
            ? DeepSeekWebConnection.DefaultBaseUrl
            : baseUrl.Trim();

        var timeout = timeoutSeconds <= 0 ? DefaultTimeoutSeconds : timeoutSeconds;
        timeout = Math.Clamp(timeout, MinTimeoutSeconds, MaxTimeoutSeconds);

        return new DeepSeekWebSettings(
            new DeepSeekWebConnection
            {
                ApiKey = apiKey,
                BaseUrl = normalizedBaseUrl,
                TimeoutSeconds = timeout,
            },
            source);
    }

    private string BuildNotConfiguredError()
    {
        // 注意：本消息会进入工具错误返回（UI/模型可见），因此只给位置与字段名，绝不拼接 Key 值。
        return "DeepSeek Search/Fetch is not configured. Configure one of: "
               + $"(1) deepseek_search.apiKey with enabled=true in {SearchProvidersPath}; "
               + $"(2) a provider with providerId=\"{LlmProviderId}\" and a non-empty apiKey "
               + $"(or ${{ENV_VAR}} placeholder) in {LlmProvidersPath}. "
               + "An apiKeyRef (KeyVault reference) cannot be resolved by the tool runtime. "
               + "The API key value is never included in this message.";
    }

    /// <summary>展开 <c>${ENV_NAME}</c> 占位；未定义的环境变量展开为空串（⇒ 视为未配置）。</summary>
    private static string ExpandEnvironmentPlaceholders(string value)
    {
        if (string.IsNullOrEmpty(value) || !value.Contains("${", StringComparison.Ordinal))
            return value;

        return EnvironmentPlaceholderRegex().Replace(
            value,
            match => Environment.GetEnvironmentVariable(match.Groups[1].Value) ?? string.Empty);
    }

    [GeneratedRegex(@"\$\{([A-Za-z0-9_]+)\}")]
    private static partial Regex EnvironmentPlaceholderRegex();
}

/// <summary>search.providers.json 中与 DeepSeek Search/Fetch 相关的顶层结构（其余 provider 节忽略）。</summary>
internal sealed record DeepSeekSearchProvidersConfigFile
{
    [JsonPropertyName(DeepSeekWebSettingsProvider.SearchProviderSectionName)]
    public DeepSeekSearchProviderSection? DeepSeekSearch { get; init; }
}

/// <summary>search.providers.json 的 <c>deepseek_search</c> 节。</summary>
internal sealed record DeepSeekSearchProviderSection
{
    /// <summary>默认启用；显式 false 表示关闭（不回退）。</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>API Base URL；为空 ⇒ 官方端点 <see cref="DeepSeekWebConnection.DefaultBaseUrl"/>。</summary>
    public string? BaseUrl { get; init; }

    /// <summary>DeepSeek API Key（Bearer 鉴权）。不得写入日志。</summary>
    public string? ApiKey { get; init; }

    /// <summary>单次请求超时秒数；默认 30，取值范围 1–120。</summary>
    public int? TimeoutSeconds { get; init; }
}
