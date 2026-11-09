using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace PuddingSlack;

/// <summary>One HTTP attempt per call. Callers own durable retries; transport errors after a send are ambiguous.</summary>
public sealed class SlackApiClient(HttpClient http, SlackCredentials credentials,
    Func<CancellationToken, Task<string>>? appTokenProvider = null)
{
    private readonly SlackCredentials _credentials = credentials.Validate();
    public async Task<SlackIdentity> AuthenticateAsync(CancellationToken ct)
    {
        using var json = await ControlAsync("auth.test", _credentials.BotToken, ct);
        return new(Required(json.RootElement, "team_id"), Required(json.RootElement, "user_id"));
    }

    public async Task<Uri> OpenConnectionAsync(CancellationToken ct)
    {
        var token = appTokenProvider is null ? _credentials.AppToken : await appTokenProvider(ct);
        using var json = await ControlAsync("apps.connections.open", token, ct);
        var uri = new Uri(Required(json.RootElement, "url"));
        if (uri.Scheme != "wss" || !(uri.Host.EndsWith(".slack.com", StringComparison.OrdinalIgnoreCase)))
            throw new SlackProtocolException("invalid_socket_url");
        return uri;
    }

    public async Task<SlackSendReceipt> PostAsync(SlackPost post, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(post.ChannelId) || string.IsNullOrWhiteSpace(post.Text) || post.Text.Length > 35000)
            return new(SlackSendStatus.Rejected, "invalid_or_oversized_message");
        ct.ThrowIfCancellationRequested();
        var text = SlackMessageText.Format(post.Text);
        if (text.Length > 35000) return new(SlackSendStatus.Rejected, "invalid_or_oversized_message");
        using var request = Request("chat.postMessage", _credentials.BotToken, new
        {
            channel = post.ChannelId, text, thread_ts = post.ThreadTs,
            unfurl_links = false, unfurl_media = false, mrkdwn = true, parse = "none", link_names = false
        });
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var delay = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(30);
                var at = response.Headers.RetryAfter?.Date ?? DateTimeOffset.UtcNow.Add(delay);
                return new(SlackSendStatus.Retryable, "rate_limited", RetryAt: at);
            }
            if ((int)response.StatusCode >= 500) return new(SlackSendStatus.Unknown, "http_server_error");
            if (!response.IsSuccessStatusCode) return new(SlackSendStatus.Rejected, "http_" + (int)response.StatusCode);
            using var json = await ReadAsync(response, deadline.Token);
            var root = json.RootElement;
            if (!IsOk(root))
            {
                var code = ErrorCode(root);
                return new(code is "internal_error" or "fatal_error" or "request_timeout" or "service_unavailable"
                    ? SlackSendStatus.Unknown : SlackSendStatus.Rejected, code);
            }
            var channel = SlackEventParser.String(root, "channel");
            var ts = SlackEventParser.String(root, "ts");
            return channel == post.ChannelId && !string.IsNullOrWhiteSpace(ts)
                ? new(SlackSendStatus.Sent, "slack_accepted", channel, ts)
                : new(SlackSendStatus.Unknown, "missing_or_mismatched_receipt");
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or IOException)
        {
            // Do not expose exception text: a transport exception can contain a signed URL or Authorization data.
            return new(SlackSendStatus.Unknown, "transport_or_response_unknown");
        }
    }

    private async Task<JsonDocument> ControlAsync(string method, string token, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        using var request = Request(method, token, new { });
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if (!response.IsSuccessStatusCode) throw new SlackProtocolException("control_http_" + (int)response.StatusCode);
        var json = await ReadAsync(response, deadline.Token);
        if (IsOk(json.RootElement)) return json;
        var code = ErrorCode(json.RootElement);
        json.Dispose();
        throw new SlackProtocolException(code);
    }

    private static HttpRequestMessage Request(string method, string token, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "https://slack.com/api/" + method)
        { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }
    private static bool IsOk(JsonElement root) => root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True;
    private static string Required(JsonElement root, string name) => SlackEventParser.String(root, name)
        ?? throw new SlackProtocolException("missing_" + name);
    private static string ErrorCode(JsonElement root)
    {
        var value = SlackEventParser.String(root, "error");
        return value is { Length: > 0 and < 80 } && value.All(c => char.IsAsciiLetterLower(c) || c == '_')
            ? value : "slack_api_error";
    }
    private static async Task<JsonDocument> ReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await response.Content.LoadIntoBufferAsync(1024 * 1024, ct);
        return JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(ct));
    }
}
