using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using PuddingCode.Skills;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Services;

/// <summary>
/// SKILL Hub 中央技能库服务——实现设计契约 §5.3 的全部语义规则（冻结）：
/// 语义版本比较、ContentHash、进化动作白名单、retire 同步、OriginKind 判定、
/// 血缘节点/边生成、InstallCount 去重重算、全写操作审计事件。
/// 无状态；由控制器按请求构造（与 SkillPackageApiController 直接持 DbContext 的风格一致）。
/// </summary>
public partial class SkillHubService(PlatformDbContext db) : ISkillHubService
{
    /// <summary>SkillId 格式（契约 §5.3.1）。</summary>
    public static readonly Regex SkillIdPattern = new(@"^[a-z0-9][a-z0-9\-]{1,127}$", RegexOptions.Compiled);

    /// <summary>进化动作白名单（契约 §5.3.5）。</summary>
    public static readonly HashSet<string> AllowedEvolutionActions = new(StringComparer.Ordinal)
    {
        "create", "patch", "split", "compress", "retire", "merge", "fork",
    };

    /// <summary>主档合法状态。</summary>
    public static readonly HashSet<string> AllowedStatuses = new(StringComparer.Ordinal)
    {
        "active", "deprecated", "retired",
    };

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    // ── 静态工具：版本比较 / ContentHash ────────────────────────────

    /// <summary>
    /// 语义版本比较器（契约 §5.3.3）：按"."分段、每段取前导数字按数值比较
    /// （1.10.0 &gt; 1.9.0），数字相同再按剩余后缀（如 -beta）的序数比较；
    /// 缺失段按 0 处理；任何输入都不抛异常（不可解析段按 0 + 空后缀）。
    /// </summary>
    /// <returns>负数 = a&lt;b；0 = 相等；正数 = a&gt;b。</returns>
    public static int CompareVersions(string? a, string? b)
    {
        var segA = SplitSegments(a);
        var segB = SplitSegments(b);
        var len = Math.Max(segA.Count, segB.Count);
        for (var i = 0; i < len; i++)
        {
            var (numA, sufA) = i < segA.Count ? segA[i] : (0L, string.Empty);
            var (numB, sufB) = i < segB.Count ? segB[i] : (0L, string.Empty);
            if (numA != numB) return numA < numB ? -1 : 1;
            var cmp = string.CompareOrdinal(sufA, sufB);
            if (cmp != 0) return cmp;
        }
        return 0;
    }

    /// <summary>把版本串拆成（前导数值, 剩余后缀）段列表。</summary>
    private static List<(long Num, string Suffix)> SplitSegments(string? version)
    {
        var result = new List<(long, string)>();
        if (string.IsNullOrWhiteSpace(version)) return result;
        foreach (var raw in version.Split('.'))
        {
            var segment = raw.Trim();
            var digits = 0;
            while (digits < segment.Length && char.IsDigit(segment[digits])) digits++;
            long num = 0;
            if (digits > 0)
            {
                _ = long.TryParse(segment.AsSpan(0, digits), out num);
            }
            result.Add((num, digits < segment.Length ? segment[digits..] : string.Empty));
        }
        return result;
    }

