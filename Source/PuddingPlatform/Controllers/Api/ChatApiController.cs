using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using PuddingCode.Platform;
using PuddingCode.Models;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services;

namespace PuddingPlatform.Controllers.Api;

/// <summary>管理员 Chat 代理 API — 将消息转发至 Controller 服务的 MessageIngress 端点。</summary>
[Authorize]
[ApiController]
[Route("api/workspaces/{workspaceId}/chat")]
public class ChatApiController(
    PlatformDbContext db,
    PlatformApiClient apiClient,
    ILogger<ChatApiController> logger) : ControllerBase
{
    // POST /api/workspaces/{workspaceId}/chat/message
    [HttpPost("message")]
    public async Task<ActionResult<AdminChatResponse>> SendMessage(
        string workspaceId, [FromBody] AdminChatRequest req, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        logger.LogInformation(
            "[Chat] REQUEST ws={WorkspaceId} agentId={AgentId} msgLen={MsgLen}",
            workspaceId, req.AgentId ?? "(none)", req.MessageText?.Length ?? 0);

        // 验证 workspace 存在
        var ws = await db.Workspaces.AsNoTracking()
            .FirstOrDefaultAsync(w => w.WorkspaceId == workspaceId, ct);
        if (ws is null)
            return NotFound(new { message = $"Workspace '{workspaceId}' 不存在" });

        if (string.IsNullOrWhiteSpace(req.MessageText))
            return BadRequest(new { message = "消息内容不能为空" });

        // 使用 web-chat 内置渠道 ID（已在 SeedDefaults 中注册）
        var channelId = $"web-chat-{workspaceId}";

        // 将当前登录用户作为外部用户 ID
        var userExternalId = User.Identity?.Name ?? "admin";

        // 解析 Agent 绑定的 LLM Provider 配置，随请求下发给 Controller
        LlmConfig? llmConfig = null;
        CapabilityPolicy? capabilityPolicy = null;
        IReadOnlyList<LlmToolDefinition>? toolDefinitions = null;
        string? agentTemplateId = null;
        WorkspaceAgentEntity? agent = null;
        if (!string.IsNullOrEmpty(req.AgentId))
        {
            agent = await db.WorkspaceAgents.AsNoTracking()
                .FirstOrDefaultAsync(a => a.AgentId == req.AgentId && a.IsEnabled, ct);
            agentTemplateId = agent?.SourceTemplateId;

            var resolved = await ResolveCapabilitiesAsync(
                db, workspaceId, agentTemplateId, ct);
            capabilityPolicy = resolved.Policy;
            toolDefinitions = resolved.ToolDefinitions;

            if (agent?.PreferredProviderId is not null)
            {
                var provider = await db.LlmProviders.AsNoTracking()
                    .FirstOrDefaultAsync(p => p.ProviderId == agent.PreferredProviderId && p.IsEnabled, ct);
                if (provider is not null)
                {
                    llmConfig = new LlmConfig
                    {
                        Endpoint = provider.BaseUrl,
                        ApiKey = provider.ApiKey,
                        ModelId = agent.PreferredModelId,
                    };
                    logger.LogInformation(
                        "[Chat] LlmConfig resolved: provider={ProviderId} model={ModelId} endpoint={Endpoint}",
                        agent.PreferredProviderId, agent.PreferredModelId ?? "(none)", provider.BaseUrl);
                }
                else
                {
                    logger.LogWarning(
                        "[Chat] LlmConfig NOT resolved: agentId={AgentId} provider={ProviderId} not found/disabled (fallback .env)",
                        req.AgentId, agent.PreferredProviderId);
                }
            }
            else
            {
                logger.LogInformation(
                    "[Chat] agent={AgentId} has no PreferredProviderId, LlmConfig fallback to .env",
                    req.AgentId);
            }

            logger.LogInformation(
                "[Chat] Agent routing resolved: agentId={AgentId} templateId={TemplateId} hasCapability={HasCapability} allowShell={AllowShell}",
                req.AgentId,
                agentTemplateId ?? "(none)",
                capabilityPolicy is not null,
                capabilityPolicy?.AllowShellExecution == true);
        }

        var result = await apiClient.SendMessageAsync(
            channelId:      channelId,
            userExternalId: userExternalId,
            messageText:    req.MessageText,
            workspaceId:    workspaceId,
            sessionId:      req.SessionId,
            llmConfig:      llmConfig,
            agentTemplateId: agentTemplateId,
            capabilityPolicy: capabilityPolicy,
            toolDefinitions: toolDefinitions,
            ct:             ct);

        sw.Stop();
        if (result is null)
        {
            logger.LogError(
                "[Chat] Controller unreachable ws={WorkspaceId} elapsed={Elapsed}ms",
                workspaceId, sw.ElapsedMilliseconds);
            return StatusCode(502, new { message = "Controller 服务未响应，请确认服务运行状态" });
        }

        if (result.IsSuccess)
            logger.LogInformation(
                "[Chat] OK ws={WorkspaceId} msgId={MessageId} elapsed={Elapsed}ms",
                workspaceId, result.MessageId, sw.ElapsedMilliseconds);
        else
            logger.LogWarning(
                "[Chat] FAILED ws={WorkspaceId} msgId={MessageId} elapsed={Elapsed}ms error={Error}",
                workspaceId, result.MessageId, sw.ElapsedMilliseconds, result.ErrorMessage);

        return Ok(new AdminChatResponse(
            result.MessageId,
            result.SessionId,
            result.Reply,
            result.IsSuccess,
            result.ErrorMessage,
            result.TurnSteps));
    }

    private sealed record ResolvedCapabilities(
        CapabilityPolicy? Policy,
        IReadOnlyList<LlmToolDefinition>? ToolDefinitions);

    /// <summary>
    /// 规范化模板 ID：前端会给全局模板附加 "global:" 前缀以区分工作区模板，
    /// 后端查询时需剥除前缀后再匹配数据库中的 TemplateId。
    /// </summary>
    private static (string RawId, string GlobalId) NormalizeTemplateId(string templateId)
    {
        const string prefix = "global:";
        return templateId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? (templateId, templateId[prefix.Length..])
            : (templateId, templateId);
    }

    private static async Task<ResolvedCapabilities> ResolveCapabilitiesAsync(
        PlatformDbContext db,
        string workspaceId,
        string? templateId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(templateId))
            return new ResolvedCapabilities(null, null);

        var (rawId, globalId) = NormalizeTemplateId(templateId);

        // 工作区模板：优先用原始 ID 匹配（支持 workspace 自定义模板），
        // 再退一步用去前缀 ID 匹配（兼容直接传 templateId 字符串的场景）。
        var workspaceTemplate = await db.WorkspaceAgentTemplates.AsNoTracking()
            .FirstOrDefaultAsync(t => t.WorkspaceId == workspaceId
                                   && (t.TemplateId == rawId || t.TemplateId == globalId)
                                   && t.IsEnabled, ct);
        if (workspaceTemplate is not null)
        {
            var selected = ParseStringList(workspaceTemplate.SelectedCapabilityIdsJson);
            var capabilities = await db.Capabilities.AsNoTracking()
                .Where(c => selected.Contains(c.CapabilityId) && c.IsEnabled)
                .ToListAsync(ct);
            return new ResolvedCapabilities(
                BuildPolicy(
                workspaceTemplate.AllowFileWrite,
                workspaceTemplate.AllowShellExecution,
                workspaceTemplate.AllowNetworkAccess,
                workspaceTemplate.AllowedToolNamesJson,
                workspaceTemplate.Role,
                capabilities),
                BuildToolDefinitions(capabilities));
        }

        // 全局模板：用去前缀的 ID 查询（DB 中存的是无前缀的裸 TemplateId）
        var globalTemplate = await db.GlobalAgentTemplates.AsNoTracking()
            .FirstOrDefaultAsync(t => t.TemplateId == globalId && t.IsEnabled, ct);
        if (globalTemplate is not null)
        {
            var selected = ParseStringList(globalTemplate.SelectedCapabilityIdsJson);
            var capabilities = await db.Capabilities.AsNoTracking()
                .Where(c => selected.Contains(c.CapabilityId) && c.IsEnabled)
                .ToListAsync(ct);
            return new ResolvedCapabilities(
                BuildPolicy(
                globalTemplate.AllowFileWrite,
                globalTemplate.AllowShellExecution,
                globalTemplate.AllowNetworkAccess,
                globalTemplate.AllowedToolNamesJson,
                globalTemplate.Role,
                capabilities),
                BuildToolDefinitions(capabilities));
        }

        // DB 不存在模板时，按常见 code-agent 兜底（同时兼容 global: 前缀写法）
        if (globalId.Equals("code-agent", StringComparison.OrdinalIgnoreCase)
            || globalId.Equals("workspace-task-agent", StringComparison.OrdinalIgnoreCase))
        {
            return new ResolvedCapabilities(
                new CapabilityPolicy
                {
                    AllowFileWrite = true,
                    AllowShellExecution = true,
                    AllowNetworkAccess = false,
                    AllowedToolNames = ["bash", "file_read", "file_write"],
                },
                [
                    new LlmToolDefinition
                    {
                        Name = "bash",
                        Description = "Execute shell command in sandbox",
                        Parameters = new ToolParameterSchema(
                            [new ToolParameter("command", "string", "Shell command to execute")],
                            ["command"]),
                    }
                ]);
        }

        return new ResolvedCapabilities(null, null);
    }

    private static IReadOnlyList<LlmToolDefinition> BuildToolDefinitions(
        IReadOnlyList<CapabilityEntity> capabilities)
    {
        var map = new Dictionary<string, LlmToolDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var cap in capabilities)
        {
            if (string.IsNullOrWhiteSpace(cap.ToolName))
                continue;

            var name = cap.ToolName.Trim();
            if (map.ContainsKey(name))
                continue;

            var schema = ParseToolParameterSchema(cap.ToolParametersJson);
            map[name] = new LlmToolDefinition
            {
                Name = name,
                Description = string.IsNullOrWhiteSpace(cap.Description)
                    ? $"Invoke capability '{cap.Name}'"
                    : cap.Description,
                Parameters = schema,
            };
        }

        return map.Values.ToList();
    }

    private static ToolParameterSchema ParseToolParameterSchema(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return DefaultCommandSchema();

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return DefaultCommandSchema();

            var properties = new List<ToolParameter>();
            var required = new List<string>();

            if (root.TryGetProperty("properties", out var propsEl) && propsEl.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in propsEl.EnumerateObject())
                {
                    var propVal = prop.Value;
                    if (propVal.ValueKind != JsonValueKind.Object)
                    {
                        properties.Add(new ToolParameter(prop.Name, "string", string.Empty));
                        continue;
                    }

                    properties.Add(new ToolParameter(
                        prop.Name,
                        propVal.TryGetProperty("type", out var propType) && propType.ValueKind == JsonValueKind.String
                            ? propType.GetString() ?? "string"
                            : "string",
                        propVal.TryGetProperty("description", out var desc) && desc.ValueKind == JsonValueKind.String
                            ? desc.GetString() ?? string.Empty
                            : string.Empty));
                }
            }

            if (root.TryGetProperty("required", out var reqEl) && reqEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in reqEl.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        var val = item.GetString();
                        if (!string.IsNullOrWhiteSpace(val))
                            required.Add(val);
                    }
                }
            }

            if (properties.Count == 0)
                return DefaultCommandSchema();

            return new ToolParameterSchema(properties, required);
        }
        catch
        {
            return DefaultCommandSchema();
        }
    }

    private static ToolParameterSchema DefaultCommandSchema()
    {
        return new ToolParameterSchema(
            [new ToolParameter("command", "string", "Tool input command")],
            ["command"]);
    }

    private static CapabilityPolicy BuildPolicy(
        bool allowFileWrite,
        bool allowShellExecution,
        bool allowNetworkAccess,
        string allowedToolNamesJson,
        string role,
        IReadOnlyList<CapabilityEntity>? selectedCapabilities = null)
    {
        var tools = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var t in JsonSerializer.Deserialize<List<string>>(allowedToolNamesJson) ?? [])
            {
                if (!string.IsNullOrWhiteSpace(t))
                    tools.Add(t.Trim());
            }
        }
        catch
        {
            // ignore malformed JSON and continue with selected capabilities.
        }

        foreach (var cap in selectedCapabilities ?? [])
        {
            if (!string.IsNullOrWhiteSpace(cap.ToolName))
                tools.Add(cap.ToolName.Trim());
        }

        var selectedAllowShell = (selectedCapabilities ?? []).Any(c => c.RequiresShellExecution);
        var selectedAllowWrite = (selectedCapabilities ?? []).Any(c => c.RequiresFileWrite);
        var selectedAllowNetwork = (selectedCapabilities ?? []).Any(c => c.RequiresNetworkAccess);

        var isTaskRole = role.Equals("Task", StringComparison.OrdinalIgnoreCase);

        // 兼容历史模板：过去无能力字段时，Task 默认应可使用 bash
        if (isTaskRole && tools.Count == 0)
            tools.UnionWith(["bash", "file_read", "file_write"]);

        return new CapabilityPolicy
        {
            AllowFileWrite = allowFileWrite || selectedAllowWrite || isTaskRole,
            AllowShellExecution = allowShellExecution || selectedAllowShell || isTaskRole,
            AllowNetworkAccess = allowNetworkAccess || selectedAllowNetwork,
            AllowedToolNames = tools.ToList(),
        };
    }

    private static List<string> ParseStringList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? []; }
        catch { return []; }
    }
}
