using PuddingCode.Abstractions;
using PuddingCode.Models;
using System.Security.Cryptography;

namespace PuddingCode.Core;

/// <summary>
/// 单次 LLM invocation 的图片请求预算（ADR-077 §3.2/§6.1）。
/// 当前为 inline-only 阶段：单图 2,000,000 bytes 解码后上限、inline 累计 40 MiB 解码后软上限；
/// 累计上限按单次 invocation（单条消息的图片集）逐消息校验，尚非跨消息整请求聚合。
/// 超限即 fail closed（Files API 落地后由 Planner 升级为 file_id 规划）。
/// Files 相关常量已按官方约束预留（64 MiB 单文件、lifetime 3600–2592000s），供 V3-S2 Planner 使用。
/// </summary>
public sealed record VisionRequestPolicy
{
    /// <summary>产品上限：单次 invocation（per-invocation）最多图片数（DeepSeek 官方允许 600，产品收口为 8）。</summary>
    public int MaxImagesPerRequest { get; init; } = 8;

    /// <summary>inline 单图解码后字节上限（原始字节，非 base64 编码/wire 体积）；超过必须走 Files API（未实现前 fail closed）。
    /// 2 MB 是产品门槛（inline/file 模式分界），非协议限制；协议限制是 Files API 单文件 64 MiB（官方）。</summary>
    public long InlineMaxBytesPerImage { get; init; } = 2_000_000;

    /// <summary>inline 累计解码后字节软上限（单图之和，预留 JSON/header 余量）。当前实现为逐消息（per-invocation）累计校验而非跨消息整请求聚合；整请求统一预算为后续切片。
    /// 40 MiB 是产品门槛，非协议限制；协议限制是 Files API 单文件 64 MiB（官方）、累计 200 MiB（官方）。</summary>
    public long InlineMaxTotalBytes { get; init; } = 40L * 1024 * 1024;

    /// <summary>
    /// inline wire 字节请求级上限（仅传入 <see cref="VisualInputRequestBudget"/> 时生效）。
    /// 口径 = data URI 字符串 UTF-8 字节（"data:" 前缀 + mime + ";base64," + base64 4×ceil(decoded/3)）。
    /// 默认 64 MiB：40 MiB 解码后对应约 53.3 MiB base64 wire，余量留给 JSON envelope。产品门槛，非协议限制。
    /// </summary>
    public long InlineMaxTotalWireBytes { get; init; } = 64L * 1024 * 1024;

    /// <summary>Files API 单文件字节上限（官方 64 MiB；任务书以官方为准，非 ADR §3.2 的 32 MiB）。</summary>
    public const long FilesMaxBytesPerImageLimit = 64L * 1024 * 1024;

    /// <summary>Files API lifetime 下限（官方 1 小时）。</summary>
    public const long FilesLifetimeMinLimit = 3_600;

    /// <summary>Files API lifetime 上限（官方 30 天 = 2,592,000 秒）。</summary>
    public const long FilesLifetimeMaxLimit = 2_592_000;

    /// <summary>Files API 单文件字节上限（上传的原始编码字节，默认官方 64 MiB；与 inline 解码后口径区分）。</summary>
    public long FilesMaxBytesPerImage { get; init; } = FilesMaxBytesPerImageLimit;

    /// <summary>默认 file lifetime：7 天（官方 1h–30d 范围内；本地 Workspace Artifact 才是 durable source of truth）。</summary>
    public long FilesDefaultLifetimeSeconds { get; init; } = 7L * 24 * 3_600;

    /// <summary>file lifetime 下限（官方 1 小时）。</summary>
    public long FilesLifetimeMinSeconds { get; init; } = FilesLifetimeMinLimit;

    /// <summary>file lifetime 上限（官方 30 天）。</summary>
    public long FilesLifetimeMaxSeconds { get; init; } = FilesLifetimeMaxLimit;

    /// <summary>含 file_id 图片的累计 wire 体积上限（官方 200 MiB；Planner preflight 用）。当前实现为逐消息（per-invocation）累计校验而非跨消息整请求聚合；整请求统一预算为后续切片。</summary>
    public long FilesMaxTotalBytes { get; init; } = 200L * 1024 * 1024;

