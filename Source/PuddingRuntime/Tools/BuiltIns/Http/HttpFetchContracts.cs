using System.Text.Json.Serialization;

namespace PuddingRuntime.Services.Skills;

/// <summary>
/// Thin HTTP transport abstraction for agent web requests.
/// Implementations may use Flurl, HttpClient, or another HTTP stack without
/// leaking implementation-specific types into the tool contract.
/// </summary>
public interface IWebClient
{
    Task<WebClientResponse> SendAsync(WebClientRequest request, CancellationToken ct);
}

/// <summary>Stable request shape consumed by <see cref="IWebClient"/>.</summary>
public sealed record WebClientRequest
{
    public required string Url { get; init; }
    public string Method { get; init; } = "GET";
    public IReadOnlyDictionary<string, string> Headers { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public string? Body { get; init; }
    public string? ContentType { get; init; }
    public int? TimeoutSeconds { get; init; }
    public string? CookieScope { get; init; }
    public string? CookieKey { get; init; }
}

/// <summary>Stable response shape returned by <see cref="IWebClient"/>.</summary>
public sealed record WebClientResponse
{
    public required int StatusCode { get; init; }
    public string ReasonPhrase { get; init; } = string.Empty;
    public string? ContentType { get; init; }
    public required string Body { get; init; }
    public IReadOnlyDictionary<string, string> Headers { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public string? FinalUrl { get; init; }
}

/// <summary>Converts HTML fragments into Markdown.</summary>
public interface IHtmlToMarkdownConverter
{
    string Convert(string html);
}

/// <summary>Formats HTTP responses for agent context consumption.</summary>
public interface IHttpFetchContentFormatter
{
    HttpFetchFormattedResponse Format(WebClientResponse response, HttpFetchFormatOptions options);
}

/// <summary>Formatting options for `http_fetch` output.</summary>
public sealed record HttpFetchFormatOptions
{
    public string OutputFormat { get; init; } = "markdown";
    public int MaxResponseChars { get; init; } = 8192;
    public bool IncludeHeaders { get; init; }
}

/// <summary>Formatted response body plus truncation metadata.</summary>
public sealed record HttpFetchFormattedResponse
{
    public required string Output { get; init; }
    public bool Truncated { get; init; }
}

internal sealed record HttpFetchJsonOutput
{
    [JsonPropertyName("status_code")]
    public required int StatusCode { get; init; }

    [JsonPropertyName("reason_phrase")]
    public required string ReasonPhrase { get; init; }

    [JsonPropertyName("success")]
    public required bool Success { get; init; }

    [JsonPropertyName("content_type")]
    public string? ContentType { get; init; }

    [JsonPropertyName("final_url")]
    public string? FinalUrl { get; init; }

    [JsonPropertyName("headers")]
    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    [JsonPropertyName("body")]
    public required string Body { get; init; }

    [JsonPropertyName("truncated")]
    public required bool Truncated { get; init; }
}
