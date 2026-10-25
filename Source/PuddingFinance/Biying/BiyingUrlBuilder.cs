using System.Text;

namespace PuddingFinance.Biying;

/// <summary>
/// 把「端点 + 参数」拼成真实请求 URL。
///
/// 规则（实测校正，与官方文档示例不同）：
/// <code>
/// https://api.biyingapi.com/{组}/{接口}/{代码}/{licence}
/// </code>
/// 证书是**末段路径**，不是 <c>?licence=</c>。参数个数不对时必须报错，
/// 而不是拼出一个语法正确、语义错误的 URL——那种 URL 会返回 404 并谎称"证书不存在"。
/// </summary>
public static class BiyingUrlBuilder
{
    /// <summary>
    /// 拼 URL。<paramref name="pathArgs"/> 按模板中占位符出现顺序填充
    /// （第一个通常是股票代码）；<paramref name="query"/> 只能使用注册表声明的查询键。
    /// </summary>
    public static string BuildUrl(
        BiyingEndpoint endpoint,
        string licence,
        IReadOnlyList<string>? pathArgs = null,
        IReadOnlyDictionary<string, string>? query = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(licence);

        var args = pathArgs ?? [];
        var required = endpoint.PlaceholderCount;
        if (args.Count < required)
        {
            throw new BiyingEndpointException(
                $"端点 {endpoint.Id}「{endpoint.Name}」需要 {required} 个路径参数，" +
                $"实际给了 {args.Count} 个。模板：{endpoint.Template}");
        }

        if (args.Count > required)
        {
            throw new BiyingEndpointException(
                $"端点 {endpoint.Id}「{endpoint.Name}」只接受 {required} 个路径参数，" +
                $"实际给了 {args.Count} 个（多余参数会被忽略，故此处直接拒绝以免掩盖错误）。");
        }

        var builder = new StringBuilder("https://");
        builder.Append(endpoint.Host);

        var argumentIndex = 0;
        foreach (var segment in endpoint.Segments)
        {
            builder.Append('/');
            switch (segment.Kind)
            {
                case BiyingSegmentKind.Literal:
                    builder.Append(segment.Value);
                    break;
                case BiyingSegmentKind.Placeholder:
                    builder.Append(Uri.EscapeDataString(args[argumentIndex].Trim()));
                    argumentIndex++;
                    break;
                case BiyingSegmentKind.Licence:
                    builder.Append(Uri.EscapeDataString(licence.Trim()));
                    break;
                default:
                    throw new InvalidOperationException($"未知路径段类型 {segment.Kind}。");
            }
        }

        if (query is { Count: > 0 })
        {
            var unknown = query.Keys
                .Where(key => !endpoint.Query.Contains(key, StringComparer.OrdinalIgnoreCase))
                .ToList();
            if (unknown.Count > 0)
            {
                var allowed = endpoint.Query.Count == 0 ? "（该端点无查询参数）" : string.Join("、", endpoint.Query);
                throw new BiyingEndpointException(
                    $"端点 {endpoint.Id}「{endpoint.Name}」不支持查询参数：{string.Join("、", unknown)}。允许的：{allowed}");
            }

            var separator = '?';
            foreach (var pair in query)
            {
                if (string.IsNullOrWhiteSpace(pair.Value))
                    continue;
                builder.Append(separator).Append(pair.Key).Append('=').Append(Uri.EscapeDataString(pair.Value.Trim()));
                separator = '&';
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// 把 URL 里的证书替换成占位符，用于日志与回执。
    /// **任何写出到日志/上下文的地方都必须用这个**——证书泄露一次就等于永久泄露。
    /// </summary>
    public static string Redact(string url, string licence)
    {
        if (string.IsNullOrEmpty(licence) || string.IsNullOrEmpty(url))
            return url;
        return url.Replace(Uri.EscapeDataString(licence.Trim()), "<licence>", StringComparison.Ordinal)
                  .Replace(licence.Trim(), "<licence>", StringComparison.Ordinal);
    }
}
