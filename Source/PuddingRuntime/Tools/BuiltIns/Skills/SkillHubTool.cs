using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using PuddingCode.Configuration;
using PuddingCode.Models;
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
public sealed class SkillHubTool(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    AgentSkillFileService skillService) : PuddingToolBase<SkillHubArgs>
{
    /// <summary>命名 HttpClient（DI 组合根注册，UA = PuddingUserAgent.Value，设计方案 §6.1）。</summary>
    public const string HttpClientName = "SkillHubClient";

    private const int LineageNodeCap = 200;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

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
            return action switch
            {
                "search" => Ok(await SearchAsync(args, ct)),
                "browse" => Ok(await BrowseAsync(args, ct)),
                "get" => Ok(await GetAsync(args, ct)),
                "install" => Ok(await InstallAsync(agentInstanceId, context.WorkspaceId, args, ct)),
                "publish" => Ok(await PublishAsync(agentInstanceId, context.WorkspaceId, args, ct)),
                "update" => Ok(await PublishVersionAsync(agentInstanceId, context.WorkspaceId, args, ct, isEvolve: false)),
                "evolve" => Ok(await PublishVersionAsync(agentInstanceId, context.WorkspaceId, args, ct, isEvolve: true)),
                "check_updates" => Ok(await CheckUpdatesAsync(agentInstanceId, ct)),
                "lineage" => Ok(await LineageAsync(args, ct)),
                "stats" => Ok(await StatsAsync(ct)),
                "unpublish" => Ok(await UnpublishAsync(args, ct)),
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
    // action=search：GET /skills?query=&tag=&status= —— 精简列表
    // ────────────────────────────────────────────────────────────
    private async Task<object> SearchAsync(SkillHubArgs args, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(args.Query) && (args.Tags is null || args.Tags.Count == 0))
            return Error("search", "query 或 tags 至少提供一个；仅浏览请用 action=browse。");

        var path = BuildSkillsListPath(args.Query, args.Tags, args.Status, args.Page, args.PageSize);
        var (ok, body, error) = await GetJsonAsync(path, "search", ct);
        if (!ok) return error!;

        var skills = SummarizeSkills(body);

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
    // action=browse：GET /skills（不带 query）—— 分页浏览
    // ────────────────────────────────────────────────────────────
    private async Task<object> BrowseAsync(SkillHubArgs args, CancellationToken ct)
    {
        var path = BuildSkillsListPath(null, null, args.Status, args.Page, args.PageSize);
        var (ok, body, error) = await GetJsonAsync(path, "browse", ct);
        if (!ok) return error!;

        var skills = SummarizeSkills(body);

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
    // action=get：GET /skills/{id}（include_content=true 附最新版全文）
    // ────────────────────────────────────────────────────────────
    private async Task<object> GetAsync(SkillHubArgs args, CancellationToken ct)
    {
        var skillId = RequireSkillId(args, "get");
        var (ok, detail, error) = await GetJsonAsync($"/api/skill-hub/skills/{Uri.EscapeDataString(skillId)}", "get", ct);
        if (!ok) return error!;

        if (!args.IncludeContent)
            return new { status = "ok", action = "get", skill = DetailSkill(detail) };

        var latestVersion = DetailSkill(detail).TryGetProperty("latestVersion", out var lv)
            ? lv.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(latestVersion))
            return Error("get", "无法从详情中解析 latestVersion，无法取全文。");

        var (okV, versionBody, errorV) = await GetJsonAsync(
            $"/api/skill-hub/skills/{Uri.EscapeDataString(skillId)}/versions/{Uri.EscapeDataString(latestVersion)}", "get", ct);
        if (!okV) return errorV!;

        return new
        {
            status = "ok",
            action = "get",
            skill = DetailSkill(detail),
            contentVersion = latestVersion,
            contentHash = versionBody.TryGetString("contentHash"),
            skillMarkdown = versionBody.TryGetString("skillMarkdown"),
        };
    }

    // ────────────────────────────────────────────────────────────
    // action=install：取版本全文 → 落本地 → POST /installs 登记
    // ────────────────────────────────────────────────────────────
    private async Task<object> InstallAsync(string agentInstanceId, string workspaceId, SkillHubArgs args, CancellationToken ct)
    {
        var skillId = RequireSkillId(args, "install");

        // 1. 详情：拿 name / latestVersion / tags / keywords 等（同时校验技能存在）。
        var (ok, detail, error) = await GetJsonAsync($"/api/skill-hub/skills/{Uri.EscapeDataString(skillId)}", "install", ct);
        if (!ok) return error!;
        var hubSkill = DetailSkill(detail);
        var displayName = hubSkill.TryGetString("name") ?? skillId;

        // 2. 版本：显式指定或取最新。
        var version = Coalesce(args.Version, hubSkill.TryGetString("latestVersion"));
        if (string.IsNullOrWhiteSpace(version))
            return Error("install", "无法确定要安装的版本（详情缺少 latestVersion）。");

        // 3. 取该版本全文。
        var (okV, versionBody, errorV) = await GetJsonAsync(
            $"/api/skill-hub/skills/{Uri.EscapeDataString(skillId)}/versions/{Uri.EscapeDataString(version)}", "install", ct);
        if (!okV) return errorV!;
        var markdown = versionBody.TryGetString("skillMarkdown");
        if (string.IsNullOrEmpty(markdown))
            return Error("install", $"版本 {version} 响应缺少 skillMarkdown，终止安装。");

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

        // 5. POST /installs 登记安装台账（upsert）。登记失败不回滚本地安装，但显式上报。
        object? installRecord = null;
        var registerError = (string?)null;
        var (okI, installBody, errorI) = await SendJsonAsync(
            HttpMethod.Post,
            "/api/skill-hub/installs",
            new
            {
                skillId,
                agentInstanceId,
                workspaceId,
                installedVersion = version,
                contentHash = versionBody.TryGetString("contentHash"),
                installedBy = $"agent:{agentInstanceId}",
            },
            "install", ct);
        if (okI) installRecord = installBody;
        else registerError = Truncate(errorI, 500);

        return new
        {
            status = "ok",
            action = "install",
            agentInstanceId,
            skillId,
            installedVersion = version,
            localPath,
            overwroteLocal = localExists,
            installRegistered = okI,
            installRecord,
            registerError,
        };
    }

    // ────────────────────────────────────────────────────────────
    // action=publish：POST /skills（409=已存在 → 提示改用 update/evolve）
    // ────────────────────────────────────────────────────────────
    private async Task<object> PublishAsync(string agentInstanceId, string workspaceId, SkillHubArgs args, CancellationToken ct)
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

        var body = new
        {
            skillId,
            name,
            summary = Coalesce(args.Summary, localManifest?.Summary is { Length: > 0 } s ? s : null),
            description = Coalesce(args.Description, localManifest?.Description),
            tags = (IReadOnlyList<string>?)Coalesce(args.Tags, localManifest?.Tags),
            keywords = (IReadOnlyList<string>?)Coalesce(args.Keywords, localManifest?.Keywords),
            version,
            skillMarkdown = markdown,
            evolutionAction = args.EvolutionAction,
            parentVersion = args.ParentVersion,
            publishedByAgentId = agentInstanceId,
            publishedByWorkspaceId = workspaceId,
            publishNote = args.PublishNote,
            evidenceJson = BuildEvidenceJson(args.Evidence),
            visibility = string.IsNullOrWhiteSpace(args.Visibility) ? "global" : args.Visibility,
        };

        var (ok, responseBody, error) = await SendJsonAsync(HttpMethod.Post, "/api/skill-hub/skills", body, "publish", ct);
        if (!ok) return MaybeConflictHint(error!, "该 skill_id 已存在于中央库；请改用 action=update/evolve 发布新版本。");

        return new
        {
            status = "ok",
            action = "publish",
            skill = responseBody,
            hint = "发布成功。后续版本请用 action=update / evolve，避免 409 冲突。",
        };
    }

