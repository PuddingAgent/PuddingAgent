using System.Text.Json;

namespace PuddingFinance.Collection;

/// <summary>
/// 巨潮公告查询参数。
///
/// <para>
/// <see cref="Stock"/> 用巨潮自己的 <c>代码,orgId</c> 形式（例 <c>601398,10000001</c>）：
/// orgId 只能从公告页/前一次查询里取，**不能猜**。只给 <see cref="SearchKey"/> 时是全文检索。
/// </para>
/// <para>
/// <see cref="Category"/> 是透传字段，不做枚举：巨潮的分类码（如 <c>category_ndbg_szsh</c>）
/// 属站点实现细节，未逐一实测前不写进常量，避免"看起来有分类其实传错了"。
/// </para>
/// </summary>
public sealed record CninfoAnnouncementQuery(
    string? SearchKey = null,
    string? Stock = null,
    string? Category = null,
    string Column = "szse",
    string? SeDate = null,
    int PageNumber = 1,
    int PageSize = 30,
    string TabName = "fulltext",
    bool HighlightTitle = false);

/// <summary>一条公告。<see cref="PublishedAt"/> 是**公告归属日**，不是抓取日。</summary>
public sealed record CninfoAnnouncement(
    string Code,
    string Name,
    string? OrgId,
    string AnnouncementId,
    string Title,
    DateTimeOffset? PublishedAt,
    string? PdfUrl,
    int? SizeKb,
    string? AdjunctType);

/// <summary>一页公告。<see cref="Total"/> 是服务端的全量命中数，与 <see cref="Items"/> 条数无关。</summary>
public sealed record CninfoAnnouncementPage(
    int Total,
    int PageNumber,
    int PageSize,
    IReadOnlyList<CninfoAnnouncement> Items);

/// <summary>
/// 巨潮公告查询客户端。
///
/// 通道已实测可用（2026-10-26：POST 200，20,716 B，totalAnnouncement=265,744），
/// 它是 SKILL 阶段 3「年报穿透」的入口：先拿公告清单与 PDF 直链，再谈读年报。
/// 传输层复用 <see cref="OfficialSourceFetcher"/>，因此长在同一个白名单与有界读取之上。
/// </summary>
public sealed class CninfoAnnouncementSource(OfficialSourceFetcher fetcher)
{
    public const string QueryUrl = "http://www.cninfo.com.cn/new/hisAnnouncement/query";

    /// <summary>PDF 直链的静态域；公告返回的是相对路径，需要拼在这后面。</summary>
    public const string StaticBaseUrl = "http://static.cninfo.com.cn/";

    private const int MaxPageSize = 100;

    private readonly OfficialSourceFetcher _fetcher =
        fetcher ?? throw new ArgumentNullException(nameof(fetcher));

    public async Task<CninfoAnnouncementPage> QueryAsync(
        CninfoAnnouncementQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var form = BuildForm(query);

        var result = await _fetcher.FetchAsync(
            new SourceFetchRequest(
                QueryUrl,
                Method: "POST",
                Body: form,
                ContentType: "application/x-www-form-urlencoded",
                Headers: new Dictionary<string, string>
                {
                    // 巨潮对缺少 Referer 的请求更保守；带上公告列表页更接近真实浏览器。
                    ["Referer"] = "http://www.cninfo.com.cn/new/commonUrl?url=disclosure/list/notice",
                }),
            cancellationToken).ConfigureAwait(false);

        return Parse(result.Body, query);
    }

    /// <summary>
    /// 表单编码。纯函数：不联网就能断言"参数确实按巨潮的口径发出去了"，
    /// 而不是靠读代码猜。
    /// </summary>
    internal static string BuildForm(CninfoAnnouncementQuery query)
    {
        if (query.PageNumber < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(query), query.PageNumber, "页码从 1 开始");
        }

        if (query.PageSize < 1 || query.PageSize > MaxPageSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(query), query.PageSize, $"每页条数须在 1..{MaxPageSize}");
        }

        (string Key, string? Value)[] pairs =
        [
            ("pageNum", query.PageNumber.ToString()),
            ("pageSize", query.PageSize.ToString()),
            ("column", query.Column),
            ("tabName", query.TabName),
            ("plate", null),
            ("stock", query.Stock),
            ("searchkey", query.SearchKey),
            ("secid", null),
            ("category", query.Category),
            ("trade", null),
            ("seDate", query.SeDate),
            ("sortName", null),
            ("sortType", null),
            ("isHLtitle", query.HighlightTitle ? "true" : "false"),
        ];

        return string.Join('&', pairs.Select(pair =>
            pair.Key + "=" + Uri.EscapeDataString(pair.Value ?? string.Empty)));
    }

    /// <summary>
    /// 解析巨潮回执。纯函数，便于用真实回执片段做回归——
    /// 字段缺失/null 是常态（实测 <c>classifiedAnnouncements</c> 就是 null），不能当异常。
    /// </summary>
    internal static CninfoAnnouncementPage Parse(string json, CninfoAnnouncementQuery query)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new CninfoParseException("巨潮回执为空，无法解析");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            throw new CninfoParseException($"巨潮回执不是 JSON：{exception.Message}", exception);
        }

        using (document)
        {
            var root = document.RootElement;
            var total = root.TryGetProperty("totalRecordNum", out var totalElement)
                && totalElement.TryGetInt32(out var parsedTotal)
                ? parsedTotal
                : 0;

            var items = new List<CninfoAnnouncement>();
            if (root.TryGetProperty("announcements", out var array)
                && array.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in array.EnumerateArray())
                {
                    items.Add(ReadAnnouncement(item));
                }
            }

            return new CninfoAnnouncementPage(total, query.PageNumber, query.PageSize, items);
        }
    }

    private static CninfoAnnouncement ReadAnnouncement(JsonElement item)
    {
        var adjunct = ReadString(item, "adjunctUrl");
        return new CninfoAnnouncement(
            Code: ReadString(item, "secCode") ?? string.Empty,
            Name: ReadString(item, "secName") ?? string.Empty,
            OrgId: ReadString(item, "orgId"),
            AnnouncementId: ReadString(item, "announcementId") ?? string.Empty,
            Title: ReadString(item, "announcementTitle") ?? string.Empty,
            PublishedAt: ReadTimestamp(item, "announcementTime"),
            PdfUrl: BuildPdfUrl(adjunct),
            SizeKb: item.TryGetProperty("adjunctSize", out var size) && size.TryGetInt32(out var kb) ? kb : null,
            AdjunctType: ReadString(item, "adjunctType"));
    }

    private static string? BuildPdfUrl(string? adjunct)
    {
        if (string.IsNullOrWhiteSpace(adjunct))
        {
            return null;
        }

        return adjunct.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? adjunct
            : StaticBaseUrl + adjunct.TrimStart('/');
    }

    private static string? ReadString(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    /// <summary>
    /// 毫秒时间戳 → 东八区时间。巨潮不带时区，按 A 股市场惯例视为 +08:00，
    /// 因此 <c>2026-10-27</c> 的公告在 26 日晚间就能查到，不能当"Future"异常处理。
    /// </summary>
    private static DateTimeOffset? ReadTimestamp(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt64(out var milliseconds) || milliseconds <= 0)
        {
            return null;
        }

        return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds)
            .ToOffset(TimeSpan.FromHours(8));
    }
}

/// <summary>巨潮回执解析失败。与传输失败分开：一个能重试，一个重试也没用。</summary>
public sealed class CninfoParseException(string message, Exception? inner = null) : Exception(message, inner);
