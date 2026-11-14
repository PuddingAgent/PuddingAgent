using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using PuddingCode.Configuration;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.Tools;

/// <summary>
/// 读取钉钉日历（CalDAV）上的日程，只读。
/// <para>
/// 为什么要它：「今天有什么安排」原先只能靠人去日历里翻，或者让 Agent 去跑一段脚本。
/// Agent 自己能读，才能把日程带进早报、判断某个时间段是否已被占用。
/// </para>
/// <para>
/// <b>凭据纪律</b>：密码只在本进程内使用，<b>绝不</b>出现在回执、异常或日志里 ——
/// 回执只给服务器主机名、日历显示名与事件本身。
/// </para>
/// <para>
/// <b>只读边界</b>：只发 PROPFIND 与 REPORT，不发 PUT/DELETE，<b>不会</b>改动用户的日历。
/// </para>
/// </summary>
[Tool(
    id: "calendar_list",
    name: "日历日程",
    description:
        "读取钉钉日历（CalDAV）上的日程，只读。【何时用】用户问「今天/这周有什么安排」、生成早报或周报需要日程、" +
        "要判断某个时间段是否被占用时。【怎么用】days 取从现在起的天数（默认 7）；也可用 from/to 指定日期（yyyy-MM-dd，本地时区）。" +
        "【坑】空列表是正常结果（没有日程），不要当成失败；事件时区常带 TZID（如 Asia/Shanghai），返回的 start/end 是该时区的墙上时间，" +
        "不要当成 UTC 再换算一次；凭据从 data/config/caldav.json 读取，缺失时明确报错而不是猜。")]
public sealed class CalendarListTool(PuddingDataPaths paths) : PuddingToolBase<CalendarListArgs>
{
    private const string ConfigFileName = "caldav.json";
    private const int DefaultDays = 7;

    private readonly PuddingDataPaths _paths = paths ?? throw new ArgumentNullException(nameof(paths));

    /// <summary>共享客户端：CalDAV 是多次往返（发现三跳 + 一次 REPORT），另建客户端会退化连接池与 DNS 缓存。</summary>
    internal static readonly HttpClient SharedHttp = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    private static readonly XNamespace D = CalDavXml.D;

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        CalendarListArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        if (!TryResolveRange(args, out var from, out var to, out var rangeError))
        {
            return ToolExecutionResult.Fail(rangeError);
        }

        var configPath = _paths.SystemConfigFile(ConfigFileName);
        if (!File.Exists(configPath))
        {
            return ToolExecutionResult.Fail(
                $"无法读取日历：配置不存在（{configPath}）。需要 {{\"server\":…,\"user\":…,\"password\":…}} 或 passwordRef 指向环境变量。");
        }

        CalendarEndpoint endpoint;
        string configError;
        try
        {
            var configJson = await File.ReadAllTextAsync(configPath, ct).ConfigureAwait(false);
            if (!TryResolveEndpoint(configJson, out endpoint, out configError))
            {
                return ToolExecutionResult.Fail(configError);
            }
        }
        catch (IOException exception)
        {
            return ToolExecutionResult.Fail($"无法读取日历配置（{configPath}）：{exception.Message}");
        }

