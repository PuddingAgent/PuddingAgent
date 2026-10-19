namespace PuddingHomeAssistant.Diagnostics;

/// <summary>
/// Home Assistant 组件的**稳定错误码**。公开合同只暴露这些常量，
/// 不把 HTTP 状态码、异常类型或第三方消息透传给调用方。
/// </summary>
public static class HaErrorCodes
{
    public const string NotConfigured = "ha.not_configured";
    public const string InstanceUnreachable = "ha.instance_unreachable";
    public const string AuthFailed = "ha.auth_failed";
    public const string Forbidden = "ha.forbidden";
    public const string NotFound = "ha.not_found";
    public const string InvalidRequest = "ha.invalid_request";
    public const string ServiceError = "ha.service_error";
    public const string Unavailable = "ha.unavailable";
    public const string RateLimited = "ha.rate_limited";
    public const string DomainBlocked = "ha.domain_blocked";
    public const string OptInRequired = "ha.domain_opt_in_required";
    public const string Timeout = "ha.timeout";
    public const string OutputTruncated = "ha.output_truncated";

    /// <summary>HTTP 状态码 → 稳定错误码。未知状态按 <see cref="ServiceError"/> 处理（fail-closed）。</summary>
    public static string FromHttpStatus(int statusCode) => statusCode switch
    {
        400 => InvalidRequest,
        401 => AuthFailed,
        403 => Forbidden,
        404 => NotFound,
        405 => InvalidRequest,
        429 => RateLimited,
        500 => ServiceError,
        502 or 503 or 504 => Unavailable,
        _ => ServiceError,
    };

    /// <summary>WebSocket 错误码 → 稳定错误码（切片 C 使用）。</summary>
    public static string FromWebSocketCode(string? code) => code switch
    {
        "unauthorized" or "auth_invalid" => AuthFailed,
        "not_allowed" => Forbidden,
        "not_found" => NotFound,
        "invalid_format" => InvalidRequest,
        "service_validation_error" => InvalidRequest,
        "timeout" => Timeout,
        "not_supported" or "unknown_command" => InvalidRequest,
        _ => ServiceError,
    };
}
