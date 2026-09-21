using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PuddingCode.Models;
using PuddingCode.Skills;
using PuddingCode.Tools;
using PuddingRuntime.Services.Skills;

namespace PuddingRuntime.Services.Tools;

[Tool(
    id: "skill_hub",
    name: "SKILL Hub",
    description: "PuddingAgent 内部私有技能中心（SKILL Hub）。【何时用】想把自己沉淀的技能/经验分享给其他 Agent（publish）、想查找并安装别人分享的技能（search/install）、想检查已装技能是否有新版本（check_updates）、想查看技能进化血缘（lineage）、或想基于已有技能演化出新版本（evolve）时使用。【怎么用】action=search 传 query/tags；install 传 skill_id（可带 version）；publish 传 skill_id+name+skill_markdown（内容取自本地技能或直接给）；evolve 传 skill_id+version+evolution_action+parent_version+skill_markdown；check_updates 无需参数。【坑】publish/evolve 会写入中央库并留审计事件，不可静默撤回；install 会覆盖本地同名技能，请先 get 预览；lineage 需要 skill_id。",
    category: ToolCategory.FileSystem,
    permission: ToolPermissionLevel.Medium,
    safety: ToolSafetyFlags.None,
    SortOrder = 46)]
/// <summary>
/// SKILL Hub 工具（进程内直连平台契约版）。
/// 只依赖 <see cref="PuddingCode.Skills.ISkillHubService"/>，拓扑、凭据、联机全部归平台；
/// 工具对"本地/远程"无感。工具注册为 Singleton、契约实现为 Scoped（内含 scoped PlatformDbContext），
/// 因此注入 <see cref="IServiceScopeFactory"/> 并在每次调用内建 scope 解析，避免 captive dependency。
/// </summary>
public sealed class SkillHubTool(
    IServiceScopeFactory scopeFactory,
    IOptions<SkillHubFeatureOptions> options,
    AgentSkillFileService skillService) : PuddingToolBase<SkillHubArgs>
{
    private const int LineageNodeCap = 200;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// 契约 DTO 出参序列化选项：与平台控制器出参相同的 Web 默认（camelCase、保留 null 字段），
    /// 保证工具结果的 JSON 形状与原链路逐字段一致。
    /// </summary>
    private static readonly JsonSerializerOptions HubDtoOptions = new(JsonSerializerDefaults.Web);

    /// <summary>进化动作白名单（契约 §5.3 冻结，不得改名）。</summary>
    private static readonly HashSet<string> EvolutionActions = new(StringComparer.Ordinal)
    {
        "create", "patch", "split", "compress", "retire", "merge", "fork",
    };

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        SkillHubArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        var agentInstanceId = context.AgentInstanceId;
        var action = NormalizeAction(args.Action);
        try
        {
            // 特性开关：关闭时不执行任何操作，返回结构化失败（能力缺失语义）。
            if (!options.Value.Enabled)
            {
                return Fail(ToJson(Error(action,
                    "SKILL Hub 功能未启用（配置节 SkillHub:Enabled=false）。请联系平台管理员开启后再试。",
                    statusCode: 503)));
            }

            // 进程内直连：工具是 Singleton、契约实现是 Scoped（内含 scoped PlatformDbContext），
            // 每次调用建独立 scope 解析，杜绝跨请求复用 DbContext 的隐蔽故障。
            using var scope = scopeFactory.CreateScope();
            var hub = scope.ServiceProvider.GetRequiredService<ISkillHubService>();

            return action switch
            {
                "search" => Ok(await SearchAsync(hub, args, ct)),
                "browse" => Ok(await BrowseAsync(hub, args, ct)),
                "get" => Ok(await GetAsync(hub, args, ct)),
                "install" => Ok(await InstallAsync(hub, agentInstanceId, context.WorkspaceId, args, ct)),
                "publish" => Ok(await PublishAsync(hub, agentInstanceId, context.WorkspaceId, args, ct)),
                "update" => Ok(await PublishVersionAsync(hub, agentInstanceId, context.WorkspaceId, args, ct, isEvolve: false)),
                "evolve" => Ok(await PublishVersionAsync(hub, agentInstanceId, context.WorkspaceId, args, ct, isEvolve: true)),
                "check_updates" => Ok(await CheckUpdatesAsync(hub, agentInstanceId, ct)),
                "lineage" => Ok(await LineageAsync(hub, args, ct)),
                "stats" => Ok(await StatsAsync(hub, ct)),
                "unpublish" => Ok(await UnpublishAsync(hub, args, ct)),
                _ => Fail($"Unknown skill_hub action '{args.Action}'. Valid actions: search, browse, get, install, publish, update, evolve, check_updates, lineage, stats, unpublish."),
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // Turn 级取消：交回宿主，不算工具失败。
        }
        catch (Exception ex)
        {
            return Fail(ToJson(Error(action, $"skill_hub action failed: {ex.Message}")));
        }
    }

    // ────────────────────────────────────────────────────────────
    // action=search：ListSkillsAsync —— 精简列表
    // ────────────────────────────────────────────────────────────
    private async Task<object> SearchAsync(ISkillHubService hub, SkillHubArgs args, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(args.Query) && (args.Tags is null || args.Tags.Count == 0))
            return Error("search", "query 或 tags 至少提供一个；仅浏览请用 action=browse。");

        var skills = SummarizeSkills(ToHubElement(await hub.ListSkillsAsync(
            args.Query,
            JoinTags(args.Tags),
            args.Status,
            args.Page ?? 1,
            Math.Clamp(args.PageSize ?? 50, 1, 200),
            ct)));

        return new
        {
            status = "ok",
            action = "search",
            query = args.Query,
            tags = args.Tags,
            count = skills?.Length ?? 0,
            skills,
        };
    }

    // ────────────────────────────────────────────────────────────
    // action=browse：ListSkillsAsync（不带 query）—— 分页浏览
    // ────────────────────────────────────────────────────────────
    private async Task<object> BrowseAsync(ISkillHubService hub, SkillHubArgs args, CancellationToken ct)
    {
        var skills = SummarizeSkills(ToHubElement(await hub.ListSkillsAsync(
            query: null,
            tag: null,
            args.Status,
            args.Page ?? 1,
            Math.Clamp(args.PageSize ?? 50, 1, 200),
            ct)));

        return new
        {
            status = "ok",
            action = "browse",
            page = args.Page ?? 1,
            pageSize = args.PageSize ?? 50,
            count = skills?.Length ?? 0,
            skills,
        };
    }

    // ────────────────────────────────────────────────────────────
    // action=get：GetSkillAsync（include_content=true 附最新版全文）
    // ────────────────────────────────────────────────────────────
    private async Task<object> GetAsync(ISkillHubService hub, SkillHubArgs args, CancellationToken ct)
    {
        var skillId = RequireSkillId(args, "get");
        var detail = await hub.GetSkillAsync(skillId, ct);
        if (detail is null)
            return MapStatusError("get", SkillHubStatus.NotFound, $"技能 '{skillId}' 不存在");

        var detailElement = ToHubElement(detail);
        if (!args.IncludeContent)
            return new { status = "ok", action = "get", skill = DetailSkill(detailElement) };

        var latestVersion = DetailSkill(detailElement).TryGetProperty("latestVersion", out var lv)
            ? lv.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(latestVersion))
            return Error("get", "无法从详情中解析 latestVersion，无法取全文。");

        var version = await hub.GetVersionAsync(skillId, latestVersion, ct);
        if (version is null)
            return MapStatusError("get", SkillHubStatus.NotFound, $"技能 '{skillId}' 的版本 {latestVersion} 不存在");

        var versionElement = ToHubElement(version);
        return new
        {
            status = "ok",
            action = "get",
            skill = DetailSkill(detailElement),
            contentVersion = latestVersion,
            contentHash = versionElement.TryGetString("contentHash"),
            skillMarkdown = versionElement.TryGetString("skillMarkdown"),
        };
    }

    // ────────────────────────────────────────────────────────────
    // action=install：GetSkillAsync + GetVersionAsync → 落本地 → RegisterInstallAsync 登记
    // ────────────────────────────────────────────────────────────
    private async Task<object> InstallAsync(
        ISkillHubService hub, string agentInstanceId, string workspaceId, SkillHubArgs args, CancellationToken ct)
    {
        var skillId = RequireSkillId(args, "install");

        // 1. 详情：拿 name / latestVersion / tags / keywords 等（同时校验技能存在）。
        var detail = await hub.GetSkillAsync(skillId, ct);
        if (detail is null)
            return MapStatusError("install", SkillHubStatus.NotFound, $"技能 '{skillId}' 不存在");
        var hubSkill = DetailSkill(ToHubElement(detail));
        var displayName = hubSkill.TryGetString("name") ?? skillId;

        // 2. 版本：显式指定或取最新。
        var version = Coalesce(args.Version, hubSkill.TryGetString("latestVersion"));
        if (string.IsNullOrWhiteSpace(version))
            return Error("install", "无法确定要安装的版本（详情缺少 latestVersion）。");

        // 3. 取该版本全文。
        var versionContent = await hub.GetVersionAsync(skillId, version, ct);
        if (versionContent is null)
            return MapStatusError("install", SkillHubStatus.NotFound, $"技能 '{skillId}' 的版本 {version} 不存在，终止安装");
        var versionElement = ToHubElement(versionContent);
        var markdown = versionElement.TryGetString("skillMarkdown");
        if (string.IsNullOrEmpty(markdown))
            return Error("install", $"版本 {version} 内容缺少 skillMarkdown，终止安装。");

        // 4. overwrite 检查 + 落本地（Create 或 Update）。
        var localExists = await LocalSkillExistsAsync(agentInstanceId, skillId, ct);
        if (localExists && !args.Overwrite)
            return Error("install", $"本地已存在技能 '{skillId}' 且 overwrite=false；如确认覆盖请传 overwrite=true，或先 action=get 预览。");

        string? localPath;
        try
        {
            if (localExists)
            {
                var updated = await skillService.UpdateAsync(agentInstanceId, skillId, new AgentSkillUpdateRequest
                {
                    Name = displayName,
                    Version = version,
                    Description = hubSkill.TryGetString("description"),
                    Summary = hubSkill.TryGetString("summary"),
                    Tags = hubSkill.TryGetStringList("tags"),
                    Keywords = hubSkill.TryGetStringList("keywords"),
                    SkillMarkdown = markdown,
                }, ct);
                localPath = updated.PhysicalPath;
            }
            else
            {
                var created = await skillService.CreateAsync(agentInstanceId, new AgentSkillCreateRequest
                {
                    SkillId = skillId,
                    Name = displayName,
                    Version = version,
                    Description = hubSkill.TryGetString("description"),
                    Summary = hubSkill.TryGetString("summary"),
                    Tags = hubSkill.TryGetStringList("tags"),
                    Keywords = hubSkill.TryGetStringList("keywords"),
                    SkillMarkdown = markdown,
                }, ct);
                localPath = created.PhysicalPath;
            }
        }
        catch (Exception ex)
        {
            return Error("install", $"写入本地技能失败：{ex.Message}");
        }

        // 5. 登记安装台账（upsert）。登记失败不回滚本地安装，但显式上报。
        object? installRecord = null;
        var registerError = (string?)null;
        var registerResult = await hub.RegisterInstallAsync(new RegisterInstallRequest(
            SkillId: skillId,
            AgentInstanceId: agentInstanceId,
            WorkspaceId: workspaceId,
            InstalledVersion: version,
            ContentHash: versionElement.TryGetString("contentHash"),
            InstalledBy: $"agent:{agentInstanceId}"), ct);
        if (registerResult.IsOk)
        {
            installRecord = ToHubElement(registerResult.Value!);
        }
        else
        {
            registerError = Truncate(MapStatusError("install", registerResult.Status, registerResult.Error), 500);
        }

        return new
        {
            status = "ok",
            action = "install",
            agentInstanceId,
            skillId,
            installedVersion = version,
            localPath,
            overwroteLocal = localExists,
            installRegistered = registerResult.IsOk,
            installRecord,
            registerError,
        };
    }

    // ────────────────────────────────────────────────────────────
    // action=publish：PublishAsync（冲突 → 提示改用 update/evolve）
    // ────────────────────────────────────────────────────────────
    private async Task<object> PublishAsync(
        ISkillHubService hub, string agentInstanceId, string workspaceId, SkillHubArgs args, CancellationToken ct)
    {
        var skillId = RequireSkillId(args, "publish");

        // 本地技能（可选）：缺 skill_markdown 时回退读取；缺 name/version/summary 时参考 manifest。
        AgentSkillManifest? localManifest = null;
        if (await LocalSkillExistsAsync(agentInstanceId, skillId, ct))
        {
            var record = await skillService.GetAsync(agentInstanceId, skillId, ct);
            localManifest = record.Manifest;
        }

        var markdown = args.SkillMarkdown;
        if (string.IsNullOrWhiteSpace(markdown))
        {
            if (localManifest is null)
                return Error("publish", "需要 skill_markdown 参数，或本地已存在该 skill_id 的技能（自动读取其 SKILL.md）。");
            var file = await skillService.ReadFileAsync(agentInstanceId, skillId, null, ct);
            markdown = file.Content;
        }

        var name = Coalesce(args.Name, localManifest?.Name);
        if (string.IsNullOrWhiteSpace(name))
            return Error("publish", "name is required for publish action（本地技能也缺少 name）。");

        var version = Coalesce(args.Version, localManifest?.Version, "1.0.0");
        if (args.EvolutionAction is not null && !EvolutionActions.Contains(args.EvolutionAction))
            return Error("publish", $"evolution_action '{args.EvolutionAction}' 非法。白名单：{string.Join('|', EvolutionActions)}。");

        var result = await hub.PublishAsync(new PublishHubSkillRequest(
            SkillId: skillId,
            Name: name,
            Summary: Coalesce(args.Summary, localManifest?.Summary is { Length: > 0 } s ? s : null),
            Description: Coalesce(args.Description, localManifest?.Description),
            Tags: (IReadOnlyList<string>?)Coalesce(args.Tags, localManifest?.Tags),
            Keywords: (IReadOnlyList<string>?)Coalesce(args.Keywords, localManifest?.Keywords),
            Version: version,
            SkillMarkdown: markdown,
            ManifestJson: null,
            EvolutionAction: args.EvolutionAction,
            ParentVersion: args.ParentVersion,
            RelatedSkillIds: args.RelatedSkillIds,
            PublishedByAgentId: agentInstanceId,
            PublishedByWorkspaceId: workspaceId,
            PublishNote: args.PublishNote,
            EvidenceJson: BuildEvidenceJson(args.Evidence),
            Visibility: string.IsNullOrWhiteSpace(args.Visibility) ? "global" : args.Visibility), ct);
        if (!result.IsOk)
            return MaybeConflictHint(
                MapStatusError("publish", result.Status, result.Error),
                "该 skill_id 已存在于中央库；请改用 action=update/evolve 发布新版本。");

        return new
        {
            status = "ok",
            action = "publish",
            skill = ToHubElement(result.Value!),
            hint = "发布成功。后续版本请用 action=update / evolve，避免 409 冲突。",
        };
    }

    // ────────────────────────────────────────────────────────────
    // action=update / evolve：PublishVersionAsync
    // update = evolve 的便捷形态（evolution_action 默认 patch）
    // ────────────────────────────────────────────────────────────
    private async Task<object> PublishVersionAsync(
        ISkillHubService hub, string agentInstanceId, string workspaceId, SkillHubArgs args, CancellationToken ct, bool isEvolve)
    {
        var action = isEvolve ? "evolve" : "update";
        var skillId = RequireSkillId(args, action);

        if (string.IsNullOrWhiteSpace(args.Version))
            return Error(action, "version is required（要发布的新版本号，如 1.1.0）。");

        if (isEvolve)
        {
            if (string.IsNullOrWhiteSpace(args.EvolutionAction))
                return Error(action, "evolution_action is required for evolve（create|patch|split|compress|retire|merge|fork）。");
            if (string.IsNullOrWhiteSpace(args.ParentVersion))
                return Error(action, "parent_version is required for evolve（父版本号，构成血缘边）。");
        }

        var evolutionAction = isEvolve ? args.EvolutionAction! : Coalesce(args.EvolutionAction, "patch")!;
        if (!EvolutionActions.Contains(evolutionAction))
            return Error(action, $"evolution_action '{evolutionAction}' 非法。白名单：{string.Join('|', EvolutionActions)}。");

        var markdown = args.SkillMarkdown;
        if (string.IsNullOrWhiteSpace(markdown))
        {
            if (!await LocalSkillExistsAsync(agentInstanceId, skillId, ct))
                return Error(action, "需要 skill_markdown 参数，或本地已存在该 skill_id 的技能（自动读取其 SKILL.md）。");
            var file = await skillService.ReadFileAsync(agentInstanceId, skillId, null, ct);
            markdown = file.Content;
        }

        var result = await hub.PublishVersionAsync(skillId, new PublishHubSkillRequest(
            SkillId: skillId,
            Name: string.Empty, // 版本发布路径不读取 Name（与原链路请求体不含 name 等价）
            Summary: null,
            Description: null,
            Tags: null,
            Keywords: null,
            Version: args.Version,
            SkillMarkdown: markdown,
            ManifestJson: null,
            EvolutionAction: evolutionAction,
            ParentVersion: args.ParentVersion,
            RelatedSkillIds: args.RelatedSkillIds,
            PublishedByAgentId: agentInstanceId,
            PublishedByWorkspaceId: workspaceId,
            PublishNote: args.PublishNote,
            EvidenceJson: BuildEvidenceJson(args.Evidence)), ct);
        if (!result.IsOk)
            return MaybeConflictHint(
                MapStatusError(action, result.Status, result.Error),
                "该版本号已存在（409）；请换一个 version。");

        return new
        {
            status = "ok",
            action,
            skillId,
            version = ToHubElement(result.Value!),
        };
    }

    // ────────────────────────────────────────────────────────────
    // action=check_updates：ListUpdatesAsync(agentInstanceId)
    // ────────────────────────────────────────────────────────────
    private async Task<object> CheckUpdatesAsync(ISkillHubService hub, string agentInstanceId, CancellationToken ct)
    {
        var updates = ResponseArray(ToHubElement(await hub.ListUpdatesAsync(agentInstanceId, ct)));

        return new
        {
            status = "ok",
            action = "check_updates",
            agentInstanceId,
            count = updates?.Length ?? 0,
            updates = updates is null ? null : (object?)updates.ToList(),
        };
    }

    // ────────────────────────────────────────────────────────────
    // action=lineage：单技能 GetSkillLineageAsync 或全局 GetLineageAsync
    // ────────────────────────────────────────────────────────────
    private async Task<object> LineageAsync(ISkillHubService hub, SkillHubArgs args, CancellationToken ct)
    {
        var limit = args.Limit is > 0 ? Math.Min(args.Limit.Value, LineageNodeCap) : LineageNodeCap;

        JsonElement body;
        if (string.IsNullOrWhiteSpace(args.SkillId))
        {
            body = ToHubElement(await hub.GetLineageAsync(skillIds: null, limit, ct));
        }
        else
        {
            var single = await hub.GetSkillLineageAsync(args.SkillId.Trim(), ct);
            if (single is null)
                return MapStatusError("lineage", SkillHubStatus.NotFound, $"技能 '{args.SkillId.Trim()}' 不存在，无法构建血缘图");
            body = ToHubElement(single);
        }
        if (body.ValueKind is not JsonValueKind.Object and not JsonValueKind.Array)
            return MapStatusError("lineage", SkillHubStatus.NotFound, $"技能 '{args.SkillId?.Trim()}' 不存在，无法构建血缘图");

        var nodes = body.TryGetArray("nodes") ?? (body.ValueKind == JsonValueKind.Array
            ? body.EnumerateArray().ToArray()
            : null);
        var edges = body.TryGetArray("edges");
        var nodeList = nodes?.ToList();
        var nodeListValue = nodeList is null ? null : (object?)nodeList;
        var nodeCount = nodeList?.Count ?? 0;
        var edgeCount = edges?.Length ?? 0;
        var truncated = nodeCount > limit;

        return new
        {
            status = "ok",
            action = "lineage",
            skillId = args.SkillId,
            nodeCount,
            edgeCount,
            truncated,
            nodes = truncated && nodeList is not null
                ? (object?)nodeList.Take(limit).ToList()
                : nodeListValue,
            edges = edges is null ? null : (object?)edges.ToList(),
            generatedAt = body.TryGetString("generatedAt"),
        };
    }

    // ────────────────────────────────────────────────────────────
    // action=stats：GetStatsAsync
    // ────────────────────────────────────────────────────────────
    private async Task<object> StatsAsync(ISkillHubService hub, CancellationToken ct)
    {
        return new { status = "ok", action = "stats", stats = ToHubElement(await hub.GetStatsAsync(ct)) };
    }

    // ────────────────────────────────────────────────────────────
    // action=unpublish：RetireAsync（软删 → retired + 事件）
    // ────────────────────────────────────────────────────────────
    private async Task<object> UnpublishAsync(ISkillHubService hub, SkillHubArgs args, CancellationToken ct)
    {
        var skillId = RequireSkillId(args, "unpublish");
        var result = await hub.RetireAsync(skillId, ct);
        if (!result.IsOk)
            return MapStatusError("unpublish", result.Status, result.Error);

        return new
        {
            status = "ok",
            action = "unpublish",
            skillId,
            message = "技能已在中央库软删（status=retired，保留全部版本与审计事件）。本地副本不受影响。",
            serverResponse = NullElement(),
        };
    }

    // ════════════════════════════════════════════════════════════
    // 契约结果整形 / 状态映射辅助
    // ════════════════════════════════════════════════════════════

    /// <summary>把契约 DTO 序列化为 JSON 元素（camelCase、保留 null 字段，与平台控制器出参一致）。</summary>
    private static JsonElement ToHubElement<T>(T dto) where T : class =>
        JsonSerializer.SerializeToElement(dto, HubDtoOptions);

    /// <summary>语义结果状态 → 工具错误 JSON 文本（沿用 status=error + statusCode/message 的既有风格）。</summary>
    private static string MapStatusError(string action, SkillHubStatus status, string? error) => ToJson(status switch
    {
        SkillHubStatus.NotFound => Error(action, error ?? "中央库中不存在目标资源。", statusCode: 404, responseSnippet: ""),
        SkillHubStatus.Conflict => Error(action, error ?? "中央库中已存在冲突的目标。", statusCode: 409, responseSnippet: ""),
        SkillHubStatus.BadRequest => Error(action, error ?? "请求参数不合法。", statusCode: 400, responseSnippet: ""),
        _ => Error(action, error ?? "SKILL Hub 操作失败。"),
    });

    /// <summary>tags 多值 → 单字符串（与原链路"逗号连接后交给服务端 tag 过滤"的语义一致）。</summary>
    private static string? JoinTags(IReadOnlyList<string>? tags) =>
        tags is { Count: > 0 } ? string.Join(',', tags) : null;

    /// <summary>JSON null 元素（可安全序列化为 null，Undefined 不行）。</summary>
    private static JsonElement NullElement()
    {
        using var doc = JsonDocument.Parse("null");
        return doc.RootElement.Clone();
    }

    private static object[]? SummarizeSkills(JsonElement body)
    {
        // 契约 ListSkillsAsync 返回 List<HubSkillSummaryDto>；防御性兼容包装对象形态。
        var array = body.ValueKind == JsonValueKind.Array
            ? body.EnumerateArray().ToArray()
            : body.TryGetArray("skills");
        if (array is null) return null;
        return array
            .Select(item => (object)new
            {
                skillId = item.TryGetString("skillId"),
                name = item.TryGetString("name"),
                summary = item.TryGetString("summary"),
                latestVersion = item.TryGetString("latestVersion"),
                status = item.TryGetString("status"),
                versionCount = item.TryGetInt("versionCount"),
                installCount = item.TryGetInt("installCount"),
                tags = item.TryGetStringList("tags"),
            })
            .ToArray();
    }

    private static JsonElement DetailSkill(JsonElement detail) =>
        detail.ValueKind == JsonValueKind.Object && detail.TryGetProperty("skill", out var skill)
            ? skill
            : detail;

    /// <summary>ListUpdatesAsync 等返回裸数组；防御性兼容包装对象形态。</summary>
    private static JsonElement[]? ResponseArray(JsonElement body) =>
        body.ValueKind == JsonValueKind.Array
            ? body.EnumerateArray().ToArray()
            : body.TryGetArray("updates");

    private static string? BuildEvidenceJson(string? evidence)
    {
        if (string.IsNullOrWhiteSpace(evidence)) return null;
        var trimmed = evidence.Trim();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                return doc.RootElement.GetRawText();
            }
            catch (JsonException)
            {
                // 非法 JSON 字符串 → 落为字符串值，不让证据字段炸掉整个请求。
            }
        }
        return JsonSerializer.Serialize(trimmed, JsonOptions);
    }

    private static object Error(string action, string message, int? statusCode = null, string? responseSnippet = null) => new
    {
        status = "error",
        action,
        message,
        statusCode,
        responseSnippet,
    };

    private static object MaybeConflictHint(string errorJson, string hint)
    {
        try
        {
            using var doc = JsonDocument.Parse(errorJson);
            if (doc.RootElement.TryGetInt("statusCode") is 409 or 404)
            {
                var payload = JsonSerializer.Deserialize<Dictionary<string, object?>>(errorJson, JsonOptions);
                if (payload is not null)
                {
                    payload["hint"] = hint;
                    return payload;
                }
            }
        }
        catch (JsonException)
        {
            // 落回原始错误文本。
        }
        return errorJson;
    }

    private static string RequireSkillId(SkillHubArgs args, string action) =>
        string.IsNullOrWhiteSpace(args.SkillId)
            ? throw new ArgumentException($"skill_id is required for {action} action.")
            : args.SkillId.Trim();

    private static string NormalizeAction(string? action) =>
        string.IsNullOrWhiteSpace(action) ? "search" : action.Trim().ToLowerInvariant();

    private static string? Coalesce(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    private static T? Coalesce<T>(params T?[] values) where T : class =>
        values.FirstOrDefault(v => v is not null);

    private async Task<bool> LocalSkillExistsAsync(string agentInstanceId, string skillId, CancellationToken ct)
    {
        try
        {
            await skillService.GetAsync(agentInstanceId, skillId, ct);
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch (KeyNotFoundException)
        {
            return false;
        }
    }

    private static string ToJson(object value) => JsonSerializer.Serialize(value, JsonOptions);

    private static ToolExecutionResult Ok(object payload) => ToolExecutionResult.Ok(ToJson(payload));

    private static ToolExecutionResult Fail(string message) => ToolExecutionResult.Fail(message);

    private static string? Truncate(string? text, int max) =>
        string.IsNullOrEmpty(text) || text.Length <= max ? text : text[..max] + "…";
}

// ────────────────────────────────────────────────────────────────
// JsonElement 扩展（容错读取契约 DTO 的 JSON 形态字段）
// ────────────────────────────────────────────────────────────────
internal static class SkillHubJsonElementExtensions
{
    public static string? TryGetString(this JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static int? TryGetInt(this JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var number)
            ? number
            : null;

    public static List<string>? TryGetStringList(this JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var list = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { } text)
                list.Add(text);
        }
        return list;
    }

    public static JsonElement[]? TryGetArray(this JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().ToArray()
            : null;

    public static int GetArrayLength(this JsonElement element, string property) =>
        element.TryGetArray(property)?.Length ?? 0;
}

// ────────────────────────────────────────────────────────────────
// 工具参数（snake_case 入参，契约 §6.1 冻结动作表）
// ────────────────────────────────────────────────────────────────
public sealed record SkillHubArgs
{
    [ToolParam("Action to run: search, browse, get, install, publish, update, evolve, check_updates, lineage, stats, unpublish.")]
    public required string Action { get; init; }

    [ToolParam("search: keyword matched against name/summary/description/tags/keywords.")]
    public string? Query { get; init; }

    [ToolParam("search: filter by tag(s), comma-joined when sent to the hub.")]
    public IReadOnlyList<string>? Tags { get; init; }

    [ToolParam("publish: keywords for hub auto-matching (falls back to the local skill manifest keywords).")]
    [JsonPropertyName("keywords")]
    public IReadOnlyList<string>? Keywords { get; init; }

    [ToolParam("search/browse: filter by status (active/retired). Default: active.")]
    public string? Status { get; init; }

    [ToolParam("browse: 1-based page number. Default: 1.")]
    public int? Page { get; init; }

    [JsonPropertyName("page_size")]
    [ToolParam("browse: page size (1-200). Default: 50.")]
    public int? PageSize { get; init; }

    [ToolParam("SKILL id in the hub. Required for get/install/publish/update/evolve/unpublish.")]
    [JsonPropertyName("skill_id")]
    public string? SkillId { get; init; }

    [ToolParam("get: also fetch and attach the latest version's full SKILL.md content. Default: false.")]
    [JsonPropertyName("include_content")]
    public bool IncludeContent { get; init; }

    [ToolParam("install: specific version to install (default: latest). publish/update/evolve: version to publish.")]
    public string? Version { get; init; }

    [ToolParam("install: allow overwriting an existing local skill with the same id. Default: true.")]
    public bool Overwrite { get; init; } = true;

    [ToolParam("publish: display name (falls back to the local skill manifest name).")]
    public string? Name { get; init; }

    [ToolParam("Short summary of the skill.")]
    public string? Summary { get; init; }

    [ToolParam("Longer description of the skill.")]
    public string? Description { get; init; }

    [ToolParam("SKILL.md markdown content. Optional for publish/update/evolve when the skill exists locally.")]
    [JsonPropertyName("skill_markdown")]
    public string? SkillMarkdown { get; init; }

    [ToolParam("update/evolve: publish note stored with the version.")]
    [JsonPropertyName("publish_note")]
    public string? PublishNote { get; init; }

    [ToolParam("publish/update/evolve: visibility of the skill in the hub. Default: global.")]
    public string? Visibility { get; init; }

    [ToolParam("evolve: evolution action — create|patch|split|compress|retire|merge|fork. update defaults to patch.")]
    [JsonPropertyName("evolution_action")]
    public string? EvolutionAction { get; init; }

    [ToolParam("evolve: parent version this new version evolves from (creates the lineage edge).")]
    [JsonPropertyName("parent_version")]
    public string? ParentVersion { get; init; }

    [ToolParam("update/evolve: related skill ids (e.g. split/merge origins).")]
    [JsonPropertyName("related_skill_ids")]
    public IReadOnlyList<string>? RelatedSkillIds { get; init; }

    [ToolParam("update/evolve: evidence supporting this evolution. Raw JSON object/array or plain text.")]
    public string? Evidence { get; init; }

    [ToolParam("lineage: node limit for the global EVO MAP (1-200). Default: 200.")]
    public int? Limit { get; init; }
}
