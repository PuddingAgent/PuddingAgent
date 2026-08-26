using PuddingCode.Abstractions;
using PuddingCode.Models;
using System.Security.Cryptography;

namespace PuddingCode.Core;

/// <summary>
/// 单次 LLM invocation 的图片请求预算（ADR-077 §3.2/§6.1）。
/// 当前为 inline-only 阶段：单图 2,000,000 bytes、整请求 40 MiB 解码后软上限；
/// 超限即 fail closed（Files API 落地后由 Planner 升级为 file_id 规划）。
/// Files 相关常量已按官方约束预留（64 MiB 单文件、lifetime 3600–2592000s），供 V3-S2 Planner 使用。
/// </summary>
public sealed record VisionRequestPolicy
{
    /// <summary>产品上限：每请求最多图片数（DeepSeek 允许 600，产品收口为 8）。</summary>
    public int MaxImagesPerRequest { get; init; } = 8;

    /// <summary>inline 单图解码字节上限；超过必须走 Files API（未实现前 fail closed）。</summary>
    public long InlineMaxBytesPerImage { get; init; } = 2_000_000;

    /// <summary>整请求解码后图片总字节软上限（预留 JSON/header 余量）。</summary>
    public long InlineMaxTotalBytes { get; init; } = 40L * 1024 * 1024;

    /// <summary>Files API 单文件字节上限（官方 64 MiB；任务书以官方为准，非 ADR §3.2 的 32 MiB）。</summary>
    public const long FilesMaxBytesPerImageLimit = 64L * 1024 * 1024;

    /// <summary>Files API lifetime 下限（官方 1 小时）。</summary>
    public const long FilesLifetimeMinLimit = 3_600;

    /// <summary>Files API lifetime 上限（官方 30 天 = 2,592,000 秒）。</summary>
    public const long FilesLifetimeMaxLimit = 2_592_000;

    /// <summary>Files API 单文件字节上限（默认官方 64 MiB）。</summary>
    public long FilesMaxBytesPerImage { get; init; } = FilesMaxBytesPerImageLimit;

    /// <summary>默认 file lifetime：7 天（官方 1h–30d 范围内；本地 Workspace Artifact 才是 durable source of truth）。</summary>
    public long FilesDefaultLifetimeSeconds { get; init; } = 7L * 24 * 3_600;

    /// <summary>file lifetime 下限（官方 1 小时）。</summary>
    public long FilesLifetimeMinSeconds { get; init; } = FilesLifetimeMinLimit;

    /// <summary>file lifetime 上限（官方 30 天）。</summary>
    public long FilesLifetimeMaxSeconds { get; init; } = FilesLifetimeMaxLimit;

    /// <summary>含 file_id 图片的整请求总字节上限（官方 200 MiB；Planner preflight 用）。</summary>
    public long FilesMaxTotalBytes { get; init; } = 200L * 1024 * 1024;

    /// <summary>每张图片 token 上界估计（DeepSeek 自动缩放后单图最多 384 tokens）。</summary>
    public int EstimatedTokensPerImageUpperBound { get; init; } = 384;

    public static VisionRequestPolicy Default { get; } = new();
}

/// <summary>
/// 已规划完成、可直接序列化为 provider input_image 的图片。
/// 两种互斥模式（ADR-077 §5.2/§6.1）：inline 模式 <see cref="DataUri"/> 非 null 且 <see cref="FileId"/> 为 null；
/// file 模式 <see cref="FileId"/> 非 null 且 <see cref="DataUri"/> 为 null（大图经 DeepSeek Files API 上传后以 file_id 引用）。
/// </summary>
public sealed record PlannedVisualInput(
    string ArtifactId,
    string? DataUri,
    string MimeType,
    string Detail,
    long SourceBytes,
    string? FileId = null,
    DateTimeOffset? ExpiresAt = null,
    string? ArtifactSha256 = null);

/// <summary>一次 invocation 的完整图片规划结果。</summary>
public sealed record VisualInputPlan(
    IReadOnlyList<PlannedVisualInput> Images,
    int EstimatedTokenUpperBound);

