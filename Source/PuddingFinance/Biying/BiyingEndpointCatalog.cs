using System.Text.Json;
using System.Text.Json.Serialization;

namespace PuddingFinance.Biying;

/// <summary>
/// 必盈端点注册表（只读）。数据来自 <c>Data/biying.endpoints.json</c>（嵌入资源），
/// 由 <c>Tools/Biying/extract_endpoints.py</c> 从官方文档机械抽取。
///
/// 为什么要注册表而不是让模型拼 URL：必盈有 200 个端点，路径规则是
/// <c>/{组}/{接口}/{代码}/{证书}</c>，其中证书**是路径段而不是查询参数**
/// （用错会返回 <c>{"code":404,"message":"Licence证书不存在","licence":"&lt;代码&gt;"}</c>）。
/// 让模型理解这套规则既费 token 又容易错；把规则固化成数据，模型只报端点 id。
/// </summary>
public sealed class BiyingEndpointCatalog
{
    private const string ResourceName = "PuddingFinance.biying.endpoints.json";

    private readonly Dictionary<string, BiyingEndpoint> _byId;
    private readonly Dictionary<string, BiyingEndpoint> _byPath;

    private BiyingEndpointCatalog(IReadOnlyList<BiyingEndpoint> endpoints, string schema, string generatedAt)
    {
        All = endpoints;
        Schema = schema;
        GeneratedAt = generatedAt;
        // 同一 id 只保留一条；同一路径可能对应多个端点（不同参数形态），保留第一条并在查找时按 id 优先
        _byId = [];
        _byPath = [];
        foreach (var endpoint in endpoints)
        {
            _byId.TryAdd(endpoint.Id, endpoint);
            _byPath.TryAdd(endpoint.Path, endpoint);
        }
    }

    public IReadOnlyList<BiyingEndpoint> All { get; }

    public string Schema { get; }

    public string GeneratedAt { get; }

    public int Count => All.Count;

