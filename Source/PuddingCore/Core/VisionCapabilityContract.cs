namespace PuddingCode.Core;

/// <summary>
/// 模型级视觉能力合同（V5 切片三 T2；ADR-088 决策 2/5，设计 §4.1「有效能力快照」的来源与投影）。
/// 唯一来源：data/config/llm.providers.json 模型条目的可选 "vision" 节，启动时由
/// PuddingFileConfigLoader 校验（存在即必须完整有效，fail-fast）、PuddingFileLlmConfigService.GetAllModels()
/// 投影到 LlmModelInfo.VisionContract，AgentExecutionSnapshotFactory 生成快照时解析为
/// VisionRequestPolicy。配置优先、版本化生效，不做在线探测。
/// 本合同只表达「支持图片输入时的预算策略值」；模型是否支持图片输入由模型类别与
/// capabilityTags("vision") 判定（快照工厂负责：embedding / image-generation / 无 vision 标签
/// 一律不投影策略，防止误标）。字节口径在字段名中显式区分：*BytesPerImage / *TotalBytes 为
/// 解码后原始字节，*WireBytes 为 base64 data URI 的 wire 字节。
/// </summary>
public sealed record VisionCapabilityContract
{
    /// <summary>合同版本（必填，如 "deepseek-2026-09-12-1024"）；投影为 VisionRequestPolicy.ImageTokenEstimatorVersion。</summary>
    public required string Version { get; init; }

    /// <summary>单请求图片份数上限（产品门槛口径；DeepSeek 官方允许 600，产品收口值见 VisionRequestPolicy）。</summary>
    public int? MaxImagesPerRequest { get; init; }

    /// <summary>inline 单图「解码后」字节上限。</summary>
    public long? InlineMaxBytesPerImage { get; init; }

    /// <summary>inline 累计「解码后」字节上限。</summary>
    public long? InlineMaxTotalBytes { get; init; }

    /// <summary>inline 累计「wire 字节」上限（base64 data URI，UTF-8）。</summary>
    public long? InlineMaxTotalWireBytes { get; init; }

    /// <summary>Files 单文件「上传原始编码」字节上限。</summary>
    public long? FilesMaxBytesPerImage { get; init; }

    /// <summary>Files 累计「wire 字节」上限。</summary>
    public long? FilesMaxTotalBytes { get; init; }

    /// <summary>单图 token 保守上界（仅用于快照与请求 manifest 记录，不参与硬校验）。</summary>
    public int? EstimatedTokensPerImageUpperBound { get; init; }

    /// <summary>
    /// 投影为运行策略：显式配置值覆盖产品默认，未配置字段沿用 <see cref="VisionRequestPolicy.Default"/>；
    /// 版本取合同 Version（快照日志与 manifest 据此区分「当前执行版本」）。
    /// </summary>
    public VisionRequestPolicy ToPolicy() => new()
    {
        MaxImagesPerRequest = MaxImagesPerRequest ?? VisionRequestPolicy.Default.MaxImagesPerRequest,
        InlineMaxBytesPerImage = InlineMaxBytesPerImage ?? VisionRequestPolicy.Default.InlineMaxBytesPerImage,
        InlineMaxTotalBytes = InlineMaxTotalBytes ?? VisionRequestPolicy.Default.InlineMaxTotalBytes,
        InlineMaxTotalWireBytes = InlineMaxTotalWireBytes ?? VisionRequestPolicy.Default.InlineMaxTotalWireBytes,
        FilesMaxBytesPerImage = FilesMaxBytesPerImage ?? VisionRequestPolicy.Default.FilesMaxBytesPerImage,
        FilesMaxTotalBytes = FilesMaxTotalBytes ?? VisionRequestPolicy.Default.FilesMaxTotalBytes,
        EstimatedTokensPerImageUpperBound = EstimatedTokensPerImageUpperBound ?? VisionRequestPolicy.Default.EstimatedTokensPerImageUpperBound,
        ImageTokenEstimatorVersion = Version,
    };
}
