using System.Globalization;
using System.Text;
using PuddingCode.Models;
using PuddingCode.Tools;
using PuddingDeepSeekWeb;
using PuddingRuntime.Services.DeepSeekWeb;

namespace PuddingRuntime.Services.Tools;

/// <summary>
/// DeepSeek 官方网页搜索（POST /v1/tools/search）：返回标题、URL、摘要、长摘要与可选正文。
/// 低风险只读网络工具（AutoAllowed 免运行时授权）；凭据由 <see cref="DeepSeekWebSettingsProvider"/> 解析，
/// 协议语义（校验/错误分类/重试）在 <c>PuddingDeepSeekWeb</c> 组件内。
/// </summary>
[Tool(
    id: "deepseek_search",
    name: "DeepSeek Search",
    description: "通过 DeepSeek 官方 Search API 搜索公开网页，返回排序后的标题、URL、摘要、长摘要与可选正文。"
        + "【何时用】需要查证最新公开网页信息（新闻、文档、产品页、社区讨论、技术资料），且希望直接用权威度/重排分判断可信来源时使用；"
        + "适合中英文混合检索，也可作为 anysearch_search / doubao_search 的交叉验证来源。"
        + "【怎么用】必传 query（1-128 字符）；count 控制条数（1-50，默认 10，越界自动夹取）；need_content=true 时额外返回每条网页的 Markdown 正文（结果更大更慢）；"
        + "ori_question 可传用户原始问题帮助 API 理解搜索意图；localization 可传 IP 地址以本地化结果。"
        + "【坑】需先在 <DataRoot>/config/search.providers.json 配置 deepseek_search.apiKey（或 llm.providers.json 中 providerId=\"deepseek\" 的 apiKey），"
        + "未配置或 provider 被禁用时工具直接失败且不会发起请求；搜索返回的是摘要级内容，关键事实必须再用 deepseek_fetch 或 http_fetch 打开原文核实；"
        + "结果里的 Rerank 分 ≥1.5 才代表高相关（文档口径）、Authority ≥3 为极权威；Rerank 明显偏低（如 <1.0）说明该查询可能没有匹配的公开实体，应视同“未检索到”，换关键词或加 site: 限定后再试；"
        + "【成本与限速】Search 按请求次数计费（官方价：每 1000 次成功请求 5 元；只有 HTTP 200 且 results 非空才计费），count 只影响条数、不影响单价 ⇒ 需要多条时一次取够，不要拆成多次调用；"
        + "账号级 QPS 仅 5（Search 与 Fetch 各自独立），并发猛刷会收到 HTTP 429，遇 408/429/5xx 应等待后重试，其余 4xx 不要重试；"
        + "count>50 会被夹到 50，输出超过 60000 字符会被有界截断（会注明 truncated）。"
        + " English: Search the public web through the DeepSeek Search API; returns ranked results "
        + "(title/url/snippet/summary/optional content). Requires DeepSeek credentials in data config, "
        + "count is 1-50 (default 10), need_content toggles page bodies, and returned facts should be verified with deepseek_fetch/http_fetch. "
        + "Treat clearly low Rerank scores (<1.0) as weak or no match rather than evidence. "
        + "Billed per successful request (not per result) and rate-limited to 5 QPS per account: fetch more results per call instead of fanning out concurrent searches; retry 408/429/5xx, do not retry other 4xx.",
    category: ToolCategory.Network,
    permission: ToolPermissionLevel.Low,
    safety: ToolSafetyFlags.ReadOnly | ToolSafetyFlags.ConcurrencySafe | ToolSafetyFlags.RequiresNetwork,
    SortOrder = 56)]
public sealed class DeepSeekSearchTool : PuddingToolBase<DeepSeekSearchArgs>
{
    private readonly IDeepSeekWebService _service;
    private readonly DeepSeekWebSettingsProvider _settingsProvider;
    private readonly ILogger<DeepSeekSearchTool> _logger;

