using System.Text;
using PuddingCode.Models;
using PuddingCode.Tools;
using PuddingDeepSeekWeb;
using PuddingRuntime.Services.DeepSeekWeb;

namespace PuddingRuntime.Services.Tools;

/// <summary>
/// DeepSeek 官方单页抓取（POST /v1/tools/fetch）：返回页面标题与 Markdown 正文。
/// 低风险只读网络工具（AutoAllowed 免运行时授权）；凭据解析与错误分类边界同 <see cref="DeepSeekSearchTool"/>。
/// </summary>
[Tool(
    id: "deepseek_fetch",
    name: "DeepSeek Fetch",
    description: "通过 DeepSeek 官方 Fetch API 抓取单个公开网页，返回页面标题与清洗后的 Markdown 正文。"
        + "【何时用】已有一个具体 URL（通常来自 deepseek_search / anysearch_search / GitHub 搜索等），需要读取该页完整正文做核实、摘录或总结时使用；"
        + "比 http_fetch 更克制：由 DeepSeek 负责抓取与正文清洗，直接得到可读 Markdown。"
        + "【怎么用】只传 url（必需；必须是 http/https 绝对地址、≤8192 字符、不能带用户名/密码）。"
        + "【坑】需先配置 DeepSeek 凭据（search.providers.json 的 deepseek_search.apiKey，或 llm.providers.json 的 providerId=\"deepseek\"），否则工具直接失败；"
        + "403 表示被 DeepSeek 安全策略拒绝、404 表示无法取得可用正文（页面不存在/被站点拦截/无可提取正文），应换 URL 或改用 http_fetch；"
        + "正文超过 60000 字符会被有界截断（会注明 truncated）。"
        + " English: Fetch one public URL through the DeepSeek Fetch API and return the page title plus cleaned Markdown body. "
        + "Requires DeepSeek credentials in data config; url must be an absolute http(s) URL without embedded credentials; "
        + "403/404 are actionable (security policy/no usable content) and long bodies are truncated at 60000 chars.",
    category: ToolCategory.Network,
    permission: ToolPermissionLevel.Low,
    safety: ToolSafetyFlags.ReadOnly | ToolSafetyFlags.ConcurrencySafe | ToolSafetyFlags.RequiresNetwork,
    SortOrder = 57)]
public sealed class DeepSeekFetchTool : PuddingToolBase<DeepSeekFetchArgs>
{
    private readonly IDeepSeekWebService _service;
    private readonly DeepSeekWebSettingsProvider _settingsProvider;
    private readonly ILogger<DeepSeekFetchTool> _logger;

    public DeepSeekFetchTool(
        IDeepSeekWebService service,
        DeepSeekWebSettingsProvider settingsProvider,
        ILogger<DeepSeekFetchTool> logger)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _settingsProvider = settingsProvider ?? throw new ArgumentNullException(nameof(settingsProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        DeepSeekFetchArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        // 1) 凭据：未配置时给出可操作错误（含两个配置位置），绝不发请求。
        if (!_settingsProvider.TryResolve(out var settings, out var settingsError))
            return ToolExecutionResult.Fail(settingsError);

        // 2) URL 校验（组件对 url 是严格校验；工具侧先给出更具体的可操作错误）。
        var url = args.Url?.Trim() ?? string.Empty;
        var validationError = ValidateUrl(url);
        if (validationError is not null)
            return ToolExecutionResult.Fail(validationError);

        var uri = new Uri(url);

        // 不记录完整 URL（可能含查询串隐私），只记 host/length。
        _logger.LogInformation(
            "[DeepSeekFetchTool] agent={Agent} host={Host} urlLength={UrlLength} source={Source}",
            context.AgentInstanceId,
            uri.Host,
            url.Length,
            settings.Source);

        DeepSeekWebResult<DeepSeekFetchResponse> result;
        try
        {
            result = await _service
                .FetchAsync(settings.Connection, new DeepSeekFetchRequest { Url = url }, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex, "[DeepSeekFetchTool] fetch request failed agent={Agent}", context.AgentInstanceId);
            return ToolExecutionResult.Fail($"DeepSeek Fetch request failed: {ex.Message}");
        }

        if (!result.IsSuccess || result.Value is null)
            return DeepSeekWebToolErrors.ToToolFailure(result.Error, "DeepSeek Fetch");

        return ToolExecutionResult.Ok(RenderFetchResponse(url, result.Value));
    }

    private static string RenderFetchResponse(string url, DeepSeekFetchResponse response)
    {
        var title = DeepSeekWebToolOutput.DisplayOrPlaceholder(response.Title, "(untitled)");
        var content = response.Content ?? string.Empty;

        var sb = new StringBuilder();
        sb.Append("DeepSeek Fetch: ").AppendLine(title);
        sb.Append("URL: ").Append(url);

        if (string.IsNullOrWhiteSpace(content))
        {
            sb.AppendLine();
            sb.AppendLine();
            sb.Append("(no content returned)");
        }
        else
        {
            sb.AppendLine();
            sb.AppendLine();
            sb.Append(content.Trim());
        }

        return DeepSeekWebToolOutput.Bound(sb.ToString().TrimEnd());
    }

    /// <summary>校验 url 参数；合法返回 null，否则返回 Agent 可操作错误消息。</summary>
    internal static string? ValidateUrl(string url)
    {
        if (url.Length == 0)
            return "Url is required.";
        if (url.Length > DeepSeekFetchLimits.MaxUrlChars)
            return $"Url must be at most {DeepSeekFetchLimits.MaxUrlChars} characters.";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return "Url must be an absolute http(s) URL.";
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return "Url must use the http or https scheme.";
        if (string.IsNullOrEmpty(uri.Host))
            return "Url must include a host.";
        if (!string.IsNullOrEmpty(uri.UserInfo))
            return "Url must not contain a username or password.";

        return null;
    }
}

/// <summary>deepseek_fetch 的参数边界（与 DeepSeek 官方文档一致）。</summary>
internal static class DeepSeekFetchLimits
{
    internal const int MaxUrlChars = 8_192;
}

/// <summary>deepseek_fetch 的 Agent 可见参数（参数名自动转 snake_case：url）。</summary>
public sealed record DeepSeekFetchArgs
{
    [ToolParam("Required absolute http/https URL to fetch (max 8192 characters, no embedded username/password).")]
    public required string Url { get; init; }
}