        try
        {
            var discovery = await DiscoverAsync(endpoint, ct).ConfigureAwait(false);
            if (discovery.Error is not null)
            {
                return ToolExecutionResult.Fail(discovery.Error);
            }

            var events = await QueryAsync(endpoint, discovery.CalendarHref!, from, to, ct).ConfigureAwait(false);

            var host = new Uri(endpoint.Server).Host;
            return ToolExecutionResult.Ok(Render(host, discovery.CalendarName, from, to, events));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException exception)
        {
            // 只报主机与原因：异常文本里不会有凭据，但也不把完整 URL 带出去。
            return ToolExecutionResult.Fail($"读取日历时网络失败：{exception.Message}");
        }
    }

    /// <summary>把 days / from / to 归一成一个 UTC 时间范围（CalDAV 的 time-range 要 UTC）。</summary>
    internal static bool TryResolveRange(CalendarListArgs args, out string from, out string to, out string error)
    {
        error = string.Empty;

        var start = DateTimeOffset.Now;
        var end = start.AddDays(args.Days is > 0 ? args.Days.Value : DefaultDays);

        if (!string.IsNullOrWhiteSpace(args.From))
        {
            if (!DateTime.TryParse(args.From, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsedFrom))
            {
                from = to = string.Empty;
                error = $"from 不是合法日期：{args.From}（期望 yyyy-MM-dd）";
                return false;
            }

            start = new DateTimeOffset(parsedFrom.Date, TimeZoneInfo.Local.GetUtcOffset(parsedFrom.Date));
        }

        if (!string.IsNullOrWhiteSpace(args.To))
        {
            if (!DateTime.TryParse(args.To, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsedTo))
            {
                from = to = string.Empty;
                error = $"to 不是合法日期：{args.To}（期望 yyyy-MM-dd）";
                return false;
            }

            end = new DateTimeOffset(parsedTo.Date.AddDays(1), TimeZoneInfo.Local.GetUtcOffset(parsedTo.Date));
        }

        if (end <= start)
        {
            from = to = string.Empty;
            error = "时间范围的结束早于开始：检查 to 是否早于 from。";
            return false;
        }

        from = start.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        to = end.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        return true;
    }

    /// <summary>
    /// 从 <c>caldav.json</c> 取出端点。<c>password</c> 为空时支持 <c>passwordRef</c>（按环境变量名解析）；
    /// 解析不到就明确报错，<b>不猜</b>。
    /// </summary>
    public static bool TryResolveEndpoint(string configJson, out CalendarEndpoint endpoint, out string error)
    {
        endpoint = default!;
        error = string.Empty;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(configJson);
        }
        catch (JsonException exception)
        {
            error = $"caldav.json 不是合法 JSON：{exception.Message}";
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            var server = ReadString(root, "server");
            var user = ReadString(root, "user");
            var password = ReadString(root, "password");
            var passwordRef = ReadString(root, "passwordRef");

            if (string.IsNullOrWhiteSpace(server))
            {
                error = "caldav.json 缺少 server。";
                return false;
            }

            if (string.IsNullOrWhiteSpace(user))
            {
                error = "caldav.json 缺少 user。";
                return false;
            }

            if (string.IsNullOrWhiteSpace(password))
            {
                if (string.IsNullOrWhiteSpace(passwordRef))
                {
                    error = "caldav.json 既没有 password 也没有 passwordRef，无法认证。";
                    return false;
                }

                password = Environment.GetEnvironmentVariable(passwordRef);
                if (string.IsNullOrWhiteSpace(password))
                {
                    error = $"caldav.json 的 passwordRef='{passwordRef}' 指向的环境变量为空，无法认证。";
                    return false;
                }
            }

            endpoint = new CalendarEndpoint(server.TrimEnd('/'), user, password);
            return true;
        }
    }

    /// <summary>三跳发现：principal → home → 第一个真日历。任一步为空都明确失败，不拿默认值猜。</summary>
    internal static async Task<(string? CalendarHref, string? CalendarName, string? Error)> DiscoverAsync(
        CalendarEndpoint endpoint, CancellationToken ct)
    {
        var principalsUrl = await ResolvePrincipalsUrlAsync(endpoint, ct).ConfigureAwait(false);

        var principalXml = await PropFindAsync(endpoint, principalsUrl,
            "<d:propfind xmlns:d=\"DAV:\"><d:prop><d:current-user-principal/></d:prop></d:propfind>",
            depth: "0", ct).ConfigureAwait(false);
        if (principalXml is null)
        {
            return (null, null, "发现失败：principals 集合没有返回 207。");
        }

        var principal = CalDavXml.CurrentUserPrincipal(principalXml);
        if (string.IsNullOrEmpty(principal))
        {
            return (null, null, "发现失败：响应里没有 current-user-principal。");
        }

        var homeXml = await PropFindAsync(endpoint, Combine(endpoint.Server, principal),
            "<d:propfind xmlns:d=\"DAV:\" xmlns:c=\"urn:ietf:params:xml:ns:caldav\"><d:prop><c:calendar-home-set/></d:prop></d:propfind>",
            depth: "0", ct).ConfigureAwait(false);
        if (homeXml is null)
        {
            return (null, null, "发现失败：principal 上没有返回 calendar-home-set。");
        }

        var home = CalDavXml.CalendarHomeSet(homeXml);
        if (string.IsNullOrEmpty(home))
        {
            return (null, null, "发现失败：响应里没有 calendar-home-set。");
        }

        var listXml = await PropFindAsync(endpoint, Combine(endpoint.Server, home),
            "<d:propfind xmlns:d=\"DAV:\" xmlns:c=\"urn:ietf:params:xml:ns:caldav\"><d:prop><d:displayname/><d:resourcetype/></d:prop></d:propfind>",
            depth: "1", ct).ConfigureAwait(false);
        if (listXml is null)
        {
            return (null, null, "发现失败：日历主目录没有返回列表。");
        }

        var calendarHref = CalDavXml.FirstCalendarHref(listXml);
        if (string.IsNullOrEmpty(calendarHref))
        {
            return (null, null, "发现失败：主目录下没有任何日历集合。");
        }

        return (calendarHref, CalDavXml.CalendarDisplayName(listXml, calendarHref), null);
    }

    /// <summary>
    /// 先请 <c>/.well-known/caldav</c> 给出 principals 地址（302 的 Location）。
    /// 目的地由服务器决定，<b>不硬编码</b>账号或路径 —— 换租户、换账号时这段代码不用改。
    /// </summary>
    private static async Task<string> ResolvePrincipalsUrlAsync(CalendarEndpoint endpoint, CancellationToken ct)
    {
        var fallback = Combine(endpoint.Server, "/dav/principals/");

        using var request = new HttpRequestMessage(HttpMethod.Get, Combine(endpoint.Server, "/.well-known/caldav"));
        ApplyAuth(request, endpoint);

        using var response = await SharedHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        var location = response.Headers.Location;
        if (location is null)
        {
            return fallback;
        }

        return location.IsAbsoluteUri ? location.ToString() : Combine(endpoint.Server, location.ToString());
    }

    private static async Task<IReadOnlyList<CalDavEvent>> QueryAsync(
        CalendarEndpoint endpoint, string calendarHref, string from, string to, CancellationToken ct)
    {
        var body =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
            "<C:calendar-query xmlns:D=\"DAV:\" xmlns:C=\"urn:ietf:params:xml:ns:caldav\">" +
            "<D:prop><D:getetag/><C:calendar-data/></D:prop>" +
            "<C:filter><C:comp-filter name=\"VCALENDAR\"><C:comp-filter name=\"VEVENT\">" +
            $"<C:time-range start=\"{from}\" end=\"{to}\"/>" +
            "</C:comp-filter></C:comp-filter></C:filter></C:calendar-query>";

        using var request = new HttpRequestMessage(new HttpMethod("REPORT"), Combine(endpoint.Server, calendarHref));
        ApplyAuth(request, endpoint);
        request.Headers.TryAddWithoutValidation("Depth", "1");
        request.Content = new StringContent(body, Encoding.UTF8, "application/xml");

        using var response = await SharedHttp.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode is not (HttpStatusCode.MultiStatus or HttpStatusCode.OK))
        {
            throw new HttpRequestException($"日历 REPORT 返回 {(int)response.StatusCode}。");
        }

        var xml = XDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        return CalDavXml.ParseCalendarQuery(xml);
    }

    private static async Task<XDocument?> PropFindAsync(
        CalendarEndpoint endpoint, string url, string body, string depth, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), url);
        ApplyAuth(request, endpoint);
        request.Headers.TryAddWithoutValidation("Depth", depth);
        request.Content = new StringContent(body, Encoding.UTF8, "application/xml");

        using var response = await SharedHttp.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.MultiStatus)
        {
            return null;
        }

        return XDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }

    private static void ApplyAuth(HttpRequestMessage request, CalendarEndpoint endpoint)
    {
        var raw = Encoding.UTF8.GetBytes($"{endpoint.User}:{endpoint.Password}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(raw));
    }

    private static string Combine(string server, string href) =>
        href.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? href : server + (href.StartsWith('/') ? href : "/" + href);

    private static string Render(
        string host, string? calendarName, string from, string to, IReadOnlyList<CalDavEvent> events)
    {
        if (events.Count == 0)
        {
            return $"{calendarName ?? "日历"}（{host}）：{from}..{to} 之间没有日程。";
        }

        var builder = new StringBuilder();
        builder.AppendLine($"{calendarName ?? "日历"}（{host}）共 {events.Count} 条，范围 {from}..{to}：");
        foreach (var item in events.OrderBy(e => e.Start, StringComparer.Ordinal))
        {
            builder.AppendLine(
                $"- {item.Start} → {item.End}  {item.Summary}" +
                (item.TimeZone == "unknown" ? string.Empty : $"  [{item.TimeZone}]"));
        }

        return builder.ToString().TrimEnd();
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

/// <summary>日历查询参数。</summary>
public sealed record CalendarListArgs
{
    [ToolParam("从现在起的天数（默认 7）；给了 from/to 时忽略")]
    public int? Days { get; init; }

    [ToolParam("起始日期（yyyy-MM-dd，本地时区），可省略")]
    public string? From { get; init; }

    [ToolParam("结束日期（yyyy-MM-dd，本地时区，含当天），可省略")]
    public string? To { get; init; }
}

/// <summary>解析出的 CalDAV 端点。<b>含凭据</b>，只在进程内使用，不得序列化。</summary>
public sealed record CalendarEndpoint(string Server, string User, string Password);
