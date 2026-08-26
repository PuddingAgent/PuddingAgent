using System.Net.Http.Headers;
using System.Text.Json;

namespace PuddingCode.Core;

/// <summary>DeepSeek Files API 上传器抽象（ADR-077 §3.3）；便于 V3-S2 注入 fake/装饰实现。</summary>
public interface IDeepSeekFilesUploader
{
    Task<ProviderFileUploadResult> UploadAsync(
        byte[] imageBytes,
        string mimeType,
        long lifetimeSeconds,
        CancellationToken ct = default);
}

/// <summary>
/// DeepSeek Files API 上传客户端（ADR-077 §3.3）：把图片以 multipart/form-data 上传到
/// provider 的 Files 端点（<c>purpose=user_data</c>），返回 <see cref="ProviderFileUploadResult"/>。
/// ApiKey 只存在于本类内部字段，绝不进入任何 DTO/日志/异常 message；
/// 异常 message 只含 HTTP status + 脱敏后的 provider 错误摘要。
/// </summary>
public sealed class DeepSeekFilesApiClient : IDeepSeekFilesUploader
{
    /// <summary>DeepSeek Files API 端点（POST /files，multipart/form-data，purpose=user_data）。</summary>
    public const string DefaultFilesEndpoint = "https://api.deepseek.com/files";

    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly string _filesEndpoint;
    private readonly VisionRequestPolicy _policy;

    /// <param name="httpClient">调用方生命周期管理的 HttpClient。</param>
    /// <param name="apiKey">当前 LLM route 的 Secret；只存于内部字段。</param>
    /// <param name="policy">上传限制策略；默认 <see cref="VisionRequestPolicy.Default"/>。</param>
    /// <param name="filesEndpoint">Files 上传端点；默认 <see cref="DefaultFilesEndpoint"/>。</param>
    public DeepSeekFilesApiClient(
        HttpClient httpClient,
        string apiKey,
        VisionRequestPolicy? policy = null,
        string? filesEndpoint = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("A DeepSeek API key is required.", nameof(apiKey));
        _httpClient = httpClient;
        _apiKey = apiKey;
        _policy = policy ?? VisionRequestPolicy.Default;
        _filesEndpoint = filesEndpoint ?? DefaultFilesEndpoint;
    }

    public async Task<ProviderFileUploadResult> UploadAsync(
        byte[] imageBytes,
        string mimeType,
        long lifetimeSeconds,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(imageBytes);
        if (imageBytes.Length == 0 || !IsSupportedImageMime(mimeType))
            throw new VisionPipelineException(
                VisionErrorCodes.MediaInvalid,
                "DeepSeek Files upload requires a non-empty JPEG/PNG/GIF/WebP image.");

        if (imageBytes.Length > _policy.FilesMaxBytesPerImage)
            throw new VisionPipelineException(
                VisionErrorCodes.RequestLimitExceeded,
                $"Image is {imageBytes.Length} bytes; the provider Files limit is " +
                $"{_policy.FilesMaxBytesPerImage} bytes per file.");

        // 官方 lifetime 3600–2592000 秒（1h–30d）；越界钳制到合法范围，不静默使用非法值。
        var lifetime = ClampLifetime(lifetimeSeconds);

        using var content = BuildMultipartContent(imageBytes, mimeType, lifetime);
        using var request = new HttpRequestMessage(HttpMethod.Post, _filesEndpoint)
        {
            Content = content,
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        using var response = await _httpClient.SendAsync(request, ct);
        var responseBody = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new VisionPipelineException(
                VisionErrorCodes.ProviderFileUploadFailed,
                $"DeepSeek Files upload failed (HTTP {(int)response.StatusCode}): " +
                $"{SanitizeProviderError(responseBody)}");
        }

        return ParseUploadResponse(responseBody, mimeType, imageBytes.Length, lifetime);
    }

