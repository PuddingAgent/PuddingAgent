namespace PuddingCode.Core;

/// <summary>
/// ADR-077 §6.2：远端 Provider File 引用生命周期状态。
/// 持久化为 snake_case 字符串 wire 值（uploading/ready/delete_pending/expired/failed），
/// 与 <c>llm_provider_file_refs</c> 表 <c>status</c> 列严格一致。
/// </summary>
public enum ProviderFileRefStatus
{
    /// <summary>上传进行中（本步骤仅保留语义；S2b-2 上传成功后 SaveAsync 以 ready 落库）。</summary>
    Uploading,

    /// <summary>已就绪，可跨轮复用（<see cref="IFileRefStore.TryGetReadyRefAsync"/> 唯一返回的状态）。</summary>
    Ready,

    /// <summary>已标记待清理（S2b-3 worker 删除远端引用后物理删除行）。</summary>
    DeletePending,

    /// <summary>已过期（远端引用失效，需要重建时先置为 expired 再重新上传）。</summary>
    Expired,

    /// <summary>上传/复用失败（保留事实供诊断，不参与复用）。</summary>
    Failed,
}

/// <summary>ProviderFileRefStatus 与数据库 wire 字符串的映射（ADR-077 §6.2）。</summary>
public static class ProviderFileRefStatusWire
{
    public const string Uploading = "uploading";
    public const string Ready = "ready";
    public const string DeletePending = "delete_pending";
    public const string Expired = "expired";
    public const string Failed = "failed";

    /// <summary>枚举 → wire 字符串（写库用）。</summary>
    public static string ToWire(ProviderFileRefStatus status) => status switch
    {
        ProviderFileRefStatus.Uploading => Uploading,
        ProviderFileRefStatus.Ready => Ready,
        ProviderFileRefStatus.DeletePending => DeletePending,
        ProviderFileRefStatus.Expired => Expired,
        ProviderFileRefStatus.Failed => Failed,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown provider file ref status."),
    };

    /// <summary>wire 字符串 → 枚举（读库用）。</summary>
    public static ProviderFileRefStatus FromWire(string value) => value switch
    {
        Uploading => ProviderFileRefStatus.Uploading,
        Ready => ProviderFileRefStatus.Ready,
        DeletePending => ProviderFileRefStatus.DeletePending,
        Expired => ProviderFileRefStatus.Expired,
        Failed => ProviderFileRefStatus.Failed,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown provider file ref status wire value."),
    };
}

/// <summary>
/// ADR-077 §6.2：<c>llm_provider_file_refs</c> 表的行值类型。
/// 本地 Artifact 永久身份（<see cref="ArtifactId"/>/<see cref="ArtifactSha256"/>）与
/// Provider 侧 file_id 生命周期（<see cref="RemoteFileId"/>/<see cref="ExpiresAt"/>）严格分开：
/// Provider 文件消失只会触发重新上传，不能删除聊天图片或修改 Conversation fact。
/// <see cref="RemoteFileId"/> 是 provider 侧凭证，**绝不**出现在普通日志/异常/诊断包（ADR-077 §8）。
/// </summary>
public sealed record ProviderFileRefRecord(
    string ProviderId,
    string CredentialEpoch,
    string ArtifactId,
    string ArtifactSha256,
    string RemoteFileId,
    long Bytes,
    string MimeType,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? LastUsedAt,
    ProviderFileRefStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    /// <summary>映射为 Planner/Gateway 使用的轻量引用（FileId/MimeType/ExpiresAt）。</summary>
    public ProviderFileReference ToReference()
        => new(RemoteFileId, MimeType, ExpiresAt);
}
