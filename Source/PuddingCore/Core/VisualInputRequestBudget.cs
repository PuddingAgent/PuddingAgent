namespace PuddingCode.Core;

/// <summary>
/// 一次 LLM 请求（一次 payload 构造）的图片预算账本（V5 切片二 T1，ADR-077 §3.2「整请求聚合」）。
/// 三个 Gateway 在构造本次 payload 的入口创建一个实例，以显式参数贯穿该请求全部
/// <see cref="LlmVisualInputPlanner.PlanAsync"/> 调用（user <c>input_image</c> 与工具
/// <c>function_call_output</c> 图片共用同一账本），按整个最终请求校验五个维度：
/// 图片份数、inline 解码后字节、inline wire（data URI）字节、token 估计、file 上传字节。
/// 单条消息自身已通过 per-invocation 校验（模型图片数 / 32 MiB / 64 MiB / 200 MiB）后，
/// 分散在多条历史消息里的图片合计超限（如300+300+1=601张 &gt; 600）在此被拦截——这是
/// per-invocation 校验覆盖不到的缺陷面。
/// 账本用显式参数传递（禁止 AsyncLocal / 全局静态可变状态，避免与执行快照冻结语义冲突）；
/// 累计值只增不减；任一维度越界即抛 <see cref="VisionPipelineException"/>（Code=
/// <see cref="VisionErrorCodes.RequestLimitExceeded"/>），整请求失败、不发 HTTP，绝不静默丢图。
/// </summary>
public sealed class VisualInputRequestBudget
{
    private readonly VisionRequestPolicy _policy;
    private int _batchOrdinal;
    private string _currentSource = "unspecified";

    public VisualInputRequestBudget(VisionRequestPolicy? policy = null, int? totalImageCount = null, long? preparationMaxBytes = null)
    {
        _policy = policy ?? VisionRequestPolicy.Default;
        TotalImageCount = totalImageCount;
        if (totalImageCount > _policy.MaxImagesPerRequest)
            throw new VisionPipelineException(VisionErrorCodes.RequestLimitExceeded,
                $"Request has {totalImageCount} images; policy limit {_policy.MaxImagesPerRequest}.",
                userMessage: $"本次请求包含 {totalImageCount} 份图片，超过上限 {_policy.MaxImagesPerRequest}。请减少图片或分批处理；你仍可继续发送文字消息。");
        var count = Math.Max(1, totalImageCount ?? 1);
        PreparationMaxBytes = Math.Min(_policy.InlineMaxBytesPerImage,
            Math.Min(_policy.InlineMaxTotalBytes / count,
                preparationMaxBytes ?? Math.Max(1, _policy.InlineMaxTotalWireBytes / count * 3 / 4 * 9 / 10)));
    }

    public int? TotalImageCount { get; }
    public int PreparationMaxEdge => (TotalImageCount ?? ImageCount + 1) >= 15 ? 4096 : 8192;
    public long PreparationMaxBytes { get; }
    public long SerializedImageBytes { get; private set; }

    public static VisualInputRequestBudget ForMessages(VisionRequestPolicy? policy,
        IReadOnlyList<PuddingCode.Models.ChatMessage> messages, long? preparationMaxBytes = null)
        => new(policy, messages.Sum(m => PuddingCode.Models.ChatMessageMultimodalNormalizer.GetImageParts(m).Count), preparationMaxBytes);

    internal void ValidateDimensions(string artifactId, int? width, int? height)
    {
        if (width > PreparationMaxEdge || height > PreparationMaxEdge)
            throw new VisionPipelineException(VisionErrorCodes.RequestLimitExceeded,
                $"Image {artifactId} is {width}x{height}; this request permits an edge of {PreparationMaxEdge} pixels after preprocessing.",
                userMessage: $"图片尺寸为 {width}×{height}，本次请求允许的最长边为 {PreparationMaxEdge} 像素。自动缩放未能满足要求，请缩小图片后重试；仍可继续发送文字消息。");
    }

    internal void RecordSerializedImage(string dataUri)
        => SerializedImageBytes += System.Text.Encoding.UTF8.GetByteCount(System.Text.Json.JsonSerializer.Serialize(dataUri));

    /// <summary>已计入的图片份数（实际序列化份数；同 artifactId/detail 重复引用按份数重复计费——解析缓存只影响解析，不影响计费）。</summary>
    public int ImageCount { get; private set; }

    /// <summary>inline 模式解码后原始字节累计（与 per-invocation <see cref="VisionRequestPolicy.InlineMaxTotalBytes"/> 同口径）。</summary>
    public long InlineDecodedBytes { get; private set; }