    /// <summary>ContentHash = SHA256(SkillMarkdown) 前 16 字节小写 hex（契约 §5.3.4，服务端计算）。</summary>
    public static string ComputeContentHash(string skillMarkdown)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(skillMarkdown ?? string.Empty));
        var prefix = new byte[16];
        Array.Copy(bytes, prefix, prefix.Length);
        return Convert.ToHexString(prefix).ToLowerInvariant();
    }

    // ── 发布新技能（契约 §5.2 POST /skills + §5.3.1/§5.3.4/§5.3.5/§5.3.6/§5.3.8/§5.3.11）──

    public async Task<SkillHubResult<HubSkillSummaryDto>> PublishAsync(
        PublishHubSkillRequest req, CancellationToken ct)
    {
        if (req is null) return SkillHubResult<HubSkillSummaryDto>.BadRequest("请求体为空");
        var skillId = (req.SkillId ?? string.Empty).Trim();
        if (!SkillIdPattern.IsMatch(skillId))
            return SkillHubResult<HubSkillSummaryDto>.BadRequest(
                $"SkillId '{skillId}' 不匹配 ^[a-z0-9][a-z0-9-]{{1,127}}$");
        if (string.IsNullOrWhiteSpace(req.Name))
            return SkillHubResult<HubSkillSummaryDto>.BadRequest("Name 不能为空");
        if (string.IsNullOrWhiteSpace(req.SkillMarkdown))
            return SkillHubResult<HubSkillSummaryDto>.BadRequest("SkillMarkdown 不能为空");
        var version = string.IsNullOrWhiteSpace(req.Version) ? "1.0.0" : req.Version.Trim();

        // 进化动作：白名单校验 + 缺省推导（create）
        var action = DeriveEvolutionAction(req.EvolutionAction, req.ParentVersion);
        if (action is null)
            return SkillHubResult<HubSkillSummaryDto>.BadRequest(
                $"EvolutionAction '{req.EvolutionAction}' 不在白名单 create|patch|split|compress|retire|merge|fork 内");

        if (await db.HubSkills.AnyAsync(s => s.SkillId == skillId, ct))
            return SkillHubResult<HubSkillSummaryDto>.Conflict($"SkillId '{skillId}' 已存在");

        var now = DateTimeOffset.UtcNow;
        var contentHash = ComputeContentHash(req.SkillMarkdown);
        var originKind = string.IsNullOrWhiteSpace(req.PublishedByAgentId) ? "manual" : "agent-evolved";

        var skill = new HubSkillEntity
        {
            SkillId = skillId,
            Name = req.Name.Trim(),
            Summary = Truncate(req.Summary, 512),
            Description = Truncate(req.Description, 2048),
            TagsJson = SerializeStringList(req.Tags, 1024),
            KeywordsJson = SerializeStringList(req.Keywords, 2048),
            LatestVersion = version,
            Status = "active",
            Visibility = string.IsNullOrWhiteSpace(req.Visibility) ? "global" : req.Visibility.Trim(),
            OwnerWorkspaceId = req.PublishedByWorkspaceId,
            SourceAgentId = req.PublishedByAgentId,
            OriginKind = originKind,
            VersionCount = 1,
            InstallCount = 0,
            PublishCount = 1,
            LatestContentHash = contentHash,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.HubSkills.Add(skill);

        db.HubSkillVersions.Add(new HubSkillVersionEntity
        {
            SkillId = skillId,
            Version = version,
            ContentHash = contentHash,
            SkillMarkdown = req.SkillMarkdown,
            ManifestJson = string.IsNullOrWhiteSpace(req.ManifestJson) ? "{}" : req.ManifestJson,
            EvolutionAction = action,
            ParentVersion = req.ParentVersion,
            RelatedSkillIdsJson = SerializeStringList(req.RelatedSkillIds, 512),
            PublishedByAgentId = req.PublishedByAgentId,
            PublishedByWorkspaceId = req.PublishedByWorkspaceId,
            PublishNote = Truncate(req.PublishNote, 512),
            EvidenceJson = req.EvidenceJson,
            ContentBytes = Encoding.UTF8.GetByteCount(req.SkillMarkdown),
            CreatedAt = now,
        });

        db.HubSkillEvents.Add(new HubSkillEventEntity
        {
            SkillId = skillId,
            Version = version,
            EventType = "publish",
            ActorKind = string.IsNullOrWhiteSpace(req.PublishedByAgentId) ? "user" : "agent",
            ActorId = req.PublishedByAgentId ?? req.PublishedByWorkspaceId,
            WorkspaceId = req.PublishedByWorkspaceId,
            PayloadJson = BuildPayload(new { version, evolutionAction = action, originKind }),
            CreatedAt = now,
        });

        await db.SaveChangesAsync(ct);
        return SkillHubResult<HubSkillSummaryDto>.Ok(ToSummaryDto(skill));
    }

    // ── 发布新版本 / 进化（契约 §5.2 POST /skills/{id}/versions + §5.3.2/§5.3.5/§5.3.6）──

    /// <summary>发布新版本。路由 skillId 优先于请求体字段；复用 PublishHubSkillRequest（冻结 DTO 不新增）。</summary>
    public async Task<SkillHubResult<HubSkillVersionDto>> PublishVersionAsync(
        string skillId, PublishHubSkillRequest req, CancellationToken ct)
    {
        if (req is null) return SkillHubResult<HubSkillVersionDto>.BadRequest("请求体为空");
        if (string.IsNullOrWhiteSpace(req.Version))
            return SkillHubResult<HubSkillVersionDto>.BadRequest("Version 不能为空");
        if (string.IsNullOrWhiteSpace(req.SkillMarkdown))
            return SkillHubResult<HubSkillVersionDto>.BadRequest("SkillMarkdown 不能为空");
        var version = req.Version.Trim();

        var action = DeriveEvolutionAction(req.EvolutionAction, req.ParentVersion);
        if (action is null)
            return SkillHubResult<HubSkillVersionDto>.BadRequest(
                $"EvolutionAction '{req.EvolutionAction}' 不在白名单 create|patch|split|compress|retire|merge|fork 内");

        var skill = await db.HubSkills.FirstOrDefaultAsync(s => s.SkillId == skillId, ct);
        if (skill is null)
            return SkillHubResult<HubSkillVersionDto>.NotFound($"技能 '{skillId}' 不存在");

        if (await db.HubSkillVersions.AnyAsync(v => v.SkillId == skillId && v.Version == version, ct))
            return SkillHubResult<HubSkillVersionDto>.Conflict(
                $"版本 '{skillId}@{version}' 已存在");

        var now = DateTimeOffset.UtcNow;
        var contentHash = ComputeContentHash(req.SkillMarkdown);

        var entity = new HubSkillVersionEntity
        {
            SkillId = skillId,
            Version = version,
            ContentHash = contentHash,
            SkillMarkdown = req.SkillMarkdown,
            ManifestJson = string.IsNullOrWhiteSpace(req.ManifestJson) ? "{}" : req.ManifestJson,
            EvolutionAction = action,
            ParentVersion = req.ParentVersion,
            RelatedSkillIdsJson = SerializeStringList(req.RelatedSkillIds, 512),
            PublishedByAgentId = req.PublishedByAgentId,
            PublishedByWorkspaceId = req.PublishedByWorkspaceId,
            PublishNote = Truncate(req.PublishNote, 512),
            EvidenceJson = req.EvidenceJson,
            ContentBytes = Encoding.UTF8.GetByteCount(req.SkillMarkdown),
            CreatedAt = now,
        };
        db.HubSkillVersions.Add(entity);

        // 主档指针更新（契约 §5.3.2）
        skill.LatestVersion = version;
        skill.VersionCount += 1;
        skill.PublishCount += 1;
        skill.LatestContentHash = contentHash;
        skill.UpdatedAt = now;

        // retire 语义（契约 §5.3.6）：同步置主档 Status=retired
        if (action == "retire" && skill.Status != "retired")
        {
            skill.Status = "retired";
        }

        db.HubSkillEvents.Add(new HubSkillEventEntity
        {
            SkillId = skillId,
            Version = version,
            EventType = "update_version",
            ActorKind = string.IsNullOrWhiteSpace(req.PublishedByAgentId) ? "user" : "agent",
            ActorId = req.PublishedByAgentId ?? req.PublishedByWorkspaceId,
            WorkspaceId = req.PublishedByWorkspaceId,
            PayloadJson = BuildPayload(new { version, evolutionAction = action, parentVersion = req.ParentVersion }),
            CreatedAt = now,
        });

        await db.SaveChangesAsync(ct);
        return SkillHubResult<HubSkillVersionDto>.Ok(ToVersionDto(entity));
    }

    // ── 元数据更新 / 停用 / 退役（契约 §5.2 PATCH + DELETE）────────────

    public async Task<SkillHubResult<HubSkillSummaryDto>> UpdateMetaAsync(
        string skillId, UpdateHubSkillMetaRequest req, CancellationToken ct)
    {
        if (req is null) return SkillHubResult<HubSkillSummaryDto>.BadRequest("请求体为空");
        var skill = await db.HubSkills.FirstOrDefaultAsync(s => s.SkillId == skillId, ct);
        if (skill is null)
            return SkillHubResult<HubSkillSummaryDto>.NotFound($"技能 '{skillId}' 不存在");

        if (req.Status is not null && !AllowedStatuses.Contains(req.Status))
            return SkillHubResult<HubSkillSummaryDto>.BadRequest(
                $"Status '{req.Status}' 不在 active|deprecated|retired 内");
        if (req.Visibility is not null && req.Visibility is not ("global" or "workspace"))
            return SkillHubResult<HubSkillSummaryDto>.BadRequest(
                $"Visibility '{req.Visibility}' 不在 global|workspace 内");

        var statusChanged = req.Status is not null && !string.Equals(req.Status, skill.Status, StringComparison.Ordinal);
        if (req.Name is not null) skill.Name = req.Name.Trim();
        if (req.Summary is not null) skill.Summary = Truncate(req.Summary, 512);
        if (req.Description is not null) skill.Description = Truncate(req.Description, 2048);
        if (req.Tags is not null) skill.TagsJson = SerializeStringList(req.Tags, 1024);
        if (req.Keywords is not null) skill.KeywordsJson = SerializeStringList(req.Keywords, 2048);
        if (req.Status is not null) skill.Status = req.Status;
        if (req.Visibility is not null) skill.Visibility = req.Visibility;
        skill.UpdatedAt = DateTimeOffset.UtcNow;

        db.HubSkillEvents.Add(new HubSkillEventEntity
        {
            SkillId = skillId,
            Version = skill.LatestVersion,
            EventType = statusChanged ? "status_change" : "update",
            ActorKind = "user",
            ActorId = null,
            WorkspaceId = skill.OwnerWorkspaceId,
            PayloadJson = BuildPayload(new { status = skill.Status, visibility = skill.Visibility }),
            CreatedAt = DateTimeOffset.UtcNow,
        });

        await db.SaveChangesAsync(ct);
        return SkillHubResult<HubSkillSummaryDto>.Ok(ToSummaryDto(skill));
    }

    /// <summary>软删（契约 §5.3.10）：不物理删除版本内容，只置 Status=retired 并写事件。</summary>
    public async Task<SkillHubResult<HubSkillSummaryDto>> RetireAsync(string skillId, CancellationToken ct)
    {
        var skill = await db.HubSkills.FirstOrDefaultAsync(s => s.SkillId == skillId, ct);
        if (skill is null)
            return SkillHubResult<HubSkillSummaryDto>.NotFound($"技能 '{skillId}' 不存在");

        skill.Status = "retired";
        skill.UpdatedAt = DateTimeOffset.UtcNow;

        db.HubSkillEvents.Add(new HubSkillEventEntity
        {
            SkillId = skillId,
            Version = skill.LatestVersion,
            EventType = "delete",
            ActorKind = "user",
            ActorId = null,
            WorkspaceId = skill.OwnerWorkspaceId,
            PayloadJson = BuildPayload(new { status = "retired", softDelete = true }),
            CreatedAt = DateTimeOffset.UtcNow,
        });

        await db.SaveChangesAsync(ct);
        return SkillHubResult<HubSkillSummaryDto>.Ok(ToSummaryDto(skill));
    }

    // ── 安装台账（契约 §5.3.9 upsert + InstallCount 去重重算）────────────

    public async Task<SkillHubResult<HubSkillInstallDto>> RegisterInstallAsync(
        RegisterInstallRequest req, CancellationToken ct)
    {
        if (req is null) return SkillHubResult<HubSkillInstallDto>.BadRequest("请求体为空");
        if (string.IsNullOrWhiteSpace(req.SkillId) || string.IsNullOrWhiteSpace(req.AgentInstanceId))
            return SkillHubResult<HubSkillInstallDto>.BadRequest("SkillId 与 AgentInstanceId 不能为空");
        if (string.IsNullOrWhiteSpace(req.InstalledVersion))
            return SkillHubResult<HubSkillInstallDto>.BadRequest("InstalledVersion 不能为空");

        var skill = await db.HubSkills.FirstOrDefaultAsync(s => s.SkillId == req.SkillId, ct);
        if (skill is null)
            return SkillHubResult<HubSkillInstallDto>.NotFound($"技能 '{req.SkillId}' 不存在");

        var now = DateTimeOffset.UtcNow;
        var install = await db.HubSkillInstalls.FirstOrDefaultAsync(
            i => i.SkillId == req.SkillId && i.AgentInstanceId == req.AgentInstanceId, ct);
        if (install is null)
        {
            install = new HubSkillInstallEntity
            {
                SkillId = req.SkillId,
                AgentInstanceId = req.AgentInstanceId,
                WorkspaceId = req.WorkspaceId,
                InstalledVersion = req.InstalledVersion,
                ContentHash = req.ContentHash,
                InstalledBy = string.IsNullOrWhiteSpace(req.InstalledBy) ? "agent" : req.InstalledBy,
                InstalledAt = now,
                UpdatedAt = now,
            };
            db.HubSkillInstalls.Add(install);
        }
        else
        {
            install.WorkspaceId = req.WorkspaceId ?? install.WorkspaceId;
            install.InstalledVersion = req.InstalledVersion;
            install.ContentHash = req.ContentHash ?? install.ContentHash;
            install.InstalledBy = string.IsNullOrWhiteSpace(req.InstalledBy) ? install.InstalledBy : req.InstalledBy;
            install.UpdatedAt = now;
        }

        // 先落库 install（Add 的实体在 SaveChanges 前对 SQL 查询不可见），
        // 再重算去重安装数，最后同步主档。
        var installedBy = install.InstalledBy;
        db.HubSkillEvents.Add(new HubSkillEventEntity
        {
            SkillId = req.SkillId,
            Version = req.InstalledVersion,
            EventType = "install",
            ActorKind = installedBy is "user" or "system" ? installedBy : "agent",
            ActorId = req.AgentInstanceId,
            WorkspaceId = req.WorkspaceId,
            PayloadJson = BuildPayload(new { agentInstanceId = req.AgentInstanceId, installedVersion = req.InstalledVersion }),
            CreatedAt = now,
        });

        await db.SaveChangesAsync(ct);

        // InstallCount 按去重 AgentInstanceId 重算（契约 §5.3.9）——必须在 install 落库后查
        var distinctAgents = await db.HubSkillInstalls
            .Where(i => i.SkillId == req.SkillId)
            .Select(i => i.AgentInstanceId)
            .Distinct()
            .CountAsync(ct);
        skill.InstallCount = distinctAgents;
        skill.UpdatedAt = now;

        await db.SaveChangesAsync(ct);
        return SkillHubResult<HubSkillInstallDto>.Ok(ToInstallDto(install));
    }

    // ── 查询：列表 / 详情 / 版本 ────────────────────────────────────

    /// <summary>列表/搜索（契约 §5.2）：name/summary/description/tags/keywords 模糊。</summary>
    public async Task<List<HubSkillSummaryDto>> ListSkillsAsync(
        string? query, string? tag, string? status, int page, int pageSize, CancellationToken ct)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize <= 0 ? 50 : pageSize, 1, 500);

        var skills = db.HubSkills.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(status))
            skills = skills.Where(s => s.Status == status);
        if (!string.IsNullOrWhiteSpace(tag))
            skills = skills.Where(s => s.TagsJson.Contains(tag));
        if (!string.IsNullOrWhiteSpace(query))
        {
            var like = $"%{query.Trim()}%";
            skills = skills.Where(s =>
                EF.Functions.Like(s.Name, like) ||
                EF.Functions.Like(s.Summary, like) ||
                EF.Functions.Like(s.Description, like) ||
                EF.Functions.Like(s.TagsJson, like) ||
                EF.Functions.Like(s.KeywordsJson, like));
        }

        var list = await skills
            .OrderByDescending(s => s.Id) // 自增 Id 序 ≈ 时间序；SQLite 不支持 DateTimeOffset ORDER BY
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);
        return list.Select(ToSummaryDto).ToList();
    }

    public async Task<HubSkillDetailDto?> GetSkillAsync(string skillId, CancellationToken ct)
    {
        var skill = await db.HubSkills.AsNoTracking().FirstOrDefaultAsync(s => s.SkillId == skillId, ct);
        if (skill is null) return null;

        var versions = await db.HubSkillVersions.AsNoTracking()
            .Where(v => v.SkillId == skillId)
            .OrderBy(v => v.Id) // 插入序 = 版本发布序；SQLite 不支持 DateTimeOffset ORDER BY
            .ToListAsync(ct);
        var installs = await db.HubSkillInstalls.AsNoTracking()
            .Where(i => i.SkillId == skillId)
            .OrderByDescending(i => i.Id)
            .Take(20)
            .ToListAsync(ct);

        return new HubSkillDetailDto(
            ToSummaryDto(skill),
            versions.Select(ToVersionDto).ToList(),
            installs.Select(ToInstallDto).ToList());
    }

    public async Task<List<HubSkillVersionDto>?> ListVersionsAsync(string skillId, CancellationToken ct)
    {
        var exists = await db.HubSkills.AsNoTracking().AnyAsync(s => s.SkillId == skillId, ct);
        if (!exists) return null;
        var versions = await db.HubSkillVersions.AsNoTracking()
            .Where(v => v.SkillId == skillId)
            .OrderByDescending(v => v.Id)
            .ToListAsync(ct);
        return versions.Select(ToVersionDto).ToList();
    }

    public async Task<HubSkillVersionContentDto?> GetVersionAsync(
        string skillId, string version, CancellationToken ct)
    {
        var entity = await db.HubSkillVersions.AsNoTracking()
            .FirstOrDefaultAsync(v => v.SkillId == skillId && v.Version == version, ct);
        return entity is null ? null : ToVersionContentDto(entity);
    }

    // ── 统计（契约 §5.2 GET /stats）─────────────────────────────────

    public async Task<HubSkillStatsDto> GetStatsAsync(CancellationToken ct)
    {
        var skills = await db.HubSkills.AsNoTracking().ToListAsync(ct);
        var totalVersions = await db.HubSkillVersions.CountAsync(ct);
        var totalInstalls = await db.HubSkillInstalls.CountAsync(ct);
        var distinctAgents = await db.HubSkillInstalls
            .Select(i => i.AgentInstanceId).Distinct().CountAsync(ct);

        var actionCounts = await db.HubSkillVersions.AsNoTracking()
            .GroupBy(v => v.EvolutionAction)
            .Select(g => new { Action = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var topInstalled = skills
            .Where(s => s.InstallCount > 0)
            .OrderByDescending(s => s.InstallCount)
            .ThenBy(s => s.SkillId)
            .Take(10)
            .Select(ToSummaryDto)
            .ToList();

        return new HubSkillStatsDto(
            TotalSkills: skills.Count,
            ActiveSkills: skills.Count(s => s.Status == "active"),
            RetiredSkills: skills.Count(s => s.Status == "retired"),
            TotalVersions: totalVersions,
            TotalInstalls: totalInstalls,
            DistinctAgents: distinctAgents,
            EvolvedSkills: skills.Count(s => s.VersionCount > 1),
            EvolutionActionCounts: actionCounts
                .OrderByDescending(x => x.Count)
                .Select(x => new HubSkillActionCountDto(x.Action, x.Count))
                .ToList(),
            TopInstalled: topInstalled,
            GeneratedAt: DateTimeOffset.UtcNow);
    }

    // ── EVO MAP 血缘（契约 §5.3.7 + §5.2 lineage 端点）───────────────

    /// <summary>单技能血缘子图。</summary>
    public async Task<EvoMapDto?> GetSkillLineageAsync(string skillId, CancellationToken ct)
    {
        var exists = await db.HubSkills.AsNoTracking().AnyAsync(s => s.SkillId == skillId, ct);
        if (!exists) return null;
        var versions = await db.HubSkillVersions.AsNoTracking()
            .Where(v => v.SkillId == skillId)
            .OrderBy(v => v.Id)
            .ToListAsync(ct);
        return BuildEvoMap(versions, skillStatusById: null, installCountsByVersion: null);
    }

    /// <summary>全局/多技能 EVO MAP（skillIds 为空 = 全部技能，节点数受 limit 约束）。</summary>
    public async Task<EvoMapDto> GetLineageAsync(IReadOnlyList<string>? skillIds, int limit, CancellationToken ct)
    {
        limit = Math.Clamp(limit <= 0 ? 200 : limit, 1, 1000);
        var query = db.HubSkillVersions.AsNoTracking().AsQueryable();
        if (skillIds is { Count: > 0 })
        {
            var ids = skillIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToList();
            if (ids.Count > 0)
                query = query.Where(v => ids.Contains(v.SkillId));
        }

        var versions = await query.OrderBy(v => v.Id).Take(limit).ToListAsync(ct);
        var skillIdsInMap = versions.Select(v => v.SkillId).Distinct().ToList();
        var skills = await db.HubSkills.AsNoTracking()
            .Where(s => skillIdsInMap.Contains(s.SkillId))
            .ToDictionaryAsync(s => s.SkillId, ct);

        // 节点安装数：按"该版本"登记的去重安装行数（(SkillId,AgentInstanceId) 唯一 → 行数 = Agent 数）
        var installCounts = await db.HubSkillInstalls.AsNoTracking()
            .Where(i => skillIdsInMap.Contains(i.SkillId))
            .GroupBy(i => new { i.SkillId, i.InstalledVersion })
            .Select(g => new { g.Key.SkillId, g.Key.InstalledVersion, Count = g.Count() })
            .ToListAsync(ct);
        var installMap = installCounts.ToDictionary(x => (x.SkillId, x.InstalledVersion), x => x.Count);

        return BuildEvoMap(versions, skills, installMap);
    }

    private EvoMapDto BuildEvoMap(
        List<HubSkillVersionEntity> versions,
        IReadOnlyDictionary<string, HubSkillEntity>? skillStatusById,
        IReadOnlyDictionary<(string SkillId, string Version), int>? installCountsByVersion)
    {
        var nodes = new List<EvoMapNodeDto>(versions.Count);
        var edges = new List<EvoMapEdgeDto>();
        foreach (var v in versions)
        {
            var status = skillStatusById is not null && skillStatusById.TryGetValue(v.SkillId, out var skill)
                ? skill.Status
                : "active";
            var installCount = installCountsByVersion is not null
                && installCountsByVersion.TryGetValue((v.SkillId, v.Version), out var c) ? c : 0;
            nodes.Add(new EvoMapNodeDto(
                NodeId: $"{v.SkillId}@{v.Version}",
                SkillId: v.SkillId,
                Version: v.Version,
                EvolutionAction: v.EvolutionAction,
                ParentNodeId: v.ParentVersion is null ? null : $"{v.SkillId}@{v.ParentVersion}",
                Name: skillStatusById is not null && skillStatusById.TryGetValue(v.SkillId, out var s) ? s.Name : v.SkillId,
                Status: status,
                PublishedByAgentId: v.PublishedByAgentId,
                CreatedAt: v.CreatedAt,
                ContentBytes: v.ContentBytes,
                InstallCount: installCount));

            // 血缘边（契约 §5.3.7）：有 ParentVersion 才有边
            if (v.ParentVersion is not null)
            {
                edges.Add(new EvoMapEdgeDto(
                    FromNodeId: $"{v.SkillId}@{v.ParentVersion}",
                    ToNodeId: $"{v.SkillId}@{v.Version}",
                    Action: v.EvolutionAction));
            }
        }

        return new EvoMapDto(nodes, edges, DateTimeOffset.UtcNow);
    }

    // ── 待更新清单（契约 §5.2 GET /updates）─────────────────────────

    /// <summary>本地已登记版本 &lt; 最新版本（语义比较）的技能清单。</summary>
    public async Task<List<HubSkillUpdateDto>> ListUpdatesAsync(string agentInstanceId, CancellationToken ct)
    {
        var result = new List<HubSkillUpdateDto>();
        if (string.IsNullOrWhiteSpace(agentInstanceId)) return result;

        var installs = await db.HubSkillInstalls.AsNoTracking()
            .Where(i => i.AgentInstanceId == agentInstanceId)
            .ToListAsync(ct);
        if (installs.Count == 0) return result;

        var skillIds = installs.Select(i => i.SkillId).Distinct().ToList();
        var skills = await db.HubSkills.AsNoTracking()
            .Where(s => skillIds.Contains(s.SkillId))
            .ToDictionaryAsync(s => s.SkillId, ct);

        var latestVersions = await db.HubSkillVersions.AsNoTracking()
            .Where(v => skillIds.Contains(v.SkillId))
            .ToListAsync(ct);
        var latestByVersion = latestVersions
            .GroupBy(v => (v.SkillId, v.Version))
            .ToDictionary(g => g.Key, g => g.First());

        foreach (var install in installs)
        {
            if (!skills.TryGetValue(install.SkillId, out var skill)) continue;
            // 语义版本比较（契约 §5.3.3）：InstalledVersion < LatestVersion 才提示更新
            if (CompareVersions(install.InstalledVersion, skill.LatestVersion) >= 0) continue;
            if (!latestByVersion.TryGetValue((skill.SkillId, skill.LatestVersion), out var latest)) continue;

            result.Add(new HubSkillUpdateDto(
                SkillId: skill.SkillId,
                Name: skill.Name,
                InstalledVersion: install.InstalledVersion,
                LatestVersion: skill.LatestVersion,
                LatestEvolutionAction: latest.EvolutionAction,
                LatestPublishedAt: latest.CreatedAt,
                PublishNote: latest.PublishNote));
        }

        return result.OrderBy(u => u.SkillId).ToList();
    }

    // ── 审计事件 / 安装台账查询 ─────────────────────────────────────

    public async Task<List<HubSkillEventDto>> ListEventsAsync(string? skillId, int limit, CancellationToken ct)
    {
        limit = Math.Clamp(limit <= 0 ? 100 : limit, 1, 1000);
        var query = db.HubSkillEvents.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(skillId))
            query = query.Where(ev => ev.SkillId == skillId);
        var events = await query
            .OrderByDescending(ev => ev.Id)
            .Take(limit)
            .ToListAsync(ct);
        return events.Select(ToEventDto).ToList();
    }

    public async Task<List<HubSkillInstallDto>> ListInstallsAsync(
        string? agentInstanceId, string? skillId, int page, int pageSize, CancellationToken ct)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize <= 0 ? 50 : pageSize, 1, 500);
        var query = db.HubSkillInstalls.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(agentInstanceId))
            query = query.Where(i => i.AgentInstanceId == agentInstanceId);
        if (!string.IsNullOrWhiteSpace(skillId))
            query = query.Where(i => i.SkillId == skillId);
        var installs = await query
            .OrderByDescending(i => i.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);
        return installs.Select(ToInstallDto).ToList();
    }

    // ── 私有辅助 ────────────────────────────────────────────────────

    /// <summary>进化动作白名单校验 + 缺省推导（契约 §5.3.5）：null → ParentVersion==null ? create : patch。</summary>
    private static string? DeriveEvolutionAction(string? action, string? parentVersion)
    {
        if (string.IsNullOrWhiteSpace(action))
            return parentVersion is null ? "create" : "patch";
        var normalized = action.Trim();
        return AllowedEvolutionActions.Contains(normalized) ? normalized : null;
    }

    private static string? Truncate(string? value, int maxLength) =>
        string.IsNullOrEmpty(value) ? value
            : value.Length <= maxLength ? value : value[..maxLength];

    private static string SerializeStringList(IReadOnlyList<string>? items, int maxLength)
    {
        if (items is null || items.Count == 0) return "[]";
        var json = JsonSerializer.Serialize(items.Where(t => !string.IsNullOrWhiteSpace(t)).ToList(), JsonOpts);
        return json.Length <= maxLength ? json : json[..maxLength];
    }

    private static string BuildPayload(object payload) =>
        JsonSerializer.Serialize(payload, JsonOpts);

    private static List<string> DeserializeStringList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            var list = JsonSerializer.Deserialize<List<string>>(json, JsonOpts);
            return list ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static HubSkillSummaryDto ToSummaryDto(HubSkillEntity s) => new(
        SkillId: s.SkillId,
        Name: s.Name,
        Summary: s.Summary,
        Description: s.Description,
        Tags: DeserializeStringList(s.TagsJson),
        Keywords: DeserializeStringList(s.KeywordsJson),
        LatestVersion: s.LatestVersion,
        Status: s.Status,
        Visibility: s.Visibility,
        OwnerWorkspaceId: s.OwnerWorkspaceId,
        SourceAgentId: s.SourceAgentId,
        OriginKind: s.OriginKind,
        VersionCount: s.VersionCount,
        InstallCount: s.InstallCount,
        PublishCount: s.PublishCount,
        LatestContentHash: s.LatestContentHash,
        CreatedAt: s.CreatedAt,
        UpdatedAt: s.UpdatedAt);

    private static HubSkillVersionDto ToVersionDto(HubSkillVersionEntity v) => new(
        SkillId: v.SkillId,
        Version: v.Version,
        ContentHash: v.ContentHash,
        EvolutionAction: v.EvolutionAction,
        ParentVersion: v.ParentVersion,
        RelatedSkillIds: DeserializeStringList(v.RelatedSkillIdsJson),
        PublishedByAgentId: v.PublishedByAgentId,
        PublishedByWorkspaceId: v.PublishedByWorkspaceId,
        PublishNote: v.PublishNote,
        ContentBytes: v.ContentBytes,
        CreatedAt: v.CreatedAt);

    private static HubSkillVersionContentDto ToVersionContentDto(HubSkillVersionEntity v) => new(
        SkillId: v.SkillId,
        Version: v.Version,
        ContentHash: v.ContentHash,
        EvolutionAction: v.EvolutionAction,
        ParentVersion: v.ParentVersion,
        RelatedSkillIds: DeserializeStringList(v.RelatedSkillIdsJson),
        PublishedByAgentId: v.PublishedByAgentId,
        PublishedByWorkspaceId: v.PublishedByWorkspaceId,
        PublishNote: v.PublishNote,
        ContentBytes: v.ContentBytes,
        CreatedAt: v.CreatedAt,
        SkillMarkdown: v.SkillMarkdown);

    private static HubSkillInstallDto ToInstallDto(HubSkillInstallEntity i) => new(
        SkillId: i.SkillId,
        AgentInstanceId: i.AgentInstanceId,
        WorkspaceId: i.WorkspaceId,
        InstalledVersion: i.InstalledVersion,
        ContentHash: i.ContentHash,
        InstalledBy: i.InstalledBy,
        InstalledAt: i.InstalledAt,
        UpdatedAt: i.UpdatedAt);

    private static HubSkillEventDto ToEventDto(HubSkillEventEntity ev) => new(
        Id: ev.Id,
        SkillId: ev.SkillId,
        Version: ev.Version,
        EventType: ev.EventType,
        ActorKind: ev.ActorKind,
        ActorId: ev.ActorId,
        WorkspaceId: ev.WorkspaceId,
        PayloadJson: ev.PayloadJson,
        CreatedAt: ev.CreatedAt);
}