    /// <summary>
    /// 每张图片的 token 消耗保守上界估计（仅用于 Run 快照与请求 manifest 记录，不参与任何硬校验）。
    /// 依据（2026-09-12 核验）：DeepSeek 官方文档当前公开的单图 token 上界为 1024；
    /// GLM 具备原生多模态但未公开图像 token 明细、本机 usage 亦无图像分量，故沿用同一保守上界（该项未逐项验证）。
    /// 版本标识见 <see cref="ImageTokenEstimatorVersion"/>。
    /// </summary>
    public int EstimatedTokensPerImageUpperBound { get; init; } = 1024;

    /// <summary>token 估计值对应的模型/日期/来源版本（当前 "deepseek-2026-09-12-1024"），供 Run 快照与请求 manifest 记录。</summary>
    public string ImageTokenEstimatorVersion { get; init; } = "deepseek-2026-09-12-1024";

    /// <summary>
    /// 请求级 token 估计上限（仅传入 <see cref="VisualInputRequestBudget"/> 时生效；账本累计 = 份数 × 本策略单图上界）。
    /// null（默认）= 只累计不硬校验，由份数上限兜底；注入小值可触发 token 维度拦截（测试口径）。
    /// 估计值无 provider usage 明细支撑（usage.input_tokens_details 只含 cached_tokens），仅为产品侧护栏。
    /// </summary>
    public int? EstimatedTokensPerRequestUpperBound { get; init; } = null;

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

/// <summary>一次 invocation 的完整图片规划结果。请求级聚合字段仅在调用方传入 <see cref="VisualInputRequestBudget"/> 时填充。</summary>
public sealed record VisualInputPlan(
    IReadOnlyList<PlannedVisualInput> Images,
    int EstimatedTokenUpperBound,
    long? RequestScopedEstimatedTokens = null,
    string? ImageTokenEstimatorVersion = null);

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
        VisualInputRequestBudget? budget = null,
        string? budgetSource = null,
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

        // V5 切片二：请求级账本批次开始（一次 PlanAsync = 一条消息的图片集，由 Gateway 传入 budgetSource
        // 标注来源与消息序号）。budget == null 时保持旧行为：仅下方 per-invocation 校验，账本不参与。
        budget?.BeginBatch(budgetSource);

        var planned = new List<PlannedVisualInput>(imageParts.Count);
        var resolveCache = new Dictionary<(string ArtifactId, string Detail), PlannedVisualInput>(
            imageParts.Count);
        long totalBytes = 0;
        long fileTotalBytes = 0;

        foreach (var part in imageParts)
        {
            if (resolveCache.TryGetValue((part.ArtifactId, part.Detail), out var cached))
            {
                // 解析缓存只影响 resolver 调用次数；预算按实际序列化份数计，缓存命中份同样计费。
                ChargeBudget(budget, cached, part.ArtifactId);
                planned.Add(cached);
                continue;
            }

            VisualArtifactResolveResult? resolved;
            try
            {
                resolved = await resolver.ResolveAsync(workspaceId, part.ArtifactId, ct, part.Detail);
            }
            catch (VisionPipelineException) { throw; }
            catch (OperationCanceledException) { throw; }
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
            // file 与 inline 两个分支的公共出口：先把这一份 charge 进请求级账本（越界即整请求失败），
            // 再落入解析缓存与规划结果。不存在超限丢弃部分图片仍返回 plan 的路径。
            ChargeBudget(budget, entry, part.ArtifactId);
            resolveCache[(part.ArtifactId, part.Detail)] = entry;
            planned.Add(entry);
        }

        return new VisualInputPlan(
            planned,
            planned.Count * policy.EstimatedTokensPerImageUpperBound,
            budget?.EstimatedTokens,
            policy.ImageTokenEstimatorVersion);
    }

    /// <summary>
    /// V5 切片二：把一份实际序列化的图片 charge 进请求级账本（budget 为 null 时不参与，保持旧行为）。
    /// inline 模式计解码后字节 + wire 字节（data URI UTF-8 字节）；file 模式计上传原始编码字节——
    /// 此处用规划期估算的 SourceBytes（与精确解码差 &lt; 3 字节，估算口径对 cache 命中份与新建份保持一致）。
    /// </summary>
    private static void ChargeBudget(VisualInputRequestBudget? budget, PlannedVisualInput entry, string artifactId)
    {
        if (budget is null)
            return;
        if (entry.FileId is null)
            budget.ChargeInline(artifactId, entry.SourceBytes, System.Text.Encoding.UTF8.GetByteCount(entry.DataUri!));
        else
            budget.ChargeFile(artifactId, entry.SourceBytes);
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
