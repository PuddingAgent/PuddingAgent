using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using PuddingCode.Platform;
using PuddingCode.Models;
using PuddingMemoryEngine.Data;
using PuddingMemoryEngine.Entities;
using PuddingCode.Services;
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
    MinioStorageService minio,
    IServiceScopeFactory scopeFactory,
    IDbContextFactory<MemoryDbContext> memoryDbFactory,
    JsonlSessionWriter jsonlWriter,
    SessionEventHub eventHub,
    ILogger<ChatApiController> logger) : ControllerBase
{
    private static readonly Regex VaultPlaceholderRegex =
        new(@"^\{\{vault:(?<name>[a-zA-Z0-9._-]+)\}\}$", RegexOptions.Compiled);

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
        IReadOnlyList<SkillPackageInfo>? skillPackages = null;
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

            // 解析 Agent 模板关联的 Skill 包，生成预签名下载 URL
            skillPackages = await ResolveSkillPackagesAsync(db, minio, agentTemplateId, ct);

            if (agent?.PreferredProviderId is not null)
            {
                var provider = await db.LlmProviders.AsNoTracking()
                    .FirstOrDefaultAsync(p => p.ProviderId == agent.PreferredProviderId && p.IsEnabled, ct);
                if (provider is not null)
                {
                    var normalizedModelId = NormalizePreferredModelId(provider.ProviderId, agent.PreferredModelId);
                    var keyVaultId = await ResolveProviderKeyVaultIdAsync(provider.ApiKey, ct);
                    llmConfig = new LlmConfig
                    {
                        Endpoint = provider.BaseUrl,
                        KeyVaultId = keyVaultId,
                        ModelId = normalizedModelId,
                        ApiKey = string.IsNullOrWhiteSpace(keyVaultId) ? provider.ApiKey : null,
                        ReasoningEffort = await ResolveReasoningEffortAsync(db, workspaceId, agentTemplateId, ct),
                    };
                    logger.LogInformation(
                        "[Chat] LlmConfig resolved: provider={ProviderId} model={ModelId} rawModel={RawModelId} endpoint={Endpoint} hasKeyVaultRef={HasKeyVaultRef}",
                        agent.PreferredProviderId,
                        normalizedModelId ?? "(none)",
                        agent.PreferredModelId ?? "(none)",
                        provider.BaseUrl,
                        !string.IsNullOrWhiteSpace(keyVaultId));

                    if (string.IsNullOrWhiteSpace(keyVaultId) && !string.IsNullOrWhiteSpace(provider.ApiKey))
                    {
                        logger.LogWarning(
                            "[Chat] provider={ProviderId} ApiKey is plaintext; forwarding as ApiKey to Runtime.",
                            agent.PreferredProviderId);
                    }
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
            skillPackages: skillPackages,
            forceNewSession: req.ForceNewSession,
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
                result.Usage,
            result.TurnSteps));
    }

    // POST /api/workspaces/{workspaceId}/chat/message/stream
    [HttpPost("message/stream")]
    public async Task<IActionResult> SendMessageStream(
        string workspaceId, [FromBody] AdminChatRequest req, CancellationToken ct)
    {
        logger.LogInformation(
            "[Chat] STREAM REQUEST ws={WorkspaceId} agentId={AgentId} msgLen={MsgLen}",
            workspaceId, req.AgentId ?? "(none)", req.MessageText?.Length ?? 0);

        var ws = await db.Workspaces.AsNoTracking()
            .FirstOrDefaultAsync(w => w.WorkspaceId == workspaceId, ct);
        if (ws is null)
            return NotFound(new { message = $"Workspace '{workspaceId}' 不存在" });

        if (string.IsNullOrWhiteSpace(req.MessageText))
            return BadRequest(new { message = "消息内容不能为空" });

        var channelId = $"web-chat-{workspaceId}";
        var userExternalId = User.Identity?.Name ?? "admin";

        LlmConfig? llmConfig = null;
        CapabilityPolicy? capabilityPolicy = null;
        IReadOnlyList<LlmToolDefinition>? toolDefinitions = null;
        IReadOnlyList<SkillPackageInfo>? skillPackages = null;
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
            skillPackages = await ResolveSkillPackagesAsync(db, minio, agentTemplateId, ct);

            if (agent?.PreferredProviderId is not null)
            {
                var provider = await db.LlmProviders.AsNoTracking()
                    .FirstOrDefaultAsync(p => p.ProviderId == agent.PreferredProviderId && p.IsEnabled, ct);
                if (provider is not null)
                {
                    var normalizedModelId = NormalizePreferredModelId(provider.ProviderId, agent.PreferredModelId);
                    var keyVaultId = await ResolveProviderKeyVaultIdAsync(provider.ApiKey, ct);
                    llmConfig = new LlmConfig
                    {
                        Endpoint = provider.BaseUrl,
                        KeyVaultId = keyVaultId,
                        ModelId = normalizedModelId,
                        ApiKey = string.IsNullOrWhiteSpace(keyVaultId) ? provider.ApiKey : null,
                        ReasoningEffort = await ResolveReasoningEffortAsync(db, workspaceId, agentTemplateId, ct),
                    };
                    logger.LogInformation(
                        "[Chat] STREAM LlmConfig resolved: provider={ProviderId} model={ModelId} rawModel={RawModelId} endpoint={Endpoint} hasKeyVaultRef={HasKeyVaultRef}",
                        agent.PreferredProviderId,
                        normalizedModelId ?? "(none)",
                        agent.PreferredModelId ?? "(none)",
                        provider.BaseUrl,
                        !string.IsNullOrWhiteSpace(keyVaultId));

                    if (string.IsNullOrWhiteSpace(keyVaultId) && !string.IsNullOrWhiteSpace(provider.ApiKey))
                    {
                        logger.LogWarning(
                            "[Chat] provider={ProviderId} ApiKey is plaintext; forwarding as ApiKey to Runtime.",
                            agent.PreferredProviderId);
                    }
                }
                else
                {
                    logger.LogWarning(
                        "[Chat] STREAM LlmConfig NOT resolved: agentId={AgentId} provider={ProviderId} not found/disabled (fallback .env)",
                        req.AgentId, agent.PreferredProviderId);
                }
            }
        }

        ConfigureSseResponse(Response);

        // 流式消息累积
        var streamSessionId = req.SessionId;
        var agentReplyBuilder = new System.Text.StringBuilder();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        TokenUsageDto? streamUsage = null;
        var isNewSession = string.IsNullOrEmpty(req.SessionId) || req.ForceNewSession;
        var forkParentSessionId = req.ForceNewSession && !string.IsNullOrWhiteSpace(req.SessionId)
            ? req.SessionId
            : null;
        var persisted = false; // 防止重复持久化

        // ── 解析帧到累积变量（在线程安全的上下文中调用） ──
        void ParseFrame(ServerSentEventFrame frame)
        {
            try
            {
                using var doc = JsonDocument.Parse(frame.Data);
                var root = doc.RootElement;
                if (frame.Event == "metadata" && root.TryGetProperty("sessionId", out var sid))
                    streamSessionId = sid.GetString();
                else if (frame.Event == "delta" && root.TryGetProperty("delta", out var d) && d.ValueKind == JsonValueKind.String)
                    agentReplyBuilder.Append(d.GetString());
                else if (frame.Event == "done")
                {
                    if (root.TryGetProperty("reply", out var rep) && rep.ValueKind == JsonValueKind.String && agentReplyBuilder.Length == 0)
                        agentReplyBuilder.Append(rep.GetString());
                    if (streamUsage is null && root.TryGetProperty("usage", out _))
                        streamUsage = JsonSerializer.Deserialize<TokenUsageDto>(frame.Data);
                }
            }
            catch { /* ignore frame parse errors */ }
        }

        // ── 持久化（完成后调用，带去重保护） ──
        async Task PersistOnceAsync()
        {
            if (persisted) return;
            persisted = true;
            try
            {
                await PersistMessagesAsync(workspaceId, streamSessionId, req.MessageText,
                    agentReplyBuilder.ToString(), streamUsage, isNewSession, forkParentSessionId, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[Chat] PersistMessages failed ws={Ws}", workspaceId);
            }
        }

        // ── 后台执行：写入临时 Channel，拿到 sessionId 后升级到 EventHub ──
        var tempChannel = Channel.CreateBounded<ServerSentEventFrame>(
            new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropWrite });
        Channel<ServerSentEventFrame>? hubChannel = null;

        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var frame in apiClient.SendMessageStreamAsync(
                    channelId:      channelId,
                    userExternalId: userExternalId,
                    messageText:    req.MessageText,
                    workspaceId:    workspaceId,
                    sessionId:      req.SessionId,
                    llmConfig:      llmConfig,
                    agentTemplateId: agentTemplateId,
                    capabilityPolicy: capabilityPolicy,
                    toolDefinitions: toolDefinitions,
                    skillPackages: skillPackages,
                    forceNewSession: req.ForceNewSession,
                    ct:             CancellationToken.None))
                {
                    ParseFrame(frame);

                    // 拿到 sessionId 后升级：注册到 EventHub，后续订阅此会话的前端可追流
                    if (hubChannel is null && streamSessionId is not null)
                    {
                        hubChannel = eventHub.GetOrCreate(streamSessionId);
                    }

                    // 同时写入临时 Channel 和 EventHub
                    tempChannel.Writer.TryWrite(frame);
                    hubChannel?.Writer.TryWrite(frame);
                }
                // 正常完成
                tempChannel.Writer.Complete();
                if (streamSessionId is not null) eventHub.CompleteAndRemove(streamSessionId);
            }
            catch (OperationCanceledException)
            {
                tempChannel.Writer.Complete();
                if (streamSessionId is not null) eventHub.CompleteAndRemove(streamSessionId);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[Chat] Background execution failed ws={WorkspaceId}", workspaceId);
                tempChannel.Writer.TryComplete(ex);
                if (streamSessionId is not null) eventHub.CompleteAndRemove(streamSessionId);
            }
            finally
            {
                await PersistOnceAsync();
            }
        });

        // ── 前端 SSE 投递循环：当前请求从临时 Channel 读取 ──
        try
        {
            await foreach (var frame in tempChannel.Reader.ReadAllAsync(ct))
            {
                await WriteRawSseAsync(Response, frame, ct);
            }
            await PersistOnceAsync();
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation(
                "[Chat] Client disconnected, background continues ws={WorkspaceId} session={SessionId} partialLen={PartialLen}",
                workspaceId, streamSessionId, agentReplyBuilder.Length);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[Chat] SSE delivery failed ws={WorkspaceId}", workspaceId);
        }

        return new EmptyResult();
    }

    private static void ConfigureSseResponse(HttpResponse response)
    {
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        response.Headers.Connection = "keep-alive";
        response.Headers["X-Accel-Buffering"] = "no";
    }

    private static async Task WriteRawSseAsync(
        HttpResponse response,
        ServerSentEventFrame frame,
        CancellationToken ct)
    {
        await response.WriteAsync($"event: {frame.Event}\n", ct);
        await response.WriteAsync($"data: {frame.Data}\n\n", ct);
        await response.Body.FlushAsync(ct);
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
            .Select(t => new { t.WorkspaceId, t.TemplateId, t.IsEnabled, t.AllowFileWrite, t.AllowShellExecution, t.AllowNetworkAccess, t.AllowedToolNamesJson, t.Role, t.SelectedCapabilityIdsJson })
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

    /// <summary>
    /// 从 Provider 的 ApiKey 字段中解析可安全下发给 Runtime 的 KeyVault 引用。
    /// 支持两种形式：
    /// 1) {{vault:secret-name}} 占位符（优先映射为 KeyVaultId，查不到则退化为 secret-name）；
    /// 2) 直接存储 KeyVaultId。
    /// 其他明文情况返回 null，避免跨服务传输密钥明文。
    /// </summary>
    private async Task<string?> ResolveProviderKeyVaultIdAsync(string? providerApiKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(providerApiKey))
            return null;

        var raw = providerApiKey.Trim();
        var placeholderMatch = VaultPlaceholderRegex.Match(raw);
        if (placeholderMatch.Success)
        {
            var secretName = placeholderMatch.Groups["name"].Value;
            var keyVaultId = await db.KeyVaults.AsNoTracking()
                .Where(k => k.Name == secretName)
                .Select(k => k.KeyVaultId)
                .FirstOrDefaultAsync(ct);

            return string.IsNullOrWhiteSpace(keyVaultId)
                ? secretName
                : keyVaultId;
        }

        var isKeyVaultId = await db.KeyVaults.AsNoTracking()
            .AnyAsync(k => k.KeyVaultId == raw, ct);
        if (isKeyVaultId)
            return raw;

        return null;
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

    /// <summary>
    /// 规范化 Provider 模型 ID。mimo 网关模型名大小写敏感，历史配置中的 "MiMo-V2.5" 需降为 "mimo-v2.5"。
    /// </summary>
    private static string? NormalizePreferredModelId(string providerId, string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            return modelId;

        var trimmed = modelId.Trim();
        if (providerId.Equals("mimo", StringComparison.OrdinalIgnoreCase))
            return trimmed.ToLowerInvariant();

        return trimmed;
    }

    /// <summary>解析 Agent 模板关联的 Skill 包列表，生成 MinIO 预签名下载 URL。</summary>
    private static async Task<IReadOnlyList<SkillPackageInfo>?> ResolveSkillPackagesAsync(
        PlatformDbContext db,
        MinioStorageService minio,
        string? templateId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(templateId))
            return null;

        var (_, globalId) = NormalizeTemplateId(templateId);

        // 先查全局模板
        var globalTemplate = await db.GlobalAgentTemplates.AsNoTracking()
            .FirstOrDefaultAsync(t => t.TemplateId == globalId && t.IsEnabled, ct);
        if (globalTemplate is null)
            return null;

        var selectedIds = ParseStringList(globalTemplate.SelectedSkillPackageIdsJson);
        if (selectedIds.Count == 0)
            return null;

        var packages = await db.SkillPackages.AsNoTracking()
            .Where(s => selectedIds.Contains(s.SkillPackageId) && s.IsEnabled)
            .ToListAsync(ct);

        if (packages.Count == 0)
            return null;

        var result = new List<SkillPackageInfo>(packages.Count);
        foreach (var pkg in packages)
        {
            var url = await minio.GetPresignedDownloadUrlAsync(pkg.ObjectKey, 86400, ct);
            result.Add(new SkillPackageInfo
            {
                SkillPackageId = pkg.SkillPackageId,
                Name           = pkg.Name,
                Description    = pkg.Description,
                Version        = pkg.Version,
                DownloadUrl    = url,
            });
        }
        return result;
    }

    // ── 消息持久化 ─────────────────────────────────────────────────

    /// <summary>
    /// SSE 流结束后将消息写入 ChatMessageEntity（使用独立 scope 避免 DbContext 随请求结束而释放）。
    /// </summary>
    private async Task PersistMessagesAsync(
        string workspaceId,
        string? sessionId,
        string userText,
        string agentReply,
        TokenUsageDto? usage,
        bool isNewSession,
        string? parentSessionId,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(sessionId))
            return;

        using var scope = scopeFactory.CreateScope();
        var persistDb = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        persistDb.ChatMessages.Add(new ChatMessageEntity
        {
            SessionId = sessionId,
            Role = "user",
            Content = userText.Length > 4000 ? userText[..4000] : userText,
            CreatedAt = now - 1,
        });

        if (!string.IsNullOrWhiteSpace(agentReply))
        {
            persistDb.ChatMessages.Add(new ChatMessageEntity
            {
                SessionId = sessionId,
                Role = "agent",
                Content = agentReply,
                UsageJson = usage is not null
                    ? JsonSerializer.Serialize(usage)
                    : null,
                CreatedAt = now,
            });
        }

        await persistDb.SaveChangesAsync(ct);

        try
        {
            await DualWriteToMemoryDbAsync(
                workspaceId,
                sessionId,
                userText,
                agentReply,
                usage,
                isNewSession,
                parentSessionId,
                ct);
        }
        catch (Exception ex)
        {
            // 双写失败不应影响主流程（旧表仍是可回退的读路径）。
            logger.LogWarning(
                ex,
                "[Chat] Dual-write to memory db failed ws={WorkspaceId} session={SessionId}; fallback to legacy chat_messages only.",
                workspaceId,
                sessionId);
        }

        logger.LogInformation(
            "[Chat] Persisted messages session={SessionId} userLen={UserLen} replyLen={ReplyLen}",
            sessionId, userText.Length, agentReply.Length);
    }

    /// <summary>
    /// 双写到 ADR-013 新消息库（Sessions/Messages）。
    /// 说明：
    /// 1) 旧表写入保持不变，双写仅作为增量能力；
    /// 2) V1 暂按线性 MAIN 分支写入，ParentId 仅连接 user -> assistant；
    /// 3) 双写异常在调用方捕获，保证不影响主业务返回。
    /// </summary>
    private async Task DualWriteToMemoryDbAsync(
        string workspaceId,
        string sessionId,
        string userText,
        string agentReply,
        TokenUsageDto? usage,
        bool isNewSession,
        string? parentSessionId,
        CancellationToken ct)
    {
        await using var memoryDb = await memoryDbFactory.CreateDbContextAsync(ct);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await using var tx = await memoryDb.Database.BeginTransactionAsync(ct);

        try
        {

            var session = await memoryDb.Sessions.FindAsync(new object[] { sessionId }, ct);
            if (session is null)
            {
                session = new SessionEntity
                {
                    SessionId = sessionId,
                    WorkspaceId = workspaceId,
                    ParentSessionId = parentSessionId,
                    AgentId = string.Empty,
                    CreatedAt = now,
                    LastActivityAt = now,
                    MessageCount = string.IsNullOrWhiteSpace(agentReply) ? 1 : 2,
                };
                memoryDb.Sessions.Add(session);
            }
            else
            {
                session.LastActivityAt = now;
                session.MessageCount += string.IsNullOrWhiteSpace(agentReply) ? 1 : 2;

                // 若旧数据无 workspaceId，这里尽量回填当前上下文，便于后续按工作区检索。
                if (string.IsNullOrWhiteSpace(session.WorkspaceId) && !string.IsNullOrWhiteSpace(workspaceId))
                    session.WorkspaceId = workspaceId;

                if (!string.IsNullOrWhiteSpace(parentSessionId) && string.IsNullOrWhiteSpace(session.ParentSessionId))
                    session.ParentSessionId = parentSessionId;
            }

            var maxSequence = await memoryDb.Messages
                .Where(m => m.SessionId == sessionId)
                .Select(m => (long?)m.Sequence)
                .MaxAsync(ct) ?? 0;

            // MessageId 采用“时间前缀 + 随机后缀”，便于肉眼排查排序。
            var timestampPrefix = now.ToString("x");
            var userMsgId = $"{timestampPrefix}-{Guid.NewGuid().ToString("N")[..8]}";
            var userContent = userText.Length > 4000 ? userText[..4000] : userText;

            var userEntity = new MessageEntity
            {
                MessageId = userMsgId,
                SessionId = sessionId,
                ParentId = null,
                BranchType = "MAIN",
                Sequence = maxSequence + 1,
                Role = "user",
                ContentType = "text",
                Content = userContent,
                CreatedAt = now - 1,
            };
            memoryDb.Messages.Add(userEntity);
            TryEnqueueJsonl(workspaceId, sessionId, new JsonlEntry
            {
                Type = "user",
                MessageId = userEntity.MessageId,
                SessionId = userEntity.SessionId,
                ParentId = userEntity.ParentId,
                Role = userEntity.Role,
                ContentType = userEntity.ContentType,
                Content = userEntity.Content,
                AgentId = userEntity.AgentId,
                BranchType = userEntity.BranchType,
                CreatedAt = userEntity.CreatedAt,
            });

            if (!string.IsNullOrWhiteSpace(agentReply))
            {
                var agentMsgId = $"{timestampPrefix}-{Guid.NewGuid().ToString("N")[..8]}";
                var assistantEntity = new MessageEntity
                {
                    MessageId = agentMsgId,
                    SessionId = sessionId,
                    ParentId = userMsgId,
                    BranchType = "MAIN",
                    Sequence = maxSequence + 2,
                    Role = "assistant",
                    ContentType = "text",
                    Content = agentReply,
                    UsageJson = usage is not null
                        ? JsonSerializer.Serialize(usage)
                        : null,
                    CreatedAt = now,
                };
                memoryDb.Messages.Add(assistantEntity);
                TryEnqueueJsonl(workspaceId, sessionId, new JsonlEntry
                {
                    Type = "assistant",
                    MessageId = assistantEntity.MessageId,
                    SessionId = assistantEntity.SessionId,
                    ParentId = assistantEntity.ParentId,
                    Role = assistantEntity.Role,
                    ContentType = assistantEntity.ContentType,
                    Content = assistantEntity.Content,
                    UsageJson = assistantEntity.UsageJson,
                    AgentId = assistantEntity.AgentId,
                    BranchType = assistantEntity.BranchType,
                    CreatedAt = assistantEntity.CreatedAt,
                });
            }

            if (isNewSession)
            {
                logger.LogDebug(
                    "[Chat] Dual-write session initialized ws={WorkspaceId} session={SessionId}",
                    workspaceId,
                    sessionId);
            }

            await memoryDb.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    private void TryEnqueueJsonl(string workspaceId, string sessionId, JsonlEntry entry)
    {
        try
        {
            jsonlWriter.Enqueue(sessionId, entry);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "[Chat] JSONL enqueue failed ws={WorkspaceId} session={SessionId} messageId={MessageId}",
                workspaceId,
                sessionId,
                entry.MessageId);
        }
    }

    /// <summary>TokenUsageDto（内联定义，与前端对齐）。</summary>
    private sealed record TokenUsageDto(
        int PromptTokens = 0,
        int CompletionTokens = 0,
        int TotalTokens = 0,
        int ContextWindowTokens = 4096
    );

    /// <summary>
    /// 从模板（工作区优先，全局兜底）解析 ReasoningEffort。
    /// </summary>
    private static async Task<string?> ResolveReasoningEffortAsync(
        PlatformDbContext db,
        string workspaceId,
        string? templateId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(templateId))
            return null;

        var (rawId, globalId) = NormalizeTemplateId(templateId);

        var wsTemplate = await db.WorkspaceAgentTemplates.AsNoTracking()
            .FirstOrDefaultAsync(t => t.WorkspaceId == workspaceId
                                   && (t.TemplateId == rawId || t.TemplateId == globalId)
                                   && t.IsEnabled, ct);
        if (wsTemplate?.ReasoningEffort is not null)
            return wsTemplate.ReasoningEffort;

        var globalTemplate = await db.GlobalAgentTemplates.AsNoTracking()
            .FirstOrDefaultAsync(t => t.TemplateId == globalId && t.IsEnabled, ct);
        return globalTemplate?.ReasoningEffort;
    }
}
