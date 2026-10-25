using System.Text.Json;
using System.Text.Json.Nodes;

namespace PuddingFinance.Biying;

/// <summary>整形后的结果。体积数字都是实测值，便于在回执里说明"省了多少"。</summary>
public sealed record BiyingShapeResult(
    string EndpointId,
    string EndpointName,
    string Json,
    int TotalRows,
    int ReturnedRows,
    bool Truncated,
    string? DateKey,
    int RawChars,
    int ShapedChars,
    IReadOnlyList<string> UnknownFields)
{
    public double CompressionRatio => RawChars == 0 ? 1d : (double)ShapedChars / RawChars;
}

/// <summary>整形参数。默认值刻意保守：宁可少给几期，也别一次塞几十万字符进上下文。</summary>
public sealed record BiyingShapeOptions
{
    /// <summary>最多返回多少行。</summary>
    public int MaxRows { get; init; } = 5;

    /// <summary>整形后 JSON 的字符预算。超出就继续减行。</summary>
    public int MaxChars { get; init; } = 8_000;

    /// <summary>只保留这些键（为 null 表示不投影、保留原始键）。</summary>
    public IReadOnlyCollection<string>? Fields { get; init; }

    /// <summary>
    /// 关键词过滤：只保留任意字段包含该词的执行。用于超大静态表（股票列表 250,566 字符）——
    /// 那种表不能全量导出，只能按名字搜。为空则不过滤。
    /// </summary>
    public string? Keyword { get; init; }
}

/// <summary>
/// 把必盈原始响应整形为"适合进上下文"的形态。
///
/// 为什么必须有这一层（实测）：单只股票的资产负债表一次返回 <b>229,084 字符</b>
/// （约 60k token），股票列表 250,566 字符。原样返回等于一次查询毁掉一段上下文。
///
/// 三个动作：
/// 1. <b>选行</b>：按日期字段倒序取最近 N 期——不假设源顺序（实测资产负债表是升序、
///    财务指标 cwzb 是倒序，端点之间不一致）。
/// 2. <b>投影</b>：只保留需要的键。
/// 3. <b>附词典</b>：只列本次实际出现的键。原始键是拼音短键（jzrq/plrq），
///    没有词典模型只能猜，猜错比不给更糟；而全量词典（112 项）本身也占上下文。
/// </summary>
public static class BiyingShaper
{
    /// <summary>识别"哪一列是日期"的候选键，按可信度排序。</summary>
    private static readonly string[] DateKeyCandidates =
        ["jzrq", "date", "t", "rq", "jyrq", "bbrq", "reportDate", "tradingDate"];

    public static BiyingShapeResult Shape(BiyingEndpoint endpoint, string rawBody, BiyingShapeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(rawBody);
        options ??= new BiyingShapeOptions();

        var rawChars = rawBody.Length;

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(rawBody);
        }
        catch (JsonException)
        {
            // 解析失败不抛：模型需要看到原文才能判断发生了什么，强行失败只会丢掉证据
            var fallback = Truncate(rawBody, options.MaxChars);
            return new BiyingShapeResult(endpoint.Id, endpoint.Name, fallback, 0, 0, false, null,
                rawChars, fallback.Length, []);
        }

        var rows = node switch
        {
            JsonArray array => array.OfType<JsonObject>().ToList(),
            JsonObject single => [single],
            _ => [],
        };

        if (rows.Count == 0)
        {
            return new BiyingShapeResult(endpoint.Id, endpoint.Name, rawBody, 0, 0, false, null,
                rawChars, rawChars, []);
        }

        // 关键词过滤（股票列表这类超大静态表的唯一入口）：过滤后为空时**不能静默返回空表**，
        // 否则模型会以为“这只股票不存在”，而实际是关键词写法不同。
        var keyword = options.Keyword?.Trim();
        if (!string.IsNullOrEmpty(keyword))
        {
            rows = rows.Where(row => row.Any(pair =>
                    pair.Value?.ToString()?.Contains(keyword, StringComparison.OrdinalIgnoreCase) == true))
                .ToList();

            if (rows.Count == 0)
            {
                var empty = new JsonObject
                {
                    ["endpoint"] = $"{endpoint.Id} {endpoint.Name}",
                    ["keyword"] = keyword,
                    ["total"] = 0,
                    ["returned"] = 0,
                    ["note"] = $"没有匹配「{keyword}」的行。关键词必须出现在某个字段值里（名称/代码均可）。",
                };
                var emptyText = empty.ToJsonString(SerializerOptions);
                return new BiyingShapeResult(endpoint.Id, endpoint.Name, emptyText, 0, 0, false, null,
                    rawChars, emptyText.Length, []);
            }
        }

        var dateKey = DateKeyCandidates.FirstOrDefault(candidate =>
            rows[0].ContainsKey(candidate) && rows.All(row => row.ContainsKey(candidate)));

