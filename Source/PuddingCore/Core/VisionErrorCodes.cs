namespace PuddingCode.Core;

/// <summary>ADR-077 §9.1 视觉链路稳定错误码。只有瞬态网络错误允许按现有策略重试；能力/格式/限制错误不可盲目重试。</summary>
public static class VisionErrorCodes
{
    public const string ArtifactMissing = "vision_artifact_missing";
    public const string ArtifactForbidden = "vision_artifact_forbidden";
    public const string SourceInvalid = "vision_source_invalid";
    public const string SourceAccessDenied = "vision_source_access_denied";
    public const string SourceDownloadFailed = "vision_source_download_failed";
    public const string MediaInvalid = "vision_media_invalid";
    public const string RequestLimitExceeded = "vision_request_limit_exceeded";
    public const string ModelCapabilityMismatch = "vision_model_capability_mismatch";
    public const string ToolOutputNotSupported = "vision_tool_output_not_supported";
    public const string HelperModelRequired = "vision_helper_model_required";
    public const string HelperFailed = "vision_helper_failed";
    public const string ProviderFileUploadFailed = "vision_provider_file_upload_failed";
    public const string ProviderFileExpired = "vision_provider_file_expired";
    public const string ProviderRejected = "vision_provider_rejected";
}

/// <summary>
/// 视觉链路 fail-closed 异常：任一图片无法授权、解析、准备或序列化时抛出，
/// 禁止静默丢图后让模型按纯文本猜测。调用方把 Code 原样映射为运行终态/HTTP 4xx。
/// </summary>
public sealed class VisionPipelineException(
    string code,
    string message,
    Exception? innerException = null) : Exception(message, innerException)
{
    public string Code { get; } = code;
}
