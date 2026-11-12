using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace PuddingDingTalk;

/// <summary>Robot media transport. Only a provider-issued download capability or Core-owned bytes enter this port.</summary>
public sealed class DingTalkMediaClient(HttpClient http, string robotCode, string secret)
{
    public const int MaxImageBytes = 4 * 1024 * 1024;
    private string? token;
    private DateTimeOffset expires;

    private async Task<string> AccessTokenAsync(CancellationToken ct)
    {
        if (token is not null && DateTimeOffset.UtcNow < expires) return token;
        using var reply = await http.PostAsJsonAsync("https://api.dingtalk.com/v1.0/oauth2/accessToken",
            new { appKey = robotCode, appSecret = secret }, ct);
        var result = await ReadAsync(reply, ct);
        token = Required(result, "accessToken");
        var seconds = result.GetProperty("expireIn").GetInt32();
        expires = DateTimeOffset.UtcNow.AddSeconds(Math.Max(0, seconds - 120));
        return token;
    }

    public async Task<byte[]> DownloadAsync(string code, CancellationToken ct)
    {
        var json = await RobotPostAsync("messageFiles/download", new { robotCode, downloadCode = code }, ct);
        var url = new Uri(Required(json, "downloadUrl"));
        for (var hop = 0; hop < 4; hop++)
        {
            if (!AllowedDownload(url)) throw new InvalidDataException("Unsupported DingTalk media host.");
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)response.StatusCode is >= 300 and <= 399 && response.Headers.Location is { } location)
            {
                url = new Uri(url, location);
                continue;
            }
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaxImageBytes) throw new InvalidDataException("Image too large.");
            using var output = new MemoryStream();
            using var input = await response.Content.ReadAsStreamAsync(ct);
            var buffer = new byte[32768];
            int count;
            while ((count = await input.ReadAsync(buffer, ct)) > 0)
            {
                if (output.Length + count > MaxImageBytes) throw new InvalidDataException("Image too large.");
                output.Write(buffer, 0, count);
            }
            return output.ToArray();
        }
        throw new InvalidDataException("Too many DingTalk media redirects.");
    }

    private static bool AllowedDownload(Uri url) => url.Scheme == "https" && url.IsDefaultPort
        && url.UserInfo.Length == 0 && url.Fragment.Length == 0 && url.HostNameType == UriHostNameType.Dns
        && new[] { "dingtalk.com", "dingtalkusercontent.com", "alicdn.com", "aliyuncs.com" }
            .Any(domain => url.Host == domain || url.Host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase));

    public async Task<bool> SendImageAsync(string room, GroupImage image, CancellationToken ct)
    {
        if (image.Bytes.Length is 0 or > MaxImageBytes || image.MimeType is not ("image/jpeg" or "image/png"))
            throw new InvalidDataException("Unsupported outgoing image.");
        using var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(image.Bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue(image.MimeType);
        form.Add(part, "media", image.MimeType == "image/png" ? "image.png" : "image.jpg");
        var access = await AccessTokenAsync(ct);
        using var upload = await http.PostAsync("https://oapi.dingtalk.com/media/upload?type=image&access_token="
            + Uri.EscapeDataString(access), form, ct);
        var uploaded = await ReadAsync(upload, ct);
        var mediaId = Required(uploaded, "media_id");
        var sent = await RobotPostAsync("groupMessages/send", new { robotCode, openConversationId = room,
            msgKey = "sampleImageMsg", msgParam = JsonSerializer.Serialize(new { photoURL = mediaId }) }, ct);
        return !string.IsNullOrWhiteSpace(GroupIngress.String(sent, "processQueryKey"));
    }

    private async Task<JsonElement> RobotPostAsync(string operation, object body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.dingtalk.com/v1.0/robot/" + operation);
        request.Headers.Add("x-acs-dingtalk-access-token", await AccessTokenAsync(ct));
        request.Content = JsonContent.Create(body);
        using var response = await http.SendAsync(request, ct);
        return await ReadAsync(response, ct);
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage reply, CancellationToken ct)
    {
        reply.EnsureSuccessStatusCode();
        return await reply.Content.ReadFromJsonAsync<JsonElement>(ct);
    }

    private static string Required(JsonElement element, string key) =>
        GroupIngress.String(element, key) is { Length: > 0 } value ? value : throw new InvalidDataException("Invalid DingTalk media receipt.");
}