    // ────────────────────────────────────────────────────────────
    // action=update / evolve：POST /skills/{id}/versions
    // update = evolve 的便捷形态（evolution_action 默认 patch）
    // ────────────────────────────────────────────────────────────
    private async Task<object> PublishVersionAsync(
        string agentInstanceId, string workspaceId, SkillHubArgs args, CancellationToken ct, bool isEvolve)
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

        var body = new
        {
            skillId,
            version = args.Version,
            skillMarkdown = markdown,
            evolutionAction,
            parentVersion = args.ParentVersion,
            relatedSkillIds = args.RelatedSkillIds,
            publishedByAgentId = agentInstanceId,
            publishedByWorkspaceId = workspaceId,
            publishNote = args.PublishNote,
            evidenceJson = BuildEvidenceJson(args.Evidence),
        };

        var (ok, responseBody, error) = await SendJsonAsync(
            HttpMethod.Post,
            $"/api/skill-hub/skills/{Uri.EscapeDataString(skillId)}/versions",
            body, action, ct);
        if (!ok) return MaybeConflictHint(error!, "该版本号已存在（409）；请换一个 version。");

        return new
        {
            status = "ok",
            action,
            skillId,
            version = responseBody,
        };
    }

    // ────────────────────────────────────────────────────────────
    // action=check_updates：GET /updates?agentInstanceId=
    // ────────────────────────────────────────────────────────────
    private async Task<object> CheckUpdatesAsync(string agentInstanceId, CancellationToken ct)
    {
        var (ok, body, error) = await GetJsonAsync(
            $"/api/skill-hub/updates?agentInstanceId={Uri.EscapeDataString(agentInstanceId)}", "check_updates", ct);
        if (!ok) return error!;

        var updates = ResponseArray(body);

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
    // action=lineage：单技能 GET /skills/{id}/lineage 或全局 GET /lineage
    // ────────────────────────────────────────────────────────────
    private async Task<object> LineageAsync(SkillHubArgs args, CancellationToken ct)
    {
        var limit = args.Limit is > 0 ? Math.Min(args.Limit.Value, LineageNodeCap) : LineageNodeCap;

        var (ok, body, error) = string.IsNullOrWhiteSpace(args.SkillId)
            ? await GetJsonAsync($"/api/skill-hub/lineage?limit={limit}", "lineage", ct)
            : await GetJsonAsync($"/api/skill-hub/skills/{Uri.EscapeDataString(args.SkillId.Trim())}/lineage", "lineage", ct);
        if (!ok) return error!;

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
    // action=stats：GET /stats
    // ────────────────────────────────────────────────────────────
    private async Task<object> StatsAsync(CancellationToken ct)
    {
        var (ok, body, error) = await GetJsonAsync("/api/skill-hub/stats", "stats", ct);
        if (!ok) return error!;

        return new { status = "ok", action = "stats", stats = body };
    }

    // ────────────────────────────────────────────────────────────
    // action=unpublish：DELETE /skills/{id}（软删 → retired + 事件）
    // ────────────────────────────────────────────────────────────
    private async Task<object> UnpublishAsync(SkillHubArgs args, CancellationToken ct)
    {
        var skillId = RequireSkillId(args, "unpublish");
        var (ok, body, error) = await SendJsonAsync(
            HttpMethod.Delete,
            $"/api/skill-hub/skills/{Uri.EscapeDataString(skillId)}",
            null, "unpublish", ct);
        if (!ok) return error!;

        return new
        {
            status = "ok",
            action = "unpublish",
            skillId,
            message = "技能已在中央库软删（status=retired，保留全部版本与审计事件）。本地副本不受影响。",
            serverResponse = body,
        };
    }

    // ════════════════════════════════════════════════════════════
    // HTTP 基础设施
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 解析 SKILL Hub 端点与机器凭据。SkillHub:BaseUrl 未配置时不再静默回退到
    /// http://localhost:5000（宿主实际监听端口未必是 5000），而是返回结构化配置错误并
    /// 明确指出缺失的配置键；SkillHub:ApiKey（及回退 AdminApiKey）缺失时也在错误中提示，
    /// 但绝不回显密钥值。
    /// </summary>
    private (string? BaseUrl, string? ApiKey, string? ConfigError) ResolveEndpoint()
    {
        var rawBaseUrl = configuration["SkillHub:BaseUrl"];
        if (string.IsNullOrWhiteSpace(rawBaseUrl))
        {
            var message =
                "SKILL Hub 未配置：缺少配置键 SkillHub:BaseUrl（已停用 http://localhost:5000 静默回退）。" +
                "请在该 Agent 的 DataRoot system.json（或环境变量 / 命令行）中设置 SkillHub:BaseUrl，" +
                "指向 PuddingHost 实际监听地址（见启动日志 Server bound addresses / local control address）。";
            var skillHubKeyConfigured = !string.IsNullOrWhiteSpace(configuration["SkillHub:ApiKey"]);
            var adminKeyConfigured = !string.IsNullOrWhiteSpace(configuration["AdminApiKey"]);
            if (!skillHubKeyConfigured && !adminKeyConfigured)
            {
                message += " 同时缺少配置键 SkillHub:ApiKey（AdminApiKey 亦未配置）：宿主端 skill-hub 端点要求 X-Admin-Api-Key 机器凭据，缺失将被 401 拒绝。";
            }
            else if (!skillHubKeyConfigured)
            {
                message += " 提示：SkillHub:ApiKey 未配置，当前将回退使用 AdminApiKey 作为 X-Admin-Api-Key 机器凭据。";
            }

            return (null, null, message);
        }

        return (rawBaseUrl.TrimEnd('/'), configuration["SkillHub:ApiKey"] ?? configuration["AdminApiKey"], null);
    }

    private async Task<(bool Ok, JsonElement Body, string? Error)> GetJsonAsync(string path, string action, CancellationToken ct) =>
        await SendJsonAsync(HttpMethod.Get, path, body: null, action, ct);

    /// <summary>
    /// 发送请求并解析 JSON。任何失败都不抛异常：返回 Ok=false + 结构化 Error 对象
    /// （含 statusCode 与响应体前 500 字符，契约 §6.1 返回约定）。
    /// </summary>
    private async Task<(bool Ok, JsonElement Body, string? Error)> SendJsonAsync(
        HttpMethod method, string path, object? body, string action, CancellationToken ct)
    {
        var (baseUrl, apiKey, configError) = ResolveEndpoint();
        if (baseUrl is null || configError is not null)
            return (false, default, ToJson(Error(action, configError ?? "SKILL Hub 端点配置无效：缺少配置键 SkillHub:BaseUrl。")));
        var client = httpClientFactory.CreateClient(HttpClientName);

        using var request = new HttpRequestMessage(method, baseUrl + path);
        if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json");
        if (!string.IsNullOrWhiteSpace(apiKey))
            request.Headers.Add("X-Admin-Api-Key", apiKey);

        HttpResponseMessage response;
        string responseText;
        try
        {
            response = await client.SendAsync(request, ct);
            responseText = await response.Content.ReadAsStringAsync(ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (false, default, ToJson(Error(action, $"SKILL Hub 请求超时：{baseUrl}{path}")));
        }
        catch (Exception ex)
        {
            return (false, default, ToJson(Error(action, $"SKILL Hub 不可达（{baseUrl}{path}）：{ex.Message}")));
        }

        if (!response.IsSuccessStatusCode)
        {
            return (false, default, ToJson(Error(
                action,
                $"SKILL Hub 返回 HTTP {(int)response.StatusCode} {response.ReasonPhrase}：{baseUrl}{path}",
                statusCode: (int)response.StatusCode,
                responseSnippet: Truncate(responseText, 500))));
        }

        if (string.IsNullOrWhiteSpace(responseText))
        {
            // 204/空响应体：返回 null 元素（ValueKind.Null 可安全序列化；default 是 Undefined，不可序列化）。
            using var nullDoc = JsonDocument.Parse("null");
            return (true, nullDoc.RootElement.Clone(), null);
        }

        try
        {
            var element = JsonSerializer.Deserialize<JsonElement>(responseText, JsonOptions);
            return (true, element, null);
        }
        catch (JsonException ex)
        {
            return (false, default, ToJson(Error(action, $"SKILL Hub 响应不是合法 JSON：{ex.Message}（{Truncate(responseText, 200)}）")));
        }
    }

    // ════════════════════════════════════════════════════════════
    // 响应整形 / 参数辅助
    // ════════════════════════════════════════════════════════════

    private static string BuildSkillsListPath(string? query, IReadOnlyList<string>? tags, string? status, int? page, int? pageSize)
    {
        var sb = new StringBuilder("/api/skill-hub/skills?");
        void Append(string key, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            if (sb[^1] != '?') sb.Append('&');
            sb.Append(key).Append('=').Append(Uri.EscapeDataString(value));
        }
        Append("query", query);
        Append("tag", tags is { Count: > 0 } ? string.Join(',', tags) : null);
        Append("status", status);
        Append("page", (page ?? 1).ToString());
        Append("pageSize", Math.Clamp(pageSize ?? 50, 1, 200).ToString());
        return sb.ToString();
    }

    private static object[]? SummarizeSkills(JsonElement body)
    {
        // 服务端 GET /skills 返回裸数组（List<HubSkillSummaryDto>）；防御性兼容包装对象。
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

    /// <summary>GET /updates 等端点返回裸数组；防御性兼容包装对象形态。</summary>
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
// JsonElement 扩展（容错读取后端 DTO 字段）
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