/// <summary>
/// LlmVisualInputPlanner（ADR-077 §5.2.3）：对全部待发图片执行授权解析与限制预检。
/// 只要一张失败，整个请求失败（不允许部分成功）；同一 (artifactId, detail) 在单请求内只解析一次。
/// </summary>
public static class LlmVisualInputPlanner
{
    public static async Task<VisualInputPlan> PlanAsync(
        string workspaceId,
        IReadOnlyList<LlmImagePart> imageParts,
        IVisualArtifactResolver resolver,
        VisionRequestPolicy? policy = null,
        IDeepSeekFilesUploader? fileUploader = null,
        IFileRefStore? fileRefStore = null,
        string? providerId = null,
        string? credentialEpoch = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(imageParts);
        ArgumentNullException.ThrowIfNull(resolver);
        policy ??= VisionRequestPolicy.Default;

        if (imageParts.Count == 0)
            return new VisualInputPlan([], 0);

        if (imageParts.Count > policy.MaxImagesPerRequest)
            throw new VisionPipelineException(
                VisionErrorCodes.RequestLimitExceeded,
                $"This request references {imageParts.Count} images; the product limit is {policy.MaxImagesPerRequest}.");

        var planned = new List<PlannedVisualInput>(imageParts.Count);
        var resolveCache = new Dictionary<(string ArtifactId, string Detail), PlannedVisualInput>(
            imageParts.Count);
        long totalBytes = 0;
        long fileTotalBytes = 0;

        foreach (var part in imageParts)
        {
            if (resolveCache.TryGetValue((part.ArtifactId, part.Detail), out var cached))
            {
                planned.Add(cached);
                continue;
            }

            VisualArtifactResolveResult? resolved;
            try
            {
                resolved = await resolver.ResolveAsync(workspaceId, part.ArtifactId, ct);
            }
            catch (Exception ex)
            {
                throw new VisionPipelineException(
                    VisionErrorCodes.ArtifactMissing,
                    $"Image artifact {part.ArtifactId} could not be read: {ex.Message}",
                    ex);
            }

            if (resolved is null)
                throw new VisionPipelineException(
                    VisionErrorCodes.ArtifactMissing,
                    $"Image artifact {part.ArtifactId} does not exist in workspace {workspaceId}.");

            var sourceBytes = EstimateDecodedBytes(resolved.DataUri);
            PlannedVisualInput entry;
            if (sourceBytes > policy.InlineMaxBytesPerImage)
            {
                // ADR-077 V3-S2a：大图走 Files API「上传即用」得到 file_id；无 uploader 时保持 fail closed。
                if (fileUploader is null)
                    throw new VisionPipelineException(
                        VisionErrorCodes.RequestLimitExceeded,
                        $"Image artifact {part.ArtifactId} is {sourceBytes} bytes; the inline limit is " +
                        $"{policy.InlineMaxBytesPerImage} bytes (provider Files API is required beyond it).");

                if (!DeepSeekFilesApiClient.IsSupportedImageMime(resolved.MimeType))
                    throw new VisionPipelineException(
                        VisionErrorCodes.MediaInvalid,
                        $"Image artifact {part.ArtifactId} has unsupported MIME type " +
                        $"'{resolved.MimeType}' for the provider Files API.");

                // Resolver 只返回 data URI，上传需要原始字节：base64 解码（与 EstimateDecodedBytes 对偶）。
                var rawBytes = DecodeDataUriBytes(resolved.DataUri);
                fileTotalBytes += rawBytes.Length;
                if (fileTotalBytes > policy.FilesMaxTotalBytes)
                    throw new VisionPipelineException(
                        VisionErrorCodes.RequestLimitExceeded,
                        $"This request references {fileTotalBytes} file-uploaded image bytes in total; " +
                        $"the provider Files request limit is {policy.FilesMaxTotalBytes} bytes.");

                // ADR-077 V3-S2b-2：store 三要素（store/providerId/credentialEpoch）齐全时先查复用，
                // 未命中上传并落库 ready，命中但过期则 MarkExpired + 重传一次；任一缺失退化为
                // S2a「上传即用」（不查 store、不落库）。ArtifactSha256 只参与 store 键比较，不打印。
                var hasStoreContext = fileRefStore is not null
                                      && !string.IsNullOrWhiteSpace(providerId)
                                      && !string.IsNullOrWhiteSpace(credentialEpoch);
                var artifactSha256 = hasStoreContext ? ComputeArtifactSha256(rawBytes) : null;
                var cachedRef = hasStoreContext
                    ? await fileRefStore!.TryGetReadyRefAsync(providerId!, credentialEpoch!, artifactSha256!, ct)
                    : null;

                var now = DateTimeOffset.UtcNow;
                if (cachedRef is not null)
                {
                    try
                    {
                        // store 保证近过期不分配，但返回后可能恰在边界过期：防御性校验，过期即重建一次。
                        DeepSeekFilesApiClient.ThrowIfFileExpired(cachedRef.ToReference(), now);
                        entry = new PlannedVisualInput(
                            resolved.ArtifactId,
                            DataUri: null,
                            resolved.MimeType,
                            part.Detail,
                            sourceBytes,
                            FileId: cachedRef.RemoteFileId,
                            ExpiresAt: cachedRef.ExpiresAt,
                            ArtifactSha256: artifactSha256);
                    }
                    catch (VisionPipelineException ex) when (ex.Code == VisionErrorCodes.ProviderFileExpired)
                    {
                        // 恰好一次 MarkExpired + 重传 + 落库 ready；重建后仍失败（含 ProviderFileExpired）
                        // 由上传客户端原样重抛，不盲目重试。
                        await fileRefStore!.MarkExpiredAsync(
                            providerId!,
                            credentialEpoch!,
                            artifactSha256!,
                            now,
                            ct);
                        var upload = await fileUploader.UploadAsync(
                            rawBytes,
                            resolved.MimeType,
                            policy.FilesDefaultLifetimeSeconds,
                            ct);
                        await fileRefStore.SaveAsync(
                            new ProviderFileRefRecord(
                                providerId!,
                                credentialEpoch!,
                                resolved.ArtifactId,
                                artifactSha256!,
                                upload.FileId,
                                rawBytes.Length,
                                resolved.MimeType,
                                upload.ExpiresAt,
                                LastUsedAt: now,
                                ProviderFileRefStatus.Ready,
                                CreatedAt: now,
                                UpdatedAt: now),
                            ct);
                        entry = new PlannedVisualInput(
                            resolved.ArtifactId,
                            DataUri: null,
                            resolved.MimeType,
                            part.Detail,
                            sourceBytes,
                            FileId: upload.FileId,
                            ExpiresAt: upload.ExpiresAt,
                            ArtifactSha256: artifactSha256);
                    }
                }
                else
                {
                    // 上传失败（含 ProviderFileUploadFailed）由 client 抛出，不在此额外包装。
                    var upload = await fileUploader.UploadAsync(
                        rawBytes,
                        resolved.MimeType,
                        policy.FilesDefaultLifetimeSeconds,
                        ct);
                    if (hasStoreContext)
                    {
                        await fileRefStore!.SaveAsync(
                            new ProviderFileRefRecord(
                                providerId!,
                                credentialEpoch!,
                                resolved.ArtifactId,
                                artifactSha256!,
                                upload.FileId,
                                rawBytes.Length,
                                resolved.MimeType,
                                upload.ExpiresAt,
                                LastUsedAt: now,
                                ProviderFileRefStatus.Ready,
                                CreatedAt: now,
                                UpdatedAt: now),
                            ct);
                    }
                    entry = new PlannedVisualInput(
                        resolved.ArtifactId,
                        DataUri: null,
                        resolved.MimeType,
                        part.Detail,
                        sourceBytes,
                        FileId: upload.FileId,
                        ExpiresAt: upload.ExpiresAt,
                        ArtifactSha256: artifactSha256);
                }
            }
            else
            {
                totalBytes += sourceBytes;
                if (totalBytes > policy.InlineMaxTotalBytes)
                    throw new VisionPipelineException(
                        VisionErrorCodes.RequestLimitExceeded,
                        $"This request references {totalBytes} decoded image bytes in total; " +
                        $"the inline request limit is {policy.InlineMaxTotalBytes} bytes.");

                entry = new PlannedVisualInput(
                    resolved.ArtifactId,
                    resolved.DataUri,
                    resolved.MimeType,
                    part.Detail,
                    sourceBytes);
            }
            resolveCache[(part.ArtifactId, part.Detail)] = entry;
            planned.Add(entry);
        }

        return new VisualInputPlan(
            planned,
            planned.Count * policy.EstimatedTokensPerImageUpperBound);
    }

