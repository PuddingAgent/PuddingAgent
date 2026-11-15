using System.Text.Json;

namespace PuddingSpeech;

public sealed class SpeechFailure(string code, string message, int status = 502, bool retryable = false)
    : Exception(message)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
    public bool Retryable { get; } = retryable;

    public static SpeechFailure Protocol() => new("voice_protocol_error", "语音服务响应不完整，请重试。");

    public static SpeechFailure FromProvider(int status, string? body)
    {
        string? code = null;
        try
        {
            using var doc = JsonDocument.Parse(body ?? "{}");
            var root = doc.RootElement;
            if (root.TryGetProperty("code", out var value)) code = value.ToString();
            else if (root.TryGetProperty("header", out var header) && header.TryGetProperty("error_code", out value)) code = value.ToString();
        }
        catch (JsonException) { }
        if (code is "AllocationQuota.FreeTierOnly" or "QuotaExhausted" or "Arrearage" or "InsufficientBalance")
            return new("voice_quota_exhausted", "语音服务额度已用尽或受到仅免费额度模式限制，请在语音设置中检查额度。", 503);
        if (status is 401 or 403 || code is "InvalidApiKey" or "AccessDenied")
            return new("voice_credentials_invalid", "语音服务凭据、区域或模型权限不可用，请检查语音设置。", 503);
        if (status == 429) return new("voice_rate_limited", "语音服务繁忙，请稍后重试。", 429, true);
        if (status is 408 or 504) return new("voice_timeout", "语音服务响应超时，请重试。", 504, true);
        return new("voice_provider_failed", "语音服务暂时不可用，请重试或检查语音设置。", 502, status >= 500);
    }
}
