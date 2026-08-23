using PuddingCode.Abstractions;
using PuddingCode.Models;

namespace PuddingCode.Core;

/// <summary>
/// 单次 LLM invocation 的图片请求预算（ADR-077 §3.2/§6.1）。
/// 当前为 inline-only 阶段：单图 2,000,000 bytes、整请求 40 MiB 解码后软上限；
/// 超限即 fail closed（Files API 落地后由 Planner 升级为 file_id 规划）。
/// </summary>
public sealed record VisionRequestPolicy
{
    /// <summary>产品上限：每请求最多图片数（DeepSeek 允许 600，产品收口为 8）。</summary>
    public int MaxImagesPerRequest { get; init; } = 8;

    /// <summary>inline 单图解码字节上限；超过必须走 Files API（未实现前 fail closed）。</summary>
    public long InlineMaxBytesPerImage { get; init; } = 2_000_000;

    /// <summary>整请求解码后图片总字节软上限（预留 JSON/header 余量）。</summary>
    public long InlineMaxTotalBytes { get; init; } = 40L * 1024 * 1024;

    /// <summary>每张图片 token 上界估计（DeepSeek 自动缩放后单图最多 384 tokens）。</summary>
    public int EstimatedTokensPerImageUpperBound { get; init; } = 384;

    public static VisionRequestPolicy Default { get; } = new();
}

/// <summary>已规划完成、可直接序列化为 provider input_image 的图片。</summary>
public sealed record PlannedVisualInput(
    string ArtifactId,
    string DataUri,
    string MimeType,
    string Detail,
    long SourceBytes);

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
            if (sourceBytes > policy.InlineMaxBytesPerImage)
                throw new VisionPipelineException(
                    VisionErrorCodes.RequestLimitExceeded,
                    $"Image artifact {part.ArtifactId} is {sourceBytes} bytes; the inline limit is " +
                    $"{policy.InlineMaxBytesPerImage} bytes (provider Files API is required beyond it).");

            totalBytes += sourceBytes;
            if (totalBytes > policy.InlineMaxTotalBytes)
                throw new VisionPipelineException(
                    VisionErrorCodes.RequestLimitExceeded,
                    $"This request references {totalBytes} decoded image bytes in total; " +
                    $"the inline request limit is {policy.InlineMaxTotalBytes} bytes.");

            var entry = new PlannedVisualInput(
                resolved.ArtifactId,
                resolved.DataUri,
                resolved.MimeType,
                part.Detail,
                sourceBytes);
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
}