    /// <summary>Base64 估算：不先构造超大字符串再判断。</summary>
    internal static long EstimateDecodedBytes(string dataUri)
    {
        var base64Length = dataUri.Contains(',', StringComparison.Ordinal)
            ? dataUri.Length - dataUri.IndexOf(',', StringComparison.Ordinal) - 1
            : dataUri.Length;
        return (long)Math.Ceiling(base64Length / 4.0) * 3;
    }

    /// <summary>把 resolver 返回的 data URI 反解码为原始字节（Files 上传用；与 <see cref="EstimateDecodedBytes"/> 对偶）。</summary>
    private static byte[] DecodeDataUriBytes(string dataUri)
    {
        var commaIndex = dataUri.IndexOf(',');
        if (!dataUri.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || commaIndex < 0)
            throw new VisionPipelineException(
                VisionErrorCodes.MediaInvalid,
                "Image data URI is malformed; a base64 payload is required for the provider Files API.");

        try
        {
            return Convert.FromBase64String(dataUri[(commaIndex + 1)..]);
        }
        catch (FormatException ex)
        {
            throw new VisionPipelineException(
                VisionErrorCodes.MediaInvalid,
                "Image data URI payload is not valid base64.",
                ex);
        }
    }

    /// <summary>
    /// ADR-077 V3-S2b-2：图片内容指纹，store 唯一键 <c>(provider_id, credential_epoch, artifact_sha256)</c>
    /// 的一部分。返回值只参与 store 键比较，不打印完整值（ADR-077 §8 泄漏约束）。
    /// </summary>
    internal static string ComputeArtifactSha256(byte[] rawBytes)
    {
        ArgumentNullException.ThrowIfNull(rawBytes);
        return Convert.ToHexString(SHA256.HashData(rawBytes)).ToLowerInvariant();
    }

    /// <summary>接受 data URI（内部解码后计算）或原始字节字符串；委托 <see cref="ComputeArtifactSha256(byte[])"/>。</summary>
    internal static string ComputeArtifactSha256(string dataUriOrBytes)
    {
        ArgumentNullException.ThrowIfNull(dataUriOrBytes);
        var bytes = dataUriOrBytes.Contains(',', StringComparison.Ordinal)
            ? DecodeDataUriBytes(dataUriOrBytes)
            : System.Text.Encoding.UTF8.GetBytes(dataUriOrBytes);
        return ComputeArtifactSha256(bytes);
    }
}
