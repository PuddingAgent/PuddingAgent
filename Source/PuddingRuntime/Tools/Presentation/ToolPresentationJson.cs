using System.Text.Json;

namespace PuddingRuntime.Services.Tools;

/// <summary>
/// 展示投影（presentation）用的 JSON 读取助手：只读事实、绝不猜值。
/// <para>
/// key 归一化 = 去下划线 + 小写（<c>job_id</c> / <c>jobId</c> / <c>JobId</c> 命中同一事实），
/// 与 <c>PuddingToolBase.DeserializeArgs</c> 对 snake_case/camelCase 参数的容错语义一致。
/// </para>
/// </summary>
internal static class ToolPresentationJson
{
    internal static readonly JsonElement EmptyObject = CreateEmptyObject();

    /// <summary>参数 JSON 原文 → object 元素；空/非法/非 object 一律回退空对象（presenter 只看事实）。</summary>
    public static JsonElement ParseObjectOrEmpty(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return EmptyObject;

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? document.RootElement.Clone()
                : EmptyObject;
        }
        catch (JsonException)
        {
            return EmptyObject;
        }
    }

    /// <summary>
    /// 结果 JSON 原文 → canonical 元素：空/空白 ⇒ null（call 阶段语义）；
    /// 合法 JSON ⇒ 原样（object/array/标量）；非法文本 ⇒ 作为 JSON string 值保留原文。
    /// </summary>
    public static JsonElement? TryParseResult(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var document = JsonDocument.Parse(json.Trim());
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return JsonSerializer.SerializeToElement(json);
        }
    }

    private static JsonElement CreateEmptyObject()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }
}

/// <summary>工具参数/结果对象的只读事实读取器（归一化 key；缺失返回 null，不返回默认值）。</summary>
internal sealed class ToolPresentationArgs
{
    private readonly Dictionary<string, JsonElement> _byNormalizedName;

    private ToolPresentationArgs(Dictionary<string, JsonElement> byNormalizedName)
        => _byNormalizedName = byNormalizedName;

    public static ToolPresentationArgs Create(JsonElement? element)
    {
        var map = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (element is { ValueKind: JsonValueKind.Object } obj)
        {
            foreach (var property in obj.EnumerateObject())
                map[Normalize(property.Name)] = property.Value;
        }

        return new ToolPresentationArgs(map);
    }

    /// <summary>原始元素（缺失或 null 值 ⇒ null）。</summary>
    public JsonElement? Get(string name)
    {
        if (!_byNormalizedName.TryGetValue(Normalize(name), out var value))
            return null;
        return value.ValueKind == JsonValueKind.Null ? null : value;
    }

    /// <summary>非空字符串事实（缺失/非字符串/空白 ⇒ null）。</summary>
    public string? GetString(string name)
    {
        var value = Get(name);
        if (value is not { ValueKind: JsonValueKind.String } element)
            return null;
        var text = element.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    /// <summary>整数事实（缺失/不可解析 ⇒ null）。</summary>
    public int? GetInt(string name)
    {
        var value = Get(name);
        if (value is null)
            return null;
        if (value.Value.ValueKind == JsonValueKind.Number && value.Value.TryGetInt32(out var number))
            return number;
        if (value.Value.ValueKind == JsonValueKind.String
            && int.TryParse(value.Value.GetString(), out var parsed))
            return parsed;
        return null;
    }

    /// <summary>数组事实（缺失/非数组 ⇒ null）。</summary>
    public JsonElement? GetArray(string name)
    {
        var value = Get(name);
        return value is { ValueKind: JsonValueKind.Array } array ? array : null;
    }

    private static string Normalize(string name)
        => name.Replace("_", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
}

/// <summary>
/// 展示 meta 事实收集器：只写「确有」的事实（null/空白一律不写），全空时 meta 整键省略。
/// 禁止写入猜测值或默认值。
/// </summary>
internal sealed class ToolPresentationMeta
{
    private readonly Dictionary<string, JsonElement> _facts = new(StringComparer.Ordinal);

    public void AddString(string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            _facts[key] = JsonSerializer.SerializeToElement(value);
    }

    public void AddInt(string key, int? value)
    {
        if (value.HasValue)
            _facts[key] = JsonSerializer.SerializeToElement(value.Value);
    }

    /// <summary>无事实 ⇒ null（调用方据此省略 meta 键）。</summary>
    public JsonElement? Build()
        => _facts.Count == 0 ? null : JsonSerializer.SerializeToElement(_facts);
}
