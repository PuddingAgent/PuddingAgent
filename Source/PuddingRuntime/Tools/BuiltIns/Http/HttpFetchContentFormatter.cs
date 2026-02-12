using System.Text;
using System.Text.Json;

namespace PuddingRuntime.Services.Skills;

/// <summary>Default formatter for `http_fetch` responses.</summary>
public sealed class HttpFetchContentFormatter : IHttpFetchContentFormatter
{
    private readonly IHtmlToMarkdownConverter _htmlToMarkdownConverter;

    public HttpFetchContentFormatter(IHtmlToMarkdownConverter htmlToMarkdownConverter)
    {
        _htmlToMarkdownConverter = htmlToMarkdownConverter;
    }

    public HttpFetchFormattedResponse Format(WebClientResponse response, HttpFetchFormatOptions options)
    {
        var outputFormat = string.IsNullOrWhiteSpace(options.OutputFormat)
            ? "markdown"
            : options.OutputFormat.Trim().ToLowerInvariant();
        var maxChars = Math.Clamp(options.MaxResponseChars, 1, 200_000);

        return outputFormat switch
        {
            "json" => FormatJson(response, options with { MaxResponseChars = maxChars }),
            "raw" => FormatTextual(response, response.Body, options.IncludeHeaders, maxChars),
            "text" => FormatTextual(response, FormatText(response), options.IncludeHeaders, maxChars),
            "markdown" => FormatTextual(response, FormatMarkdown(response), options.IncludeHeaders, maxChars),
            _ => FormatTextual(response, FormatMarkdown(response), options.IncludeHeaders, maxChars),
        };
    }

    private static HttpFetchFormattedResponse FormatJson(WebClientResponse response, HttpFetchFormatOptions options)
    {
        var truncatedBody = Truncate(response.Body, options.MaxResponseChars, out var truncated);
        var payload = new HttpFetchJsonOutput
        {
            StatusCode = response.StatusCode,
            ReasonPhrase = response.ReasonPhrase,
            Success = IsSuccess(response.StatusCode),
            ContentType = response.ContentType,
            FinalUrl = response.FinalUrl,
            Headers = options.IncludeHeaders ? response.Headers : null,
            Body = truncatedBody,
            Truncated = truncated,
        };

        return new HttpFetchFormattedResponse
        {
            Output = JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                WriteIndented = false,
            }),
            Truncated = truncated,
        };
    }

    private HttpFetchFormattedResponse FormatTextual(
        WebClientResponse response,
        string body,
        bool includeHeaders,
        int maxChars)
    {
        var truncatedBody = Truncate(body, maxChars, out var truncated);
        var sb = new StringBuilder();
        sb.Append("HTTP ").Append(response.StatusCode).Append(' ').AppendLine(response.ReasonPhrase);

        if (includeHeaders && response.Headers.Count > 0)
        {
            foreach (var (name, value) in response.Headers.OrderBy(h => h.Key, StringComparer.OrdinalIgnoreCase))
            {
                sb.Append(name).Append(": ").AppendLine(value);
            }
        }

        sb.AppendLine();
        sb.Append(truncatedBody);
        if (truncated)
            sb.AppendLine().Append("... [truncated]");

        return new HttpFetchFormattedResponse
        {
            Output = sb.ToString(),
            Truncated = truncated,
        };
    }

    private string FormatMarkdown(WebClientResponse response)
    {
        if (!HtmlContentExtractor.IsHtml(response.ContentType, response.Body))
            return response.Body;

        var readableHtml = HtmlContentExtractor.ExtractReadableHtml(response.Body);
        return _htmlToMarkdownConverter.Convert(readableHtml).Trim();
    }

    private static string FormatText(WebClientResponse response)
    {
        if (!HtmlContentExtractor.IsHtml(response.ContentType, response.Body))
            return response.Body;

        return HtmlContentExtractor.ToPlainText(response.Body);
    }

    private static string Truncate(string value, int maxChars, out bool truncated)
    {
        if (value.Length <= maxChars)
        {
            truncated = false;
            return value;
        }

        truncated = true;
        return value[..maxChars];
    }

    private static bool IsSuccess(int statusCode) => statusCode is >= 200 and <= 299;
}
