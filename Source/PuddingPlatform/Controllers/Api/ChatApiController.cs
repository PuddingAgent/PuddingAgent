using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PuddingCode.Abstractions;
using PuddingCode.Platform;
using PuddingCode.Models;
using PuddingCode.Observability;
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
    ChatTranscriptWriter transcriptWriter,
    IDbContextFactory<MemoryDbContext> memoryDbFactory,
    JsonlSessionWriter jsonlWriter,
    ISessionStateManager ssm,
    IRuntimeTraceAccessor traceAccessor,
    ILogger<ChatApiController> logger) : ControllerBase
{
    private static readonly Regex VaultPlaceholderRegex =
        new(@"^\{\{vault:(?<name>[a-zA-Z0-9._-]+)\}\}$", RegexOptions.Compiled);

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    // POST /api/workspaces/{workspaceId}/chat/message
    // T-102: 改为 fire-and-forget — 立即返回 { messageId, sessionId }，不再等待完整执行结果。
    // 所有流式帧通过 SessionEventsController 的持久 SSE 通道（SSM/EventHub）推送给前端。
    [HttpPost("message")]
    public async Task<IActionResult> SendMessage(
        string workspaceId, [FromBody] AdminChatRequest req, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var trace = RuntimeTraceContext.CreateNew(
            sessionId: req.SessionId,
            workspaceId: workspaceId,
            userId: User.Identity?.Name ?? "admin");
        traceAccessor.Current = trace;

        logger.LogInformation(
            "[Chat] REQUEST trace={TraceId} ws={WorkspaceId} agentId={AgentId} msgLen={MsgLen}",
            trace.TraceId, workspaceId, req.AgentId ?? "(none)", req.MessageText?.Length ?? 0);

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

        // T-102: 通过流式接口获取第一个 metadata 帧提取 sessionId/messageId，
        // 然后 fire-and-forget 将剩余帧写入 SSM (EventHub)，立即返回 IDs 给前端。
        string? streamSessionId = req.SessionId;
        string? streamMessageId = null;
        var framesWritten = 0;
        var userCreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // 启动后台执行 — 获取第一个 metadata 帧后返回，剩余帧推入 SSM
        _ = Task.Run(async () =>
        {
            var replyBuilder = new StringBuilder();
            var thinkingChunks = new List<TranscriptThinkingChunk>();
            string? latestUsageJson = null;
            var userTranscriptPersisted = false;
            var assistantTranscriptPersisted = false;

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
                    // 提取 metadata 帧中的 IDs
                    if (frame.Event == "metadata")
                    {
                        try
                        {
                            using var doc = JsonDocument.Parse(frame.Data);
                            var root = doc.RootElement;
                            if (root.TryGetProperty("sessionId", out var sid))
                                streamSessionId = sid.GetString();
                            if (root.TryGetProperty("messageId", out var mid))
                                streamMessageId = mid.GetString();
                        }
                        catch { /* ignore parse errors */ }
                    }

                    // ADR-031: ChatMessages 是面向 UI/检索的聊天转录物化视图。
                    // 用户消息在确认 sessionId 后写入；执行事实仍由 session_event_log 记录。
                    if (!userTranscriptPersisted && streamSessionId is not null)
                    {
                        await transcriptWriter.PersistMessageAsync(
                            streamSessionId,
                            role: "user",
                            content: req.MessageText,
                            createdAt: userCreatedAt,
                            thinkingJson: null,
                            usageJson: null,
                                CancellationToken.None);
                        userTranscriptPersisted = true;
                    }

                    if (frame.Event == "delta")
                    {
                        var delta = TryReadStringProperty(frame.Data, "delta");
                        if (!string.IsNullOrEmpty(delta))
                            replyBuilder.Append(delta);
                    }
                    else if (frame.Event == "thinking")
                    {
                        var delta = TryReadStringProperty(frame.Data, "delta");
                        if (!string.IsNullOrEmpty(delta))
                        {
                            thinkingChunks.Add(new TranscriptThinkingChunk(
                                delta,
                                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
                        }
                    }
                    else if (frame.Event == "usage")
                    {
                        latestUsageJson = TryReadUsageJson(frame.Data) ?? latestUsageJson;
                    }

                    // 写入 SSM EventHub
                    if (streamSessionId is not null)
                    {
                        var frameTrace = trace.WithSession(streamSessionId, workspaceId);
                        await ssm.AppendAsync(
                            streamSessionId,
                            workspaceId,
                            frame,
                            CancellationToken.None,
                            frameTrace,
                            RuntimeActivityComponents.AgentExecution,
                            $"chat.stream.{frame.Event}");
                        framesWritten++;
                    }

                    // T-CACHE-005: 检测 done 帧，fire-and-forget 更新 TokenUsageStats
                    if (frame.Event == "done" && !string.IsNullOrEmpty(frame.Data))
                    {
                        if (!assistantTranscriptPersisted && streamSessionId is not null)
                        {
                            var reply = TryReadStringProperty(frame.Data, "reply");
                            var assistantContent = !string.IsNullOrWhiteSpace(reply)
                                ? reply
                                : replyBuilder.ToString();
                            var doneUsageJson = TryReadUsageJson(frame.Data) ?? latestUsageJson;
                            var thinkingJson = thinkingChunks.Count > 0
                                ? JsonSerializer.Serialize(thinkingChunks, JsonOpts)
                                : null;

                            await transcriptWriter.PersistMessageAsync(
                                streamSessionId,
                                role: "agent",
                                content: assistantContent,
                                createdAt: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                                thinkingJson,
                                doneUsageJson,
                                CancellationToken.None);
                            assistantTranscriptPersisted = true;
                        }

                        var capturedProviderId = agent?.PreferredProviderId;
                        var capturedModelId = llmConfig?.ModelId;
                        var capturedData = frame.Data;
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                using var doc = JsonDocument.Parse(capturedData);
                                var root = doc.RootElement;
                                if (!root.TryGetProperty("usage", out var usageEl)) return;
                                
                                var usageTokens = JsonSerializer.Deserialize<TokenUsageDto>(usageEl.GetRawText(), JsonOpts);
                                if (usageTokens is null) return;

                                var yearMonth = DateTimeOffset.UtcNow.ToString("yyyy-MM");
                                var providerId = capturedProviderId ?? "unknown";
                                var modelId = capturedModelId ?? "unknown";

                                using var statsScope = scopeFactory.CreateScope();
                                var statsDb = statsScope.ServiceProvider.GetRequiredService<PlatformDbContext>();

                                // 查询价格配置用于成本计算
                                var priceConfig = await statsDb.LlmModels.AsNoTracking()
                                    .Where(m => m.ModelId == modelId)
                                    .Select(m => new { m.InputPricePer1MTokens, m.OutputPricePer1MTokens, m.CacheHitPricePer1MTokens })
                                    .FirstOrDefaultAsync();

                                var inputPrice = priceConfig?.InputPricePer1MTokens ?? 0m;
                                var outputPrice = priceConfig?.OutputPricePer1MTokens ?? 0m;
                                var cacheHitPrice = priceConfig?.CacheHitPricePer1MTokens ?? inputPrice;

                                // 计算费用：cacheHit * cacheHitPrice + cacheMiss * inputPrice + completion * outputPrice
                                var hitTokens = (decimal)(usageTokens.PromptCacheHitTokens ?? 0);
                                var missTokens = (decimal)(usageTokens.PromptCacheMissTokens ?? 0);
                                var completionTokens = (decimal)(usageTokens.CompletionTokens ?? 0);
                                var promptTokens = (decimal)(usageTokens.PromptTokens ?? 0);
                                var actualMiss = missTokens > 0 ? missTokens : (hitTokens > 0 ? promptTokens - hitTokens : promptTokens);
                                var cost = (hitTokens / 1_000_000m * cacheHitPrice)
                                         + (actualMiss / 1_000_000m * inputPrice)
                                         + (completionTokens / 1_000_000m * outputPrice);

                                // UPSERT：SQLite 不支持 ON CONFLICT，先查再插/更
                                var existing = await statsDb.TokenUsageStats
                                    .FirstOrDefaultAsync(s => s.YearMonth == yearMonth
                                        && s.ProviderId == providerId
                                        && s.ModelId == modelId);

                                if (existing is not null)
                                {
                                    existing.PromptTokens += (long)(usageTokens.PromptTokens ?? 0);
                                    existing.CompletionTokens += (long)(usageTokens.CompletionTokens ?? 0);
                                    existing.CacheHitTokens += (long)(usageTokens.PromptCacheHitTokens ?? 0);
                                    existing.CacheMissTokens += (long)(usageTokens.PromptCacheMissTokens ?? 0);
                                    existing.RequestCount++;
                                    existing.TotalCost += cost;
                                    existing.UpdatedAt = DateTimeOffset.UtcNow;
                                }
                                else
                                {
                                    statsDb.TokenUsageStats.Add(new TokenUsageStatsEntity
                                    {
                                        ProviderId = providerId,
                                        ModelId = modelId,
                                        YearMonth = yearMonth,
                                        PromptTokens = (long)(usageTokens.PromptTokens ?? 0),
                                        CompletionTokens = (long)(usageTokens.CompletionTokens ?? 0),
                                        CacheHitTokens = (long)(usageTokens.PromptCacheHitTokens ?? 0),
                                        CacheMissTokens = (long)(usageTokens.PromptCacheMissTokens ?? 0),
                                        RequestCount = 1,
                                        TotalCost = cost,
                                        UpdatedAt = DateTimeOffset.UtcNow,
                                    });
                                }
                                await statsDb.SaveChangesAsync();
                            }
                            catch (Exception ex)
                            {
                                var statsLogger = scopeFactory.CreateScope().ServiceProvider
                                    .GetRequiredService<ILogger<ChatApiController>>();
                                statsLogger.LogWarning(ex, "[Chat:Stats] Failed to update TokenUsageStats");
                            }
                        });
                    }
                }

                logger.LogInformation(
                    "[Chat:FireAndForget] Stream completed ws={Workspace} session={Session} framesWritten={Frames}",
                    workspaceId, streamSessionId, framesWritten);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "[Chat:FireAndForget] Background stream failed ws={Workspace} session={Session}",
                    workspaceId, streamSessionId);
            }
        });

        // 等待首个 metadata 帧到达（带超时）
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            while (streamMessageId is null && streamSessionId is null)
            {
                await Task.Delay(100, linkedCts.Token);
                // streamSessionId 可能从 request 中已有
                if (req.SessionId is not null && streamSessionId is null)
                    streamSessionId = req.SessionId;
            }

            // 再等一等 messageId（metadata 帧可能延迟）
            var waitStart = System.Diagnostics.Stopwatch.StartNew();
            while (streamMessageId is null && waitStart.ElapsedMilliseconds < 3000)
            {
                await Task.Delay(100, linkedCts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning(
                "[Chat:FireAndForget] Timeout waiting for metadata ws={Workspace} session={Session}",
                workspaceId, streamSessionId);
        }

        sw.Stop();

        // P0-2 修复: metadata 帧获取失败时返回 500，防止前端 loading 永久悬挂
        if (streamMessageId is null || streamSessionId is null)
        {
            logger.LogError(
                "[Chat:FireAndForget] Failed to get metadata ws={Workspace} msgId={MessageId} session={Session} elapsed={Elapsed}ms",
                workspaceId, streamMessageId, streamSessionId, sw.ElapsedMilliseconds);
            return StatusCode(500, new { message = "AI 服务响应超时，请稍后重试" });
        }

        logger.LogInformation(
            "[Chat:FireAndForget] Returned ws={Workspace} msgId={MessageId} sessionId={SessionId} elapsed={Elapsed}ms",
            workspaceId, streamMessageId, streamSessionId, sw.ElapsedMilliseconds);

        return Ok(new { messageId = streamMessageId, sessionId = streamSessionId });
    }

    private sealed record TranscriptThinkingChunk(string Text, long Timestamp);

    private static string? TryReadStringProperty(string json, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryReadUsageJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                return usage.GetRawText();

            return LooksLikeUsagePayload(root)
                ? root.GetRawText()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool LooksLikeUsagePayload(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return false;

        return root.TryGetProperty("promptTokens", out _)
            || root.TryGetProperty("PromptTokens", out _)
            || root.TryGetProperty("completionTokens", out _)
            || root.TryGetProperty("CompletionTokens", out _)
            || root.TryGetProperty("totalTokens", out _)
            || root.TryGetProperty("TotalTokens", out _);
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
                    DefaultToolNames = ["file_read", "memory_library", "grep_memory",
                        "query_sessions", "http_fetch", "search_files", "search_codebase",
                        "spawn_sub_agent", "task_manager"],
                    RequiresGrantToolNames = ["bash", "python", "file_write"],
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

        if (isTaskRole && tools.Count == 0)
            tools.UnionWith(["bash", "file_read", "file_write"]);

        // V2 两级权限：从 selectedCapabilities 拆分 DefaultToolNames / RequiresGrantToolNames
        var defaultTools = new List<string>();
        var grantTools = new List<string>();

        foreach (var cap in selectedCapabilities ?? [])
        {
            if (string.IsNullOrWhiteSpace(cap.ToolName)) continue;
            var toolName = cap.ToolName.Trim();

            if (cap.RequiresShellExecution || cap.RequiresFileWrite)
                grantTools.Add(toolName);
            else
                defaultTools.Add(toolName);
        }

        return new CapabilityPolicy
        {
            AllowFileWrite = allowFileWrite || selectedAllowWrite || isTaskRole,
            AllowShellExecution = allowShellExecution || selectedAllowShell || isTaskRole,
            AllowNetworkAccess = allowNetworkAccess || selectedAllowNetwork,
            AllowedToolNames = tools.ToList(),
            DefaultToolNames = defaultTools,
            RequiresGrantToolNames = grantTools,
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