    public DeepSeekSearchTool(
        IDeepSeekWebService service,
        DeepSeekWebSettingsProvider settingsProvider,
        ILogger<DeepSeekSearchTool> logger)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _settingsProvider = settingsProvider ?? throw new ArgumentNullException(nameof(settingsProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        DeepSeekSearchArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        // 1) 凭据：未配置时给出可操作错误（含两个配置位置），绝不发请求。
        if (!_settingsProvider.TryResolve(out var settings, out var settingsError))
            return ToolExecutionResult.Fail(settingsError);

        // 2) 参数校验。组件对 query 1–128 字符是严格校验，这里先挡住空查询。
        var query = args.Query?.Trim() ?? string.Empty;
        if (query.Length == 0)
            return ToolExecutionResult.Fail("Query is required.");
        if (query.Length > DeepSeekSearchLimits.MaxQueryChars)
        {
            return ToolExecutionResult.Fail(
                $"Query must be at most {DeepSeekSearchLimits.MaxQueryChars} characters.");
        }

        // 3) 本地 clamp（组件是严格校验：超界会被拒绝，而不是夹取）。
        var count = Math.Clamp(
            args.Count ?? DeepSeekSearchLimits.DefaultCount,
            DeepSeekSearchLimits.MinCount,
            DeepSeekSearchLimits.MaxCount);
        var needContent = args.NeedContent ?? false;

        var request = new DeepSeekSearchRequest
        {
            Query = query,
            Count = count,
            NeedContent = needContent,
            OriQuestion = DeepSeekWebToolOutput.NormalizeOptional(args.OriQuestion),
            Localization = DeepSeekWebToolOutput.NormalizeOptional(args.Localization),
        };

        _logger.LogInformation(
            "[DeepSeekSearchTool] agent={Agent} queryLength={QueryLength} count={Count} needContent={NeedContent} source={Source}",
            context.AgentInstanceId,
            query.Length,
            count,
            needContent,
            settings.Source);

        DeepSeekWebResult<DeepSeekSearchResponse> result;
        try
        {
            result = await _service.SearchAsync(settings.Connection, request, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex, "[DeepSeekSearchTool] search request failed agent={Agent}", context.AgentInstanceId);
            return ToolExecutionResult.Fail($"DeepSeek Search request failed: {ex.Message}");
        }

        if (!result.IsSuccess || result.Value is null)
            return DeepSeekWebToolErrors.ToToolFailure(result.Error, "DeepSeek Search");

        return ToolExecutionResult.Ok(RenderSearchResponse(query, result.Value));
    }

    private static string RenderSearchResponse(string query, DeepSeekSearchResponse response)
    {
        var results = response.Results ?? [];
        var sb = new StringBuilder();
        sb.Append("DeepSeek Search results for: \"")
            .Append(query)
            .Append("\" (")
            .Append(results.Count)
            .AppendLine(" results)");

        if (results.Count == 0)
        {
            sb.AppendLine();
            sb.Append("(no results)");
            return DeepSeekWebToolOutput.Bound(sb.ToString());
        }

        for (var i = 0; i < results.Count; i++)
        {
            var item = results[i];
            sb.AppendLine();
            sb.AppendLine($"{i + 1}. {DeepSeekWebToolOutput.DisplayOrPlaceholder(item.Title, "(untitled)")}");

            if (!string.IsNullOrWhiteSpace(item.Url))
                sb.AppendLine($"URL: {item.Url.Trim()}");
            if (!string.IsNullOrWhiteSpace(item.Date))
                sb.AppendLine($"Date: {item.Date.Trim()}");
            if (!string.IsNullOrWhiteSpace(item.SiteName))
                sb.AppendLine($"Site: {item.SiteName.Trim()}");
            if (item.Authority >= 0)
                sb.AppendLine($"Authority: {item.Authority}");
            if (item.RerankScore > 0)
            {
                sb.AppendLine(
                    "Rerank: " + item.RerankScore.ToString("0.###", CultureInfo.InvariantCulture));
            }

            if (!string.IsNullOrWhiteSpace(item.Snippet))
                sb.AppendLine($"Snippet: {item.Snippet.Trim()}");
            if (!string.IsNullOrWhiteSpace(item.Summary))
                sb.AppendLine($"Summary: {item.Summary.Trim()}");
            // need_content=false 时组件回空串；为兼容"上游仍带正文"的情况，正文非空也展示（有界截断）。
            if (!string.IsNullOrWhiteSpace(item.Content))
            {
                sb.AppendLine(
                    $"Content: {DeepSeekWebToolOutput.Truncate(item.Content.Trim(), DeepSeekSearchLimits.MaxContentCharsPerResult)}");
            }
        }

        return DeepSeekWebToolOutput.Bound(sb.ToString().TrimEnd());
    }
}

/// <summary>deepseek_search 的参数边界（与 DeepSeek 官方文档一致；组件严格校验，工具侧先行夹取/拦截）。</summary>
internal static class DeepSeekSearchLimits
{
    internal const int MaxQueryChars = 128;
    internal const int DefaultCount = 10;
    internal const int MinCount = 1;
    internal const int MaxCount = 50;

    /// <summary>单条结果正文在工具输出中的最大字符数（避免单条吞掉全部预算）。</summary>
    internal const int MaxContentCharsPerResult = 8_000;

    /// <summary>工具输出总上限（字符）。超出即有界截断并注明 truncated。</summary>
    internal const int MaxRenderedChars = 60_000;
}

/// <summary>两个 DeepSeek 工具共享的输出裁剪/格式化小工具。</summary>
internal static class DeepSeekWebToolOutput
{
    internal static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    internal static string DisplayOrPlaceholder(string? value, string placeholder) =>
        string.IsNullOrWhiteSpace(value) ? placeholder : value.Trim();

