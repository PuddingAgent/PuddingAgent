namespace PuddingCode.Core;

/// <summary>
/// DeepSeek Files API 上传成功后的值类型结果（ADR-077 §3.3）。
/// <see cref="FileId"/> 是 provider 侧唯一凭证；ApiKey 永不进入任何 DTO/日志/异常 message。
/// </summary>
public sealed record ProviderFileUploadResult(
    string FileId,
    string MimeType,
    long SourceBytes,
    long LifetimeSeconds,
    DateTimeOffset UploadedAt)
{
    /// <summary>按上传时刻 + lifetime 计算的有效期（UTC）。</summary>
    public DateTimeOffset ExpiresAt => UploadedAt.AddSeconds(LifetimeSeconds);
}

/// <summary>
/// 供 Planner/Gateway 使用的轻量 file 引用值类型（V3-S2 将把 FileId 引入 <see cref="PlannedVisualInput"/>）。
/// 本步骤只定义类型与有效期语义，不落地持久化 store / 清理 worker。
/// </summary>
public sealed record ProviderFileReference(
    string FileId,
    string MimeType,
    DateTimeOffset ExpiresAt);
