namespace PuddingRuntime.Services.Skills;

/// <summary>
/// Category of an <c>http_fetch</c> failure, chosen so the agent can decide the next move
/// without another probe.
/// </summary>
public enum HttpFetchFailureKind
{
    /// <summary>401/403 - the target refused the request.</summary>
    AccessDenied,

    /// <summary>404/410 - the resource is gone.</summary>
    NotFound,

    /// <summary>429 - the target is throttling.</summary>
    RateLimited,

    /// <summary>5xx - the target server itself failed.</summary>
    ServerError,

    /// <summary>Any other 4xx.</summary>
    ClientError,

    /// <summary>TLS/SSL handshake failure, usually a local proxy or network block.</summary>
    TlsHandshake,

    /// <summary>The request timed out.</summary>
    Timeout,

    /// <summary>The host name could not be resolved.</summary>
    DnsFailure,

    /// <summary>Anything not covered above.</summary>
    Unknown,
}

/// <summary>
/// One classified failure: what happened, whether retrying can plausibly help, and what to do instead.
/// </summary>
/// <param name="Kind">Failure category.</param>
/// <param name="Summary">Short factual description, e.g. <c>HTTP 403 Forbidden</c>.</param>
/// <param name="Hint">Actionable next step for the agent.</param>
/// <param name="Retryable">Whether an immediate retry can plausibly succeed.</param>
public sealed record HttpFetchFailure(
    HttpFetchFailureKind Kind,
    string Summary,
    string Hint,
    bool Retryable)
{
    /// <summary>
    /// Error text handed back to the agent: the fact first, then the decision.
    /// </summary>
    public string ToErrorMessage() => $"{Summary} | {Hint}";
}

/// <summary>
/// Maps an <c>http_fetch</c> outcome to an agent-actionable diagnosis.
/// </summary>
/// <remarks>
/// A bare <c>HTTP 403 Forbidden</c> leaves the agent guessing whether a retry could work, and that
/// guess costs a full model round trip over the whole conversation. Every failure therefore carries
/// an explicit hint and a retry verdict, so the agent can switch tools in the same turn.
/// </remarks>
public static class HttpFetchFailureClassifier
{
    private const int MaxDetailChars = 160;

    /// <summary>Classifies a non-success HTTP status code.</summary>
    public static HttpFetchFailure ClassifyStatus(int statusCode, string? reasonPhrase)
    {
        var summary = string.IsNullOrWhiteSpace(reasonPhrase)
            ? $"HTTP {statusCode}"
            : $"HTTP {statusCode} {reasonPhrase}";

        return statusCode switch
        {
            401 or 403 => new HttpFetchFailure(
                HttpFetchFailureKind.AccessDenied,
                summary,
                "Target refused the request (rate limit, bot protection or auth). Repeating this URL usually fails again; use deepseek_fetch or an existing search summary.",
                Retryable: false),
            404 or 410 => new HttpFetchFailure(
                HttpFetchFailureKind.NotFound,
                summary,
                "Page not found. Do not retry; pick another URL or use a search tool.",
                Retryable: false),
            429 => new HttpFetchFailure(
                HttpFetchFailureKind.RateLimited,
                summary,
                "Target is throttling. Wait before a single retry, or use deepseek_fetch.",
                Retryable: true),
            >= 500 => new HttpFetchFailure(
                HttpFetchFailureKind.ServerError,
                summary,
                "Target server error. One retry may help; otherwise use deepseek_fetch.",
                Retryable: true),
            >= 400 => new HttpFetchFailure(
                HttpFetchFailureKind.ClientError,
                summary,
                "Request rejected. Recheck the URL and headers before retrying.",
                Retryable: false),
            _ => new HttpFetchFailure(
                HttpFetchFailureKind.Unknown,
                summary,
                "Unexpected status. Do not retry unchanged.",
                Retryable: false),
        };
    }

    /// <summary>Classifies a transport-level exception.</summary>
    public static HttpFetchFailure ClassifyException(Exception exception)
    {
        var text = Describe(exception);

        if (ContainsAny(text, "SSL", "TLS", "authentication failed"))
        {
            return new HttpFetchFailure(
                HttpFetchFailureKind.TlsHandshake,
                $"TLS handshake failed: {Clip(text)}",
                "Usually a local proxy or network block for this host, so retrying it keeps failing. Use deepseek_fetch; for GitHub content try raw.githubusercontent.com.",
                Retryable: false);
        }

        if (exception is TimeoutException || ContainsAny(text, "timed out", "timeout"))
        {
            return new HttpFetchFailure(
                HttpFetchFailureKind.Timeout,
                $"Request timed out: {Clip(text)}",
                "Retry once with a larger timeout_seconds.",
                Retryable: true);
        }

        if (ContainsAny(text, "No such host", "Name or service not known", "nodename nor servname"))
        {
            return new HttpFetchFailure(
                HttpFetchFailureKind.DnsFailure,
                $"DNS resolution failed: {Clip(text)}",
                "Host cannot be resolved. Do not retry.",
                Retryable: false);
        }

        return new HttpFetchFailure(
            HttpFetchFailureKind.Unknown,
            $"Request failed: {Clip(text)}",
            "Do not retry unchanged; use deepseek_fetch or another source.",
            Retryable: false);
    }

    /// <summary>
    /// Flattens the whole exception chain into one line: the outer message carries the category
    /// ("SSL connection could not be established") while the inner one carries the actual cause,
    /// and Flurl's "see inner exception" placeholder adds no information once the chain is walked.
    /// </summary>
    private static string Describe(Exception exception)
    {
        var parts = new System.Collections.Generic.List<string>();
        var current = exception;
        var guard = 0;
        while (current is not null && guard++ < 8)
        {
            var message = InnerPlaceholderPattern.Replace(current.Message ?? string.Empty, string.Empty).Trim();
            if (message.Length > 0)
                parts.Add(message);

            current = current.InnerException;
        }

        var detail = parts.Count == 0 ? exception.GetType().Name : string.Join(" ", parts);
        return $"{exception.GetType().Name}: {detail}";
    }

    private static readonly System.Text.RegularExpressions.Regex InnerPlaceholderPattern = new(
        @"\s*[,;]?\s*see inner exception\.?",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static string Clip(string value) =>
        value.Length <= MaxDetailChars ? value : value[..MaxDetailChars] + "...";

    private static bool ContainsAny(string value, params string[] needles) =>
        needles.Any(needle => value.Contains(needle, StringComparison.OrdinalIgnoreCase));
}