    /// <summary>
    /// file_id 失效时的 fail-closed 重建路径（ADR-077 §9.1）。本步骤只定义 helper，
    /// 不落地持久化 store；V3-S2 引入 store 后由 Planner 在调用前校验。
    /// 异常 message 刻意不含 FileId（ADR §8：provider file_id 不进日志/诊断包）。
    /// </summary>
    public static void ThrowIfFileExpired(ProviderFileReference reference, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (reference.ExpiresAt <= now)
        {
            throw new VisionPipelineException(
                VisionErrorCodes.ProviderFileExpired,
                $"Provider file reference expired at {reference.ExpiresAt:O}; " +
                "re-upload via the Files API before retrying the vision request.");
        }
    }

    /// <summary>官方支持的上传图片格式。</summary>
    public static bool IsSupportedImageMime(string? mimeType)
        => !string.IsNullOrWhiteSpace(mimeType)
           && SupportedImageMimes.Contains(mimeType, StringComparer.OrdinalIgnoreCase);

    private static readonly string[] SupportedImageMimes = ["image/jpeg", "image/png", "image/gif", "image/webp"];

    private long ClampLifetime(long lifetimeSeconds)
        => Math.Clamp(lifetimeSeconds, _policy.FilesLifetimeMinSeconds, _policy.FilesLifetimeMaxSeconds);

    private MultipartFormDataContent BuildMultipartContent(byte[] imageBytes, string mimeType, long lifetime)
    {
        var content = new MultipartFormDataContent();
        content.Add(new StringContent("user_data"), "purpose");

        var fileContent = new ByteArrayContent(imageBytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(mimeType);
        content.Add(fileContent, "file", $"vision-{Guid.NewGuid():N}.{ExtensionForMime(mimeType)}");

        content.Add(
            new StringContent(lifetime.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            "lifetime");
        return content;
    }

    private static ProviderFileUploadResult ParseUploadResponse(
        string responseBody,
        string mimeType,
        long sourceBytes,
        long lifetime)
    {
        using var doc = JsonDocument.Parse(responseBody);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw UploadFailed("the provider returned a non-object upload response.");

        var fileId = TryGetString(root, "id") ?? TryGetString(root, "file_id");
        if (string.IsNullOrWhiteSpace(fileId))
            throw UploadFailed("the provider upload response did not contain a file id.");

        var uploadedAt = root.TryGetProperty("created_at", out var created) && created.TryGetInt64(out var unix)
            ? DateTimeOffset.FromUnixTimeSeconds(unix)
            : DateTimeOffset.UtcNow;

        return new ProviderFileUploadResult(fileId, mimeType, sourceBytes, lifetime, uploadedAt);
    }

    private static VisionPipelineException UploadFailed(string detail)
        => new(
            VisionErrorCodes.ProviderFileUploadFailed,
            $"DeepSeek Files upload failed: {detail}");

    /// <summary>脱敏：只提取 JSON error/message 摘要并截断；非 JSON 不贴原文。</summary>
    private string SanitizeProviderError(string responseBody)
    {
        var summary = TryExtractErrorMessage(responseBody);
        if (string.IsNullOrWhiteSpace(summary))
            return "the provider returned no readable error summary.";

        // 即使 provider 把 key 回显进错误信息也强制替换，杜绝泄漏。
        return summary.Replace(_apiKey, "***", StringComparison.Ordinal);
    }

    private static string? TryExtractErrorMessage(string responseBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;
            var message = TryGetString(root, "message");
            if (message is not null)
                return Truncate(message, 300);
            if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                var errorMessage = TryGetString(error, "message");
                if (errorMessage is not null)
                    return Truncate(errorMessage, 300);
            }
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Truncate(string value, int maxChars)
        => value.Length <= maxChars ? value : value[..maxChars] + "…";

    private static string? TryGetString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;

    private static string ExtensionForMime(string mimeType) => mimeType.ToLowerInvariant() switch
    {
        "image/jpeg" => "jpg",
        "image/png" => "png",
        "image/gif" => "gif",
        "image/webp" => "webp",
        _ => "img",
    };
}
