using Flurl.Http;
using Flurl.Http.Configuration;
using System.Collections.Concurrent;

namespace PuddingRuntime.Services.Skills;

/// <summary>Default <see cref="IWebClient"/> implementation backed by Flurl.</summary>
public sealed class FlurlWebClient : IWebClient
{
    private readonly IFlurlClientCache _clientCache;
    private readonly ConcurrentDictionary<string, CookieJar> _sessionCookies = new(StringComparer.Ordinal);

    public FlurlWebClient(IFlurlClientCache clientCache)
    {
        _clientCache = clientCache;
    }

    public async Task<WebClientResponse> SendAsync(WebClientRequest request, CancellationToken ct)
    {
        var client = _clientCache.GetOrAdd("HttpFetchSkill");
        var flurlRequest = client.Request(request.Url)
            .AllowAnyHttpStatus();

        foreach (var (name, value) in request.Headers)
        {
            flurlRequest = flurlRequest.WithHeader(name, value);
        }

        if (request.TimeoutSeconds is > 0)
            flurlRequest = flurlRequest.WithTimeout(TimeSpan.FromSeconds(Math.Clamp(request.TimeoutSeconds.Value, 1, 120)));

        if (string.Equals(request.CookieScope, "session", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(request.CookieKey))
        {
            var jar = _sessionCookies.GetOrAdd(request.CookieKey, _ => new CookieJar());
            flurlRequest = flurlRequest.WithCookies(jar);
        }

        using var response = await flurlRequest.SendAsync(
            new HttpMethod(request.Method),
            string.IsNullOrEmpty(request.Body)
                ? null
                : new StringContent(request.Body, System.Text.Encoding.UTF8, request.ContentType ?? "application/json"),
            cancellationToken: ct);

        var body = await response.GetStringAsync();
        return new WebClientResponse
        {
            StatusCode = response.StatusCode,
            ReasonPhrase = response.ResponseMessage.ReasonPhrase ?? string.Empty,
            ContentType = response.ResponseMessage.Content.Headers.ContentType?.ToString(),
            Body = body,
            Headers = CollectHeaders(response.ResponseMessage),
            FinalUrl = response.ResponseMessage.RequestMessage?.RequestUri?.ToString() ?? request.Url,
        };
    }

    private static IReadOnlyDictionary<string, string> CollectHeaders(HttpResponseMessage response)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in response.Headers)
        {
            headers[header.Key] = string.Join(", ", header.Value);
        }

        foreach (var header in response.Content.Headers)
        {
            headers[header.Key] = string.Join(", ", header.Value);
        }

        return headers;
    }
}
