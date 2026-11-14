using System.Xml.Linq;

namespace PuddingRuntime.Services.Tools;

/// <summary>日历事件（解析结果，不含凭据）。</summary>
public sealed record CalDavEvent(string Summary, string Start, string End, string TimeZone);

/// <summary>
/// CalDAV 响应的**纯解析**部分：只做 XML/ICS 到对象的转换，不碰网络、不碰配置。
///
/// 单独拆出来的理由：Discovery 的三跳（principal → home → calendar）和事件解析
/// 全都可以用固定的服务端响应做回归；埋进工具方法里就只能靠联网才能验，
/// 而联网验不了「服务器换了前缀/换了命名空间」这类形状变化。
/// </summary>
internal static class CalDavXml
{
    internal static readonly XNamespace D = "DAV:";
    internal static readonly XNamespace C = "urn:ietf:params:xml:ns:caldav";

    /// <summary><c>&lt;current-user-principal&gt;&lt;href&gt;…</c>。</summary>
    internal static string? CurrentUserPrincipal(XDocument document) =>
        HrefOf(document, D + "current-user-principal");

    /// <summary><c>&lt;calendar-home-set&gt;&lt;href&gt;…</c>。</summary>
    internal static string? CalendarHomeSet(XDocument document) =>
        HrefOf(document, C + "calendar-home-set");

    /// <summary>
    /// 日历主目录下第一个<b>真正的日历集合</b>的 href。
    ///
    /// 只看 <c>resourcetype</c> 里有没有 <c>calendar</c>：Inbox/Outbox 也是 collection，
    /// 但它们的 resourcetype 是 schedule-inbox/schedule-outbox —— 把它们当日历会在
    /// REPORT 上得到空结果，而不是报错（最难查的那种失败）。
    /// </summary>
    internal static string? FirstCalendarHref(XDocument document)
    {
        foreach (var response in document.Descendants(D + "response"))
        {
            var type = response.Descendants(D + "resourcetype").FirstOrDefault();
            if (type is null || type.Element(C + "calendar") is null)
            {
                continue;
            }

            var href = response.Element(D + "href")?.Value?.Trim();
            if (!string.IsNullOrEmpty(href))
            {
                return href;
            }
        }

        return null;
    }

    /// <summary>日历集合的显示名（用于回执；取不到就返回 null，不编造）。</summary>
    internal static string? CalendarDisplayName(XDocument document, string calendarHref)
    {
        foreach (var response in document.Descendants(D + "response"))
        {
            if (!string.Equals(response.Element(D + "href")?.Value?.Trim(), calendarHref, StringComparison.Ordinal))
            {
                continue;
            }

            return response.Descendants(D + "displayname").FirstOrDefault()?.Value?.Trim();
        }

        return null;
    }

    /// <summary>解析 <c>calendar-query</c> REPORT 的响应。<b>没有事件是空列表，不是错误。</b></summary>
    internal static IReadOnlyList<CalDavEvent> ParseCalendarQuery(XDocument document)
    {
        var events = new List<CalDavEvent>();

        foreach (var data in document.Descendants(C + "calendar-data"))
        {
            var ics = data.Value;
            if (string.IsNullOrWhiteSpace(ics))
            {
                continue;
            }

            var summary = IcsLine(ics, "SUMMARY") ?? string.Empty;
            var (start, startZone) = IcsLine(ics, "DTSTART") is { } s ? SplitValue(s) : (null, null);
            var (end, _) = IcsLine(ics, "DTEND") is { } e ? SplitValue(e) : (null, null);

            events.Add(new CalDavEvent(
                summary,
                start ?? string.Empty,
                end ?? string.Empty,
                startZone ?? "unknown"));
        }

        return events;
    }

    private static string? HrefOf(XDocument document, XName propertyName)
    {
        var property = document.Descendants(propertyName).FirstOrDefault();
        return property?.Element(D + "href")?.Value?.Trim();
    }

    /// <summary>
    /// 取 ICS 一行（如 <c>DTSTART;TZID=Asia/Shanghai:20261114T120000</c>）。
    /// 参数部分原样保留，交给 <see cref="SplitValue"/> 拆。
    /// </summary>
    private static string? IcsLine(string ics, string name)
    {
        foreach (var raw in ics.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (!line.StartsWith(name, StringComparison.Ordinal))
            {
                continue;
            }

            // "SUMMARY" 也要排除 "SUMMARYX" 之类的前缀巧合。
            if (line.Length > name.Length && line[name.Length] is not (';' or ':'))
            {
                continue;
            }

            var colon = line.IndexOf(':');
            if (colon < 0)
            {
                continue;
            }

            var parameters = line[name.Length..colon];
            var value = line[(colon + 1)..].Trim();
            return string.IsNullOrEmpty(parameters) ? value : $"{parameters.TrimStart(';')}|{value}";
        }

        return null;
    }

    /// <summary>把 <c>TZID=…|20261114T120000</c> 拆成（值、时区）。无参数时返回 (值, null)。</summary>
    private static (string? Value, string? Zone) SplitValue(string raw)
    {
        var bar = raw.IndexOf('|');
        if (bar < 0)
        {
            return (raw, null);
        }

        var parameters = raw[..bar];
        var value = raw[(bar + 1)..];

        var zone = parameters
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .Where(p => p.Length == 2 && p[0].Equals("TZID", StringComparison.OrdinalIgnoreCase))
            .Select(p => p[1])
            .FirstOrDefault();

        return (value, zone);
    }
}