    /// <summary>从嵌入资源加载。程序集自带注册表，宿主无需分发额外文件。</summary>
    public static BiyingEndpointCatalog LoadEmbedded()
    {
        using var stream = typeof(BiyingEndpointCatalog).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"嵌入资源 {ResourceName} 缺失：请检查 PuddingFinance.csproj 的 EmbeddedResource 配置。");
        return FromStream(stream);
    }

    public static BiyingEndpointCatalog FromStream(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var document = JsonDocument.Parse(stream);
        var payload = document.RootElement.Deserialize<CatalogPayload>(SerializerOptions)
            ?? throw new InvalidOperationException("端点注册表 JSON 反序列化得到 null。");
        return FromPayload(payload);
    }

    /// <summary>从 JSON 文本加载（测试与热更新用）。</summary>
    public static BiyingEndpointCatalog FromJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var payload = JsonSerializer.Deserialize<CatalogPayload>(json, SerializerOptions)
            ?? throw new InvalidOperationException("端点注册表 JSON 反序列化得到 null。");
        return FromPayload(payload);
    }

    /// <summary>按 id（如 <c>313</c>）或按路径（如 <c>hsrl/ssjy</c>）查端点；找不到返回 false。</summary>
    public bool TryGet(string key, out BiyingEndpoint endpoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var normalized = key.Trim().Trim('/');
        return _byId.TryGetValue(normalized, out endpoint!)
            || _byPath.TryGetValue(normalized, out endpoint!);
    }

    /// <summary>按 id/路径取端点；找不到时抛出带候选的异常。</summary>
    public BiyingEndpoint Get(string key)
    {
        if (TryGet(key, out var endpoint))
            return endpoint;
        var candidates = Suggest(key, 5);
        var hint = candidates.Count == 0
            ? "注册表里没有相近的端点"
            : "最接近的候选：" + string.Join("；", candidates.Select(c => $"{c.Id} {c.Name}（{c.Path}）"));
        throw new BiyingEndpointException($"端点「{key}」不在注册表中。{hint}");
    }

    /// <summary>按名称/路径/分类做关键词检索（全量模糊），用于让模型在不记得 id 时自发现。</summary>
    public IReadOnlyList<BiyingEndpoint> Search(string keyword, int limit = 10)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyword);
        var needle = keyword.Trim();
        var matches = All.Where(endpoint =>
                endpoint.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || endpoint.Path.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || endpoint.Category.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || endpoint.SubCategory.Contains(needle, StringComparison.OrdinalIgnoreCase))
            .Take(Math.Max(1, limit))
            .ToList();
        return matches;
    }

    /// <summary>
    /// 近似候选：先按子串，再退回按编辑距离（只对 id 与路径片段做，避免对 200 条做全量相似度）。
    /// 目的是把「模型报了个不存在的端点」变成一次可自纠的回复，而不是一次瞎试。
    /// </summary>
    public IReadOnlyList<BiyingEndpoint> Suggest(string input, int limit = 5)
    {
        if (string.IsNullOrWhiteSpace(input))
            return [];

        var needle = input.Trim();
        var substring = All.Where(endpoint =>
                endpoint.Path.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || endpoint.Name.Contains(needle, StringComparison.OrdinalIgnoreCase))
            .Take(limit)
            .ToList();
        if (substring.Count > 0)
            return substring;

        return All.Where(endpoint => endpoint.Path.Contains(needle.Split('/')[0], StringComparison.OrdinalIgnoreCase))
            .Take(limit)
            .ToList();
    }

    private static BiyingEndpointCatalog FromPayload(CatalogPayload payload)
    {
        var raw = payload.Endpoints ?? [];
        var endpoints = new List<BiyingEndpoint>(raw.Count);
        foreach (var item in raw)
        {
            if (string.IsNullOrWhiteSpace(item.Template))
                throw new InvalidOperationException($"端点 {item.Id} 缺少 template，注册表损坏。");

            endpoints.Add(new BiyingEndpoint(
                Id: item.Id ?? string.Empty,
                Name: item.Name ?? string.Empty,
                Category: item.Category ?? string.Empty,
                SubCategory: item.SubCategory ?? string.Empty,
                Host: item.Host ?? string.Empty,
                Path: item.Path ?? string.Empty,
                Template: item.Template!,
                Query: item.Query ?? [],
                NeedsCode: item.NeedsCode,
                Fields: (item.Fields ?? []).Select(field => new BiyingField(
                    field.Key ?? string.Empty,
                    field.Type ?? string.Empty,
                    field.Description ?? string.Empty)).ToList())
            {
                Segments = SplitSegments(item.Template!),
            });
        }

        return new BiyingEndpointCatalog(endpoints, payload.Schema ?? string.Empty, payload.GeneratedAt ?? string.Empty);
    }

    /// <summary>
    /// 把模板切成路径段并分型。证书位识别是**内容判断**（段内含 licence/license），
    /// 而不是「最后一段」这个位置假设——万一官方把证书挪位，这里不会静默拼错。
    /// </summary>
    internal static IReadOnlyList<BiyingSegment> SplitSegments(string template)
    {
        // 去掉 scheme://host 与 query，只留 tail
        var withoutQuery = template.Split('?')[0];
        var schemeIndex = withoutQuery.IndexOf("://", StringComparison.Ordinal);
        var afterScheme = schemeIndex >= 0 ? withoutQuery[(schemeIndex + 3)..] : withoutQuery;
        var slashIndex = afterScheme.IndexOf('/');
        var tail = slashIndex >= 0 ? afterScheme[slashIndex..] : "/";

        var segments = new List<BiyingSegment>();
        foreach (var raw in tail.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var kind = Classify(raw);
            segments.Add(new BiyingSegment(kind, raw));
        }

        return segments;
    }

    private static BiyingSegmentKind Classify(string segment)
    {
        if (segment.Contains("licence", StringComparison.OrdinalIgnoreCase)
            || segment.Contains("license", StringComparison.OrdinalIgnoreCase))
            return BiyingSegmentKind.Licence;

        // 中文占位符（如「股票代码(如000001)」）与 {stock_code} 形式都算占位符
        var hasCjk = segment.Any(character => character >= '\u4e00' && character <= '\u9fff');
        if (hasCjk || (segment.StartsWith('{') && segment.EndsWith('}')))
            return BiyingSegmentKind.Placeholder;

        return BiyingSegmentKind.Literal;
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private sealed class CatalogPayload
    {
        [JsonPropertyName("schema")] public string? Schema { get; set; }
        [JsonPropertyName("generatedAt")] public string? GeneratedAt { get; set; }
        [JsonPropertyName("endpoints")] public List<EndpointPayload>? Endpoints { get; set; }
    }

    private sealed class EndpointPayload
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("category")] public string? Category { get; set; }
        [JsonPropertyName("subcategory")] public string? SubCategory { get; set; }
        [JsonPropertyName("host")] public string? Host { get; set; }
        [JsonPropertyName("path")] public string? Path { get; set; }
        [JsonPropertyName("template")] public string? Template { get; set; }
        [JsonPropertyName("query")] public List<string>? Query { get; set; }
        [JsonPropertyName("needsCode")] public bool NeedsCode { get; set; }
        [JsonPropertyName("fields")] public List<FieldPayload>? Fields { get; set; }
    }

    private sealed class FieldPayload
    {
        [JsonPropertyName("key")] public string? Key { get; set; }
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("desc")] public string? Description { get; set; }
    }
}
