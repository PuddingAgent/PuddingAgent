namespace PuddingDeepSeekWeb;

/// <summary>
/// DeepSeek Search / Fetch 调用所需的连接参数。凭据由宿主从配置解析后逐次传入，
/// 组件本身不读取配置、不持有长期凭据、不回显 API Key。
/// </summary>
public sealed record DeepSeekWebConnection
{
    /// <summary>官方 API Base URL（不含尾部斜杠）。</summary>
    public const string DefaultBaseUrl = "https://api.deepseek.com";

    /// <summary>DeepSeek API Key（Bearer 鉴权）。不得写入日志与错误消息。</summary>
    public required string ApiKey { get; init; }

    /// <summary>API Base URL；默认官方端点。组件负责去尾部斜杠并校验协议。</summary>
    public string BaseUrl { get; init; } = DefaultBaseUrl;

    /// <summary>单次 HTTP 请求超时秒数（1–120）。</summary>
    public int TimeoutSeconds { get; init; } = 30;
}

/// <summary>DeepSeek Search / Fetch 端点常量与 URL 拼装。</summary>
public static class DeepSeekWebEndpoints
{
    /// <summary>POST /v1/tools/search — 按 query 搜索网页。</summary>
    public const string SearchPath = "/v1/tools/search";

    /// <summary>POST /v1/tools/fetch — 抓取单个网页并返回 Markdown 正文。</summary>
    public const string FetchPath = "/v1/tools/fetch";

    /// <summary>
    /// 把 baseUrl 与端点路径拼成绝对 URL。baseUrl 为空白时回退官方端点；
    /// 非 http/https 绝对地址返回 null（由调用方转成配置错误）。
    /// </summary>
    public static string? BuildUrl(string? baseUrl, string path)
    {
        var normalized = NormalizeBaseUrl(baseUrl);
        return normalized is null ? null : normalized + path;
    }

    /// <summary>
    /// 规范化 Base URL：去空白与尾部斜杠。空白 ⇒ 官方端点；
    /// 非 http/https、缺 host 或带用户信息 ⇒ null（不接受）。
    /// </summary>
    public static string? NormalizeBaseUrl(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            return DeepSeekWebConnection.DefaultBaseUrl;

        var value = baseUrl.Trim().TrimEnd('/');
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return null;
        if (string.IsNullOrEmpty(uri.Host))
            return null;
        if (!string.IsNullOrEmpty(uri.UserInfo))
            return null;

        return value;
    }
}
