namespace PuddingDeepSeekWeb;

/// <summary>
/// Search 请求（POST /v1/tools/search）。字段语义与 DeepSeek 官方文档一致：
/// query 1–128 个 Unicode 字符且不能全为空白；count 1–50（默认 10）；
/// ori_question 1–128 字符（可选）；localization 为用于本地化的 IP 地址（可选）；
/// need_content=false 时响应 content 恒为空串。
/// </summary>
public sealed record DeepSeekSearchRequest
{
    public required string Query { get; init; }

    /// <summary>返回条数，1–50；null = API 默认 10。</summary>
    public int? Count { get; init; }

    /// <summary>触发搜索的用户原始问题（1–128 字符），帮助理解搜索意图。</summary>
    public string? OriQuestion { get; init; }

    /// <summary>用于优先返回本地化搜索结果的 IP 地址。</summary>
    public string? Localization { get; init; }

    /// <summary>是否在 content 中返回网页正文。</summary>
    public bool NeedContent { get; init; }
}

/// <summary>Search 的单条结果，字段与官方响应一一对应。</summary>
public sealed record DeepSeekSearchResult
{
    /// <summary>排序分数 0–3；≥1.5 为优质结果。</summary>
    public double RerankScore { get; init; }

    public string Title { get; init; } = string.Empty;

    public string Url { get; init; } = string.Empty;

    /// <summary>短摘要，通常 100–200 字符。</summary>
    public string Snippet { get; init; } = string.Empty;

    /// <summary>长摘要，通常 600–800 字符，适合给模型阅读。</summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>网页正文；need_content=false 或正文缺失时为空串。</summary>
    public string Content { get; init; } = string.Empty;

    public string? SiteName { get; init; }

    public string? SiteIcon { get; init; }

    /// <summary>发布时间（UTC RFC 3339 原文）；未知时为 null。</summary>
    public string? Date { get; init; }

    /// <summary>网页权威性 0–4；-1 表示未知；≥3 为极权威结果。</summary>
    public int Authority { get; init; } = -1;
}

/// <summary>Search 成功响应；无匹配结果时 Results 为空数组。</summary>
public sealed record DeepSeekSearchResponse
{
    public IReadOnlyList<DeepSeekSearchResult> Results { get; init; } = [];
}

/// <summary>Fetch 请求（POST /v1/tools/fetch）。</summary>
public sealed record DeepSeekFetchRequest
{
    /// <summary>要抓取的网页地址：≤8192 字符、含 host 的 HTTP/HTTPS URL、不得带用户名或密码。</summary>
    public required string Url { get; init; }
}

/// <summary>Fetch 成功响应：页面标题与 Markdown 正文（缺失时为空串）。</summary>
public sealed record DeepSeekFetchResponse
{
    public string Title { get; init; } = string.Empty;

    public string Content { get; init; } = string.Empty;
}