    internal static string Truncate(string? value, int maxChars)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxChars)
            return value ?? string.Empty;

        return value[..maxChars] + $"... (truncated at {maxChars} chars)";
    }

    /// <summary>
    /// 把渲染文本压到 <see cref="DeepSeekSearchLimits.MaxRenderedChars"/> 以内；
    /// 超限时保留截断标记本身，保证最终长度（含标记）不超过上限。
    /// </summary>
    internal static string Bound(string text)
    {
        if (text.Length <= DeepSeekSearchLimits.MaxRenderedChars)
            return text;

        var suffix = $"\n... (truncated at {DeepSeekSearchLimits.MaxRenderedChars} chars)";
        var bodyLength = DeepSeekSearchLimits.MaxRenderedChars - suffix.Length;
        return text[..bodyLength] + suffix;
    }
}

/// <summary>
/// 组件 <see cref="DeepSeekWebErrorKind"/> → Agent 可操作错误消息的映射（两个工具共用）。
/// 保留 HTTP 状态码：既写进文本（便于模型理解），也作为 <see cref="ToolExecutionResult.ExitCode"/> 透出。
/// </summary>
internal static class DeepSeekWebToolErrors
{
    internal static ToolExecutionResult ToToolFailure(DeepSeekWebError? error, string operation)
    {
        if (error is null)
            return ToolExecutionResult.Fail($"{operation} failed: no result was returned by the DeepSeek web service.");

        var statusSuffix = error.HttpStatusCode is null ? string.Empty : $" (HTTP {error.HttpStatusCode})";
        var hint = error.Kind switch
        {
            DeepSeekWebErrorKind.InvalidRequest =>
                "The request was rejected as invalid. Verify the tool arguments (query/count/need_content for search, url for fetch).",
            DeepSeekWebErrorKind.Unauthorized =>
                "DeepSeek rejected the API key (missing, invalid, or not allowed). Check deepseek_search.apiKey in search.providers.json "
                + "or the apiKey of providerId=\"deepseek\" in llm.providers.json.",
            DeepSeekWebErrorKind.InsufficientBalance =>
                "The DeepSeek account balance is insufficient. Top up the account before retrying.",
            DeepSeekWebErrorKind.Forbidden =>
                "DeepSeek Fetch was rejected by the provider's security policy. Choose a different public URL.",
            DeepSeekWebErrorKind.NotFound =>
                "DeepSeek Fetch could not obtain usable page content: the URL may be unavailable, blocked by the site, "
                + "or return no extractable Markdown body. Try another URL or use http_fetch.",
            DeepSeekWebErrorKind.RequestTimeout =>
                "The DeepSeek request timed out. Retry later.",
            DeepSeekWebErrorKind.PayloadTooLarge =>
                "The request exceeded DeepSeek's payload limit. Reduce the query size or use a shorter URL.",
            DeepSeekWebErrorKind.RateLimited =>
                "DeepSeek rate limit exceeded (Search/Fetch QPS). Wait a moment and retry.",
            DeepSeekWebErrorKind.ServerError =>
                "DeepSeek reported a server-side error. Retry later.",
            DeepSeekWebErrorKind.InvalidResponse =>
                "DeepSeek returned a response that could not be parsed as the expected JSON payload.",
            DeepSeekWebErrorKind.Transport =>
                "Could not reach the DeepSeek API (network/proxy/TLS failure). Check connectivity and retry.",
            _ => "The DeepSeek web request failed.",
        };

        var upstream = DeepSeekWebToolOutput.Truncate(error.Message, 600);
        var message = string.IsNullOrWhiteSpace(upstream)
            ? $"{operation} failed{statusSuffix}: {hint}"
            : $"{operation} failed{statusSuffix}: {hint} Upstream message: {upstream}";

        return ToolExecutionResult.Fail(message, error.HttpStatusCode ?? 1);
    }
}

/// <summary>deepseek_search 的 Agent 可见参数（参数名自动转 snake_case：query/count/need_content/ori_question/localization）。</summary>
public sealed record DeepSeekSearchArgs
{
    [ToolParam("Required search query (1-128 characters). Describe the exact information to find.")]
    public required string Query { get; init; }

    [ToolParam("Number of results to return, 1-50. Default: 10. Values outside the range are clamped.")]
    public int? Count { get; init; }

    [ToolParam("When true, also return each page's Markdown content. Default: false. Results become larger and slower.")]
    public bool? NeedContent { get; init; }

    [ToolParam("Optional original user question (1-128 characters) that motivated this search; helps the API infer search intent.")]
    public string? OriQuestion { get; init; }

    [ToolParam("Optional IP address used by DeepSeek to localize results, for example the user's public IP.")]
    public string? Localization { get; init; }
}
