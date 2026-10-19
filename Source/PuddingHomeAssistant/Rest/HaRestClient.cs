using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using PuddingHomeAssistant.Contracts;
using PuddingHomeAssistant.Diagnostics;
using PuddingHomeAssistant.Policy;

namespace PuddingHomeAssistant.Rest;

/// <summary>组件抛出的唯一异常类型：携带稳定错误码与可选 HTTP 状态，不泄露令牌。</summary>
public sealed class HaClientException(string code, string message, int? httpStatus = null)
    : Exception(message)
{
    public string Code { get; } = code;
    public int? HttpStatus { get; } = httpStatus;
}

/// <summary>`GET /api/` 的健康响应。</summary>
public sealed record HaApiStatus(string Message);

/// <summary>`GET /api/config` 的摘要（只保留对调用方有用的字段）。</summary>
public sealed record HaConfigSnapshot(
    string Version,
    string? LocationName,
    string? TimeZone,
    int ComponentCount);

/// <summary>
/// Home Assistant REST 客户端（`/api/…`）。
///
/// <para>设计约束：</para>
/// <list type="bullet">
/// <item>不做实例寻址/凭据读取 —— <see cref="HaInstance"/> 由引用方冻结后传入；</item>
/// <item>不使用 <c>HttpClient.BaseAddress</c>（避免跨实例污染），每次构造绝对 URI；</item>
/// <item>不自动重试（动作调用非幂等，重试可能造成重复副作用）；</item>
/// <item>错误只映射为 <see cref="HaErrorCodes"/>，响应体只截取有界诊断片段。</item>
/// </list>
/// </summary>
public sealed class HaRestClient(HttpClient http)
{
    private const int DiagnosticBytes = 512;

    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);

    public async Task<HaApiStatus> GetStatusAsync(HaInstance instance, CancellationToken ct)
    {
        using var doc = await SendAsync(instance, HttpMethod.Get, "api/", null, ct).ConfigureAwait(false);
        var message = doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() ?? string.Empty : string.Empty;
        return new HaApiStatus(message);
    }

    public async Task<HaConfigSnapshot> GetConfigAsync(HaInstance instance, CancellationToken ct)
    {
        using var doc = await SendAsync(instance, HttpMethod.Get, "api/config", null, ct).ConfigureAwait(false);
        var root = doc.RootElement;
        return new HaConfigSnapshot(
            Version: GetString(root, "version") ?? "unknown",
            LocationName: GetString(root, "location_name"),
            TimeZone: GetString(root, "time_zone"),
            ComponentCount: root.TryGetProperty("components", out var c) && c.ValueKind == JsonValueKind.Array
                ? c.GetArrayLength()
                : 0);
    }

    /// <summary>读取全部可读状态；由调用方用 <see cref="HaOutputBudget"/> 投影与截断。</summary>
    public async Task<IReadOnlyList<HaState>> GetStatesAsync(HaInstance instance, CancellationToken ct)
    {
        using var doc = await SendAsync(instance, HttpMethod.Get, "api/states", null, ct).ConfigureAwait(false);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new HaClientException(HaErrorCodes.ServiceError, "Unexpected /api/states payload shape.");
        }

        var list = new List<HaState>(doc.RootElement.GetArrayLength());
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var state = ParseState(item);
            if (state is not null)
            {
                list.Add(state);
            }
        }

        return list;
    }

    /// <summary>读取单个实体；404 映射为 <see cref="HaErrorCodes.NotFound"/>。</summary>
    public async Task<HaState> GetStateAsync(HaInstance instance, string entityId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(entityId) || entityId.Contains('/'))
        {
            throw new HaClientException(HaErrorCodes.InvalidRequest, "entityId is empty or contains '/'.");
        }

        using var doc = await SendAsync(instance, HttpMethod.Get, $"api/states/{Uri.EscapeDataString(entityId)}", null, ct)
            .ConfigureAwait(false);
        return ParseState(doc.RootElement)
               ?? throw new HaClientException(HaErrorCodes.ServiceError, "Unexpected state payload.");
    }

    /// <summary>
    /// 调用动作。**调用前**执行域策略判定：命中永久黑名单抛 <see cref="HaErrorCodes.DomainBlocked"/>，
    /// 未放开的 opt-in 域抛 <see cref="HaErrorCodes.OptInRequired"/> —— 两者都不会发出 HTTP 请求。
    /// </summary>
    public async Task<HaServiceCallResult> CallServiceAsync(
        HaInstance instance,
        HaServiceCall call,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(call);

        if (!HaDomainPolicy.TryNormalize(call.Domain, out var domain) ||
            !HaDomainPolicy.TryNormalize(call.Service, out var service))
        {
            throw new HaClientException(HaErrorCodes.InvalidRequest, "domain/service must match [a-zA-Z0-9_].");
        }

        var decision = HaDomainPolicy.Decide(domain, instance);
        switch (decision)
        {
            case HaDomainDecision.Blocked:
                throw new HaClientException(
                    HaErrorCodes.DomainBlocked,
                    $"Domain '{domain}' is permanently blocked by policy.");
            case HaDomainDecision.OptInRequired:
                throw new HaClientException(
                    HaErrorCodes.OptInRequired,
                    $"Domain '{domain}' requires the user to enable it in the Home Assistant admin settings.");
        }

        var payload = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (call.EntityIds is { Count: > 0 })
        {
            payload["entity_id"] = call.EntityIds;
        }

        if (call.Data is not null)
        {
            foreach (var (key, value) in call.Data)
            {
                payload[key] = value;
            }
        }

        var relative = $"api/services/{domain}/{service}" + (call.ReturnResponse ? "?return_response" : string.Empty);
        using var doc = await SendAsync(instance, HttpMethod.Post, relative, payload, ct).ConfigureAwait(false);

        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Array)
        {
            return new HaServiceCallResult(root.GetArrayLength(), null);
        }

        if (root.ValueKind == JsonValueKind.Object)
        {
            var changed = root.TryGetProperty("changed_states", out var cs) && cs.ValueKind == JsonValueKind.Array
                ? cs.GetArrayLength()
                : 0;
            JsonElement? response = root.TryGetProperty("response", out var r) ? r.Clone() : null;
            return new HaServiceCallResult(changed, response);
        }

        return new HaServiceCallResult(0, null);
    }

    /// <summary>状态历史（`/api/history/period`）。`entityIds` 不能为空。</summary>
    public async Task<JsonElement> GetHistoryAsync(
        HaInstance instance,
        IReadOnlyList<string> entityIds,
        DateTimeOffset start,
        DateTimeOffset? end,
        CancellationToken ct)
    {
        if (entityIds is null || entityIds.Count == 0)
        {
            throw new HaClientException(HaErrorCodes.InvalidRequest, "entityIds is required for history.");
        }

        var filter = string.Join(',', entityIds.Select(Uri.EscapeDataString));
        var relative = $"api/history/period/{start:yyyy-MM-ddTHH:mm:ssK}?filter_entity_id={filter}";
        if (end is not null)
        {
            relative += $"&end_time={Uri.EscapeDataString(end.Value.ToString("yyyy-MM-ddTHH:mm:ssK"))}";
        }

        using var doc = await SendAsync(instance, HttpMethod.Get, relative, null, ct).ConfigureAwait(false);
        return doc.RootElement.Clone();
    }

    // ---------- 传输与解析 ----------

    private async Task<JsonDocument> SendAsync(
        HaInstance instance,
        HttpMethod method,
        string relative,
        object? body,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(instance);

        var uri = new Uri(instance.BaseUrl, relative);
        using var request = new HttpRequestMessage(method, uri);
        if (!string.IsNullOrEmpty(instance.Token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", instance.Token);
        }

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body is not null)
        {
            var json = JsonSerializer.Serialize(body, s_json);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw new HaClientException(HaErrorCodes.Timeout, "Request to Home Assistant timed out.");
        }
        catch (HttpRequestException ex)
        {
            throw new HaClientException(
                HaErrorCodes.InstanceUnreachable,
                $"Cannot reach Home Assistant at {instance.BaseUrl.Host}: {ex.GetType().Name}.");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var code = HaErrorCodes.FromHttpStatus((int)response.StatusCode);
                var detail = await ReadBoundedAsync(response, ct).ConfigureAwait(false);
                throw new HaClientException(code, $"HTTP {(int)response.StatusCode} {response.StatusCode}: {detail}", (int)response.StatusCode);
            }

            var payload = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(payload))
            {
                return JsonDocument.Parse("null");
            }

            try
            {
                return JsonDocument.Parse(payload);
            }
            catch (JsonException)
            {
                throw new HaClientException(HaErrorCodes.ServiceError, "Home Assistant returned non-JSON payload.");
            }
        }
    }

    private static async Task<string> ReadBoundedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            text = text.Replace('\r', ' ').Replace('\n', ' ');
            return text.Length <= DiagnosticBytes ? text : text[..DiagnosticBytes] + "…";
        }
        catch
        {
            return "<unreadable body>";
        }
    }

    private static HaState? ParseState(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var entityId = GetString(item, "entity_id");
        if (string.IsNullOrEmpty(entityId))
        {
            return null;
        }

        IReadOnlyDictionary<string, JsonElement>? attributes = null;
        if (item.TryGetProperty("attributes", out var attr) && attr.ValueKind == JsonValueKind.Object)
        {
            var dict = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var p in attr.EnumerateObject())
            {
                dict[p.Name] = p.Value.Clone();
            }

            attributes = dict;
        }

        return new HaState(
            entityId,
            GetString(item, "state") ?? string.Empty,
            ParseTimestamp(item, "last_changed"),
            ParseTimestamp(item, "last_updated"),
            attributes);
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static DateTimeOffset? ParseTimestamp(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String &&
        DateTimeOffset.TryParse(v.GetString(), out var ts)
            ? ts
            : null;
}
