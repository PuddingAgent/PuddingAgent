namespace PuddingCode.Skills;

/// <summary>SKILL Hub 写/读操作的语义结果状态（契约类型，自 PuddingPlatform.Services 原位迁入，形状冻结）。</summary>
public enum SkillHubStatus
{
    Ok,
    NotFound,
    Conflict,
    BadRequest,
}

/// <summary>SKILL Hub 语义结果——控制器据此映射 HTTP 状态码（404/409/400）。</summary>
public sealed record SkillHubResult<T>(SkillHubStatus Status, T? Value = default, string? Error = null)
    where T : class
{
    public bool IsOk => Status == SkillHubStatus.Ok;

    public static SkillHubResult<T> Ok(T value) => new(SkillHubStatus.Ok, value);
    public static SkillHubResult<T> NotFound(string error) => new(SkillHubStatus.NotFound, default, error);
    public static SkillHubResult<T> Conflict(string error) => new(SkillHubStatus.Conflict, default, error);
    public static SkillHubResult<T> BadRequest(string error) => new(SkillHubStatus.BadRequest, default, error);
}

/// <summary>
/// SKILL Hub 中央技能库契约（进程内直连地基）。
/// 方法集为 PuddingPlatform.Services.SkillHubService 公共实例 API 的一一镜像，
/// 由平台组合根（PuddingHost）注册：生命周期 Scoped，与实现依赖的 scoped PlatformDbContext
/// 一致，避免 Singleton 捕获 scoped 依赖（captive dependency）。
/// 实现类的公共静态成员（SkillIdPattern / AllowedEvolutionActions / AllowedStatuses /
/// CompareVersions / ComputeContentHash）无法成为接口成员，仍保留在 SkillHubService 上。
/// </summary>
public interface ISkillHubService
{
    /// <summary>发布新技能（契约 §5.2 POST /skills + §5.3 校验规则）。</summary>
    Task<SkillHubResult<HubSkillSummaryDto>> PublishAsync(PublishHubSkillRequest req, CancellationToken ct);

    /// <summary>发布新版本 / 进化（契约 §5.2 POST /skills/{skillId}/versions）。</summary>
    Task<SkillHubResult<HubSkillVersionDto>> PublishVersionAsync(string skillId, PublishHubSkillRequest req, CancellationToken ct);

    /// <summary>元数据更新 / 停用（契约 §5.2 PATCH /skills/{skillId}）。</summary>
    Task<SkillHubResult<HubSkillSummaryDto>> UpdateMetaAsync(string skillId, UpdateHubSkillMetaRequest req, CancellationToken ct);

    /// <summary>软删退役（契约 §5.3.10，DELETE /skills/{skillId} → retired + 审计事件）。</summary>
    Task<SkillHubResult<HubSkillSummaryDto>> RetireAsync(string skillId, CancellationToken ct);

    /// <summary>登记安装 upsert + InstallCount 去重重算（契约 §5.3.9，POST /installs）。</summary>
    Task<SkillHubResult<HubSkillInstallDto>> RegisterInstallAsync(RegisterInstallRequest req, CancellationToken ct);

    /// <summary>列表/搜索（契约 §5.2 GET /skills）：name/summary/description/tags/keywords 模糊。</summary>
    Task<List<HubSkillSummaryDto>> ListSkillsAsync(string? query, string? tag, string? status, int page, int pageSize, CancellationToken ct);

    /// <summary>技能详情（版本列表 + 最近安装；技能不存在返回 null）。</summary>
    Task<HubSkillDetailDto?> GetSkillAsync(string skillId, CancellationToken ct);

    /// <summary>版本列表（技能不存在返回 null）。</summary>
    Task<List<HubSkillVersionDto>?> ListVersionsAsync(string skillId, CancellationToken ct);

    /// <summary>单版本全文（含 SkillMarkdown；不存在返回 null）。</summary>
    Task<HubSkillVersionContentDto?> GetVersionAsync(string skillId, string version, CancellationToken ct);

    /// <summary>概览统计（契约 §5.2 GET /stats）。</summary>
    Task<HubSkillStatsDto> GetStatsAsync(CancellationToken ct);

    /// <summary>单技能血缘子图（契约 §5.3.7；技能不存在返回 null）。</summary>
    Task<EvoMapDto?> GetSkillLineageAsync(string skillId, CancellationToken ct);

    /// <summary>全局/多技能 EVO MAP（契约 §5.2 GET /lineage）。</summary>
    Task<EvoMapDto> GetLineageAsync(IReadOnlyList<string>? skillIds, int limit, CancellationToken ct);

    /// <summary>本地已登记版本 &lt; 最新版本（语义比较）的技能清单（契约 §5.2 GET /updates）。</summary>
    Task<List<HubSkillUpdateDto>> ListUpdatesAsync(string agentInstanceId, CancellationToken ct);

    /// <summary>审计事件查询（GET /events）。</summary>
    Task<List<HubSkillEventDto>> ListEventsAsync(string? skillId, int limit, CancellationToken ct);

    /// <summary>安装台账查询（GET /installs）。</summary>
    Task<List<HubSkillInstallDto>> ListInstallsAsync(string? agentInstanceId, string? skillId, int page, int pageSize, CancellationToken ct);
}