    /// <summary>
    /// inline 模式 wire 字节累计。口径 = 整个 data URI 字符串的 UTF-8 字节数：
    /// <c>data:</c> 前缀 + mime 段 + <c>;base64,</c> + base64（4×ceil(decoded/3)）。
    /// JSON 序列化转义余量不在此口径（由 envelope 层承担）。
    /// </summary>
    public long InlineWireBytes { get; private set; }

    /// <summary>file 模式上传原始编码字节累计（对应 <see cref="VisionRequestPolicy.FilesMaxTotalBytes"/> 的 200 MiB 请求级口径）。</summary>
    public long FileUploadBytes { get; private set; }

    /// <summary>
    /// token 估计累计 = 份数 × <see cref="VisionRequestPolicy.EstimatedTokensPerImageUpperBound"/>
    /// （本地估计；provider usage 无图像 token 明细，见 ADR-088 §遗留）。
    /// </summary>
    public long EstimatedTokens { get; private set; }

    /// <summary>
    /// 开启一次规划批次（一次 <c>PlanAsync</c> 调用 = 一条消息的图片集），返回 1-based 批次序号。
    /// 批次序号与来源描述进入越界异常消息，用于定位是哪条消息的哪类图片触发拦截。
    /// </summary>
    internal int BeginBatch(string? sourceDescription)
    {
        _currentSource = string.IsNullOrWhiteSpace(sourceDescription)
            ? "unspecified"
            : sourceDescription.Trim();
        return ++_batchOrdinal;
    }

    /// <summary>inline 模式计费一份图：份数 +1、解码字节、wire 字节、token 估计；任一维度越界即抛。</summary>
    internal void ChargeInline(string artifactId, long decodedBytes, long wireBytes)
    {
        ChargeCount(artifactId);
        ThrowIfExceeds("inline decoded bytes", InlineDecodedBytes, decodedBytes, _policy.InlineMaxTotalBytes, artifactId);
        if (FileUploadBytes > 0)
            ThrowIfExceeds("combined image bytes", InlineDecodedBytes + FileUploadBytes, decodedBytes, _policy.FilesMaxTotalBytes, artifactId);
        InlineDecodedBytes += decodedBytes;
        ThrowIfExceeds("inline wire bytes", InlineWireBytes, wireBytes, _policy.InlineMaxTotalWireBytes, artifactId);
        InlineWireBytes += wireBytes;
        ChargeTokens(artifactId);
    }

    /// <summary>file 模式计费一份图：份数 +1、上传字节、token 估计；任一维度越界即抛。</summary>
    internal void ChargeFile(string artifactId, long uploadBytes)
    {
        ChargeCount(artifactId);
        ThrowIfExceeds("file uploaded bytes", FileUploadBytes, uploadBytes, _policy.FilesMaxTotalBytes, artifactId);
        ThrowIfExceeds("combined image bytes", InlineDecodedBytes + FileUploadBytes, uploadBytes, _policy.FilesMaxTotalBytes, artifactId);
        FileUploadBytes += uploadBytes;
        ChargeTokens(artifactId);
    }

    private void ChargeCount(string artifactId)
    {
        ThrowIfExceeds("image count", ImageCount, 1, _policy.MaxImagesPerRequest, artifactId);
        ImageCount += 1;
    }

    private void ChargeTokens(string artifactId)
    {
        // EstimatedTokensPerRequestUpperBound 为 null（默认）时只累计不硬校验，由份数上限兜底；
        // 注入小值可触发 token 维度拦截（测试口径）。
        if (_policy.EstimatedTokensPerRequestUpperBound is { } limit)
            ThrowIfExceeds("estimated tokens", EstimatedTokens, _policy.EstimatedTokensPerImageUpperBound, limit, artifactId);
        EstimatedTokens += _policy.EstimatedTokensPerImageUpperBound;
    }

    private void ThrowIfExceeds(string dimension, long cumulative, long incoming, long limit, string artifactId)
    {
        if (cumulative + incoming > limit)
            throw new VisionPipelineException(
                VisionErrorCodes.RequestLimitExceeded,
                $"Request-scoped vision budget exceeded [{dimension}] at artifact '{artifactId}': " +
                $"cumulative {cumulative} + incoming {incoming} > policy limit {limit}; " +
                $"source={_currentSource}; planning-batch=#{_batchOrdinal}. " +
                "Each message passed its per-invocation limits individually; the whole request is " +
                "rejected without dropping any image (fail closed).");
    }
}
