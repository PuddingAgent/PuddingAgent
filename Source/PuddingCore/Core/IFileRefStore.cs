namespace PuddingCode.Core;

/// <summary>
/// ADR-077 §6.2：Provider File 引用持久化 store（<c>llm_provider_file_refs</c>）。
/// <para>
/// 唯一键 = <c>(provider_id, credential_epoch, artifact_sha256)</c>；只存远端引用事实，
/// 不持有 Secret（apiKey 永不进入 store/DTO/日志）。<see cref="RemoteFileId"/> 不进普通日志。
/// 本接口供 S2b-2（Planner 跨轮复用 file_id）与 S2b-3（清理 worker）消费。
/// </para>
/// </summary>
public interface IFileRefStore
{
    /// <summary>距离过期不足该秒数时不再分配给新 invocation（ADR §6.2「近过期不分配」）。</summary>
    const int FileRefNearExpirySkewSeconds = 300;

    /// <summary>
    /// 返回可复用的就绪远端引用；无匹配（未上传 / 非 ready / 已过期 / 近过期）时返回 null，
    /// 调用方视为「需重新上传」。
    /// </summary>
    Task<ProviderFileRefRecord?> TryGetReadyRefAsync(
        string providerId,
        string credentialEpoch,
        string artifactSha256,
        CancellationToken ct = default);

    /// <summary>
    /// 幂等 upsert（按唯一键）。并发以 BEGIN IMMEDIATE 夹具降低竞争，
    /// 同一唯一键并发写入不会产生重复行。
    /// </summary>
    Task<ProviderFileRefRecord> SaveAsync(
        ProviderFileRefRecord record,
        CancellationToken ct = default);

    /// <summary>
    /// 续期（供过期重建后回写新 expires_at）；仅 <c>ready</c> 状态可续期。
    /// 成功返回更新后的记录；键不存在或状态不满足 CAS 时返回 null。
    /// </summary>
    Task<ProviderFileRefRecord?> UpdateExpiryAsync(
        string providerId,
        string credentialEpoch,
        string artifactSha256,
        DateTimeOffset newExpiresAt,
        DateTimeOffset updatedAt,
        CancellationToken ct = default);

    /// <summary>
    /// 标记过期（供重建前失效）；仅非终态（uploading/ready/delete_pending）可迁移到 expired。
    /// 成功返回更新后的记录；键不存在或状态不满足 CAS 时返回 null。
    /// </summary>
    Task<ProviderFileRefRecord?> MarkExpiredAsync(
        string providerId,
        string credentialEpoch,
        string artifactSha256,
        DateTimeOffset updatedAt,
        CancellationToken ct = default);

    /// <summary>
    /// 标记待清理（供 S2b-3 worker 枚举删除）；ready/expired/uploading/failed 均可迁移到 delete_pending。
    /// 成功返回更新后的记录；键不存在或状态不满足 CAS 时返回 null。
    /// </summary>
    Task<ProviderFileRefRecord?> MarkDeletePendingAsync(
        string providerId,
        string credentialEpoch,
        string artifactSha256,
        DateTimeOffset updatedAt,
        CancellationToken ct = default);

    /// <summary>
    /// 枚举待清理的过期引用（status = expired/delete_pending 且 expires_at ≤ before），
    /// 按过期时间升序、最多 <paramref name="limit"/> 条，供清理 worker（S2b-3）删除远端引用。
    /// </summary>
    Task<IReadOnlyList<ProviderFileRefRecord>> ListExpiredAsync(
        DateTimeOffset before,
        int limit,
        CancellationToken ct = default);
}
