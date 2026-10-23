using System.Text;
using System.Text.Json;
using PuddingHtml;

namespace PuddingRuntime.Services.Skills;

/// <summary>Default formatter for `http_fetch` responses.</summary>
/// <remarks>
/// Textual output is paged rather than hard-cut at the character limit: the agent gets a whole
/// page plus a cursor, and asks for the remainder only if it actually needs it. A hard cut both
/// loses structure and forces a full re-fetch to see what came next.
/// </remarks>
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

        switch (outputFormat)
        {
            case "outline":
                return FormatOutline(response, options, maxChars);
            case "json":
                // A paged JSON document is not valid JSON, so this representation stays whole and
                // truncation is reported explicitly instead.
                return FormatJson(response, options with { MaxResponseChars = maxChars });
        }

        var (body, representation) = outputFormat switch
        {
            "raw" => (response.Body, "raw"),
            "text" => (FormatText(response), "text"),
            _ => (FormatMarkdown(response), "markdown"),
        };

        return Page(response, body, representation, options, maxChars);
    }

    private HttpFetchFormattedResponse FormatOutline(
        WebClientResponse response,
        HttpFetchFormatOptions options,
        int maxChars)
    {
        var outline = HttpFetchContentSegmenter.BuildOutline(FormatMarkdown(response));
        var bounded = Truncate(outline, maxChars, out var truncated);

        var sb = new StringBuilder();
        AppendStatusLine(sb, response, options.IncludeHeaders);
        sb.Append(bounded);
        if (truncated)
            sb.AppendLine().Append("... [truncated]");

        return new HttpFetchFormattedResponse
        {
            Output = sb.ToString(),
            Truncated = truncated,
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

    private static HttpFetchFormattedResponse Page(
        WebClientResponse response,
        string body,
        string representation,
        HttpFetchFormatOptions options,
        int maxChars)
    {
        var fingerprint = HttpFetchContentCursor.FingerprintOf(body);
        var offset = 0;

        if (!string.IsNullOrWhiteSpace(options.Cursor))
        {
            if (!HttpFetchContentCursor.TryDecode(options.Cursor, out var cursor))
            {
                return Reject(
                    "Invalid cursor",
                    "The cursor is malformed. Omit cursor to read from the start.");
            }

            if (!string.Equals(cursor.Representation, representation, StringComparison.Ordinal))
            {
                return Reject(
                    "Cursor does not belong to this representation",
                    $"That cursor was produced by output_format={cursor.Representation}. "
                    + "Re-request with that format, or omit cursor to read from the start.");
            }

            if (!string.Equals(cursor.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                return Reject(
                    "Content changed since the cursor was issued",
                    "The page is not the one that cursor was produced from, so resuming could splice "
                    + "unrelated text. Omit cursor to read from the start.");
            }

            offset = cursor.Offset;
        }

        var page = HttpFetchContentSegmenter.Slice(body, offset, maxChars);
        var sb = new StringBuilder();
        AppendStatusLine(sb, response, options.IncludeHeaders);

        if (offset > 0)
        {
            sb.Append("[resumed at char ").Append(offset)
              .Append(" of ").Append(body.Length).AppendLine("]");
        }

        sb.Append(page.Text);

        string? nextCursor = null;
        if (page.HasMore)
        {
            nextCursor = new HttpFetchContentCursor(representation, page.NextOffset, fingerprint).Encode();
            sb.AppendLine()
              .Append("[partial: ").Append(page.NextOffset).Append(" / ").Append(body.Length)
              .Append(" chars read; pass cursor=").Append(nextCursor).Append(" for the rest]");
        }

        return new HttpFetchFormattedResponse
        {
            Output = sb.ToString(),
            Truncated = page.HasMore,
            NextCursor = nextCursor,
        };
    }

    private static HttpFetchFormattedResponse Reject(string summary, string hint) => new()
    {
        Output = string.Empty,
        Truncated = false,
        Failure = new HttpFetchFailure(HttpFetchFailureKind.ClientError, summary, hint, Retryable: false),
    };

    private static void AppendStatusLine(StringBuilder sb, WebClientResponse response, bool includeHeaders)
    {
        sb.Append("HTTP ").Append(response.StatusCode).Append(' ').AppendLine(response.ReasonPhrase);

        if (!includeHeaders || response.Headers.Count == 0)
            return;

        foreach (var (name, value) in response.Headers.OrderBy(h => h.Key, StringComparer.OrdinalIgnoreCase))
        {
            sb.Append(name).Append(": ").AppendLine(value);
        }
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