        var ordered = dateKey is null
            ? rows
            : rows.OrderByDescending(row => row[dateKey]?.ToString() ?? string.Empty, StringComparer.Ordinal).ToList();

        var fieldAllowlist = options.Fields is { Count: > 0 }
            ? new HashSet<string>(options.Fields, StringComparer.Ordinal)
            : null;

        var projected = ordered
            .Select(row => Project(row, fieldAllowlist))
            .ToList();

        // 从最大行数开始收，直到装进字符预算
        var take = Math.Min(options.MaxRows, projected.Count);
        while (take > 0)
        {
            var candidate = BuildPayload(endpoint, ordered, projected, take, dateKey, rawChars, options);
            var text = candidate.ToJsonString(SerializerOptions);
            if (text.Length <= options.MaxChars)
            {
                var unknown = CollectUnknownFields(projected, take, endpoint);
                return new BiyingShapeResult(endpoint.Id, endpoint.Name, text,
                    TotalRows: rows.Count,
                    ReturnedRows: take,
                    Truncated: take < rows.Count,
                    DateKey: dateKey,
                    RawChars: rawChars,
                    ShapedChars: text.Length,
                    UnknownFields: unknown);
            }

            take--;
        }

        // 连一行都装不下：**不硬塞**。硬塞就会让预算形同虚设；返回可行动的提示，
        // 让调用方用 fields 收窄，而不是把一次查询变成一次上下文事故。
        var singleRowChars = projected[0].ToJsonString(SerializerOptions).Length;
        var exceeded = new JsonObject
        {
            ["endpoint"] = $"{endpoint.Id} {endpoint.Name}",
            ["total"] = rows.Count,
            ["returned"] = 0,
            ["truncated"] = true,
            ["budgetExceeded"] = true,
            ["singleRowChars"] = singleRowChars,
            ["rawChars"] = rawChars,
            ["note"] = $"单行约 {singleRowChars} 字符，超出本次预算 {options.MaxChars}；" +
                       "请用 fields 收窄字段，或换用更细的端点。",
        };
        var exceededText = exceeded.ToJsonString(SerializerOptions);
        return new BiyingShapeResult(endpoint.Id, endpoint.Name, exceededText,
            TotalRows: rows.Count,
            ReturnedRows: 0,
            Truncated: true,
            DateKey: dateKey,
            RawChars: rawChars,
            ShapedChars: exceededText.Length,
            UnknownFields: []);
    }

    private static JsonObject BuildPayload(
        BiyingEndpoint endpoint,
        List<JsonObject> ordered,
        List<JsonObject> projected,
        int take,
        string? dateKey,
        int rawChars,
        BiyingShapeOptions options)
    {
        var payload = new JsonObject
        {
            ["endpoint"] = $"{endpoint.Id} {endpoint.Name}",
            ["total"] = ordered.Count,
            ["returned"] = take,
            ["truncated"] = take < ordered.Count,
            ["rawChars"] = rawChars,
        };

        if (dateKey is not null)
            payload["dateKey"] = dateKey;

        payload["legend"] = BuildLegend(projected, take, endpoint);
        payload["rows"] = new JsonArray(projected.Take(take).Select(row => (JsonNode)row.DeepClone()).ToArray());

        if (take < ordered.Count)
        {
            payload["note"] = $"仅返回最近 {take} 行（共 {ordered.Count} 行）；" +
                              $"需要更多请提高 maxRows（当前预算 {options.MaxChars} 字符）。";
        }

        return payload;
    }

    private static JsonObject Project(JsonObject row, HashSet<string>? allowlist)
    {
        var result = new JsonObject();
        foreach (var pair in row)
        {
            if (allowlist is not null && !allowlist.Contains(pair.Key))
                continue;
            result[pair.Key] = pair.Value?.DeepClone();
        }

        return result;
    }

    /// <summary>只列出**本次实际出现**的键，避免把 112 项全量词典塞进上下文。</summary>
    private static JsonObject BuildLegend(List<JsonObject> projected, int take, BiyingEndpoint endpoint)
    {
        var dictionary = endpoint.Fields.ToDictionary(field => field.Key, field => field, StringComparer.Ordinal);
        var legend = new JsonObject();

        foreach (var key in projected.Take(take).SelectMany(row => row.Select(pair => pair.Key)).Distinct(StringComparer.Ordinal))
        {
            legend[key] = dictionary.TryGetValue(key, out var field)
                ? (field.Description.Length == 0 ? field.Type : $"{field.Description}")
                : "（词典未收录）";
        }

        return legend;
    }

    private static IReadOnlyList<string> CollectUnknownFields(List<JsonObject> projected, int take, BiyingEndpoint endpoint)
    {
        var known = endpoint.Fields.Select(field => field.Key).ToHashSet(StringComparer.Ordinal);
        return projected.Take(take)
            .SelectMany(row => row.Select(pair => pair.Key))
            .Where(key => !known.Contains(key))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max];

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
