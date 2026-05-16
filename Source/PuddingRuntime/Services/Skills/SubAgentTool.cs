using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingRuntime.Services;

namespace PuddingRuntime.Services.Skills;

/// <summary>
/// SubAgentTool — 主 Agent 派生子代理执行任务的 Skill。
/// 
/// 设计原则：
///   · 复用 AgentExecutionService — 子代理与主代理使用同一执行引擎，不另起炉灶
///   · 权限继承 — 子代继承父代理的能力策略，父代理可下调（不可升级）
///   · 工具继承 — 默认继承父代理的工具集，可指定子集
///   · 模型继承 — 通过 ILlmResolver 从 DB 注册表解析 LLM 配置
///   · 同步模式 — 父代理等待子代理完成，结果注入父代理上下文
///   · 异步模式 — 父代理继续执行，子代理完成后通过事件系统回调通知
///   · 参数校验 — 无效模板名返回可用列表，不让 LLM 盲猜
///   · 延迟解析 — 使用 IServiceProvider 避免 AgentExecutionService 的 DI 死锁
/// 
/// 实现 ITool（LLM function calling）和 IAgentSkill（SkillRuntime）双接口。
/// 对应 Claude Code AgentTool / SendMessageTool 的子代理模式。
/// </summary>
public sealed class SubAgentTool : ITool, IAgentSkill
{
    private readonly IServiceProvider _services;
    private readonly ILogger<SubAgentTool> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string SkillId => "spawn_sub_agent";
    public string Name => "spawn_sub_agent";
    public string Description =>
        "派生子代理执行独立任务。子代理拥有独立的上下文窗口，看不到主代理的对话历史。" +
        "参数：task（任务描述）、agent_template（可选，默认 workspace-task-agent）、" +
        "model（可选，如 mimo/mimo-v2.5-pro 或 deepseek/deepseek-v3，不指定则用平台默认模型）、" +
        "sync（可选，true=同步阻塞等待结果 / false=异步立即返回，默认 true）。" +
        "异步模式下立即返回 agentId，稍后通过 agent.sub_completed 事件通知结果。" +
        "provider 格式为 {providerId}/{modelId}，平台已在 LLM 资源池注册模型。";
    public bool RequiresShellExecution => false;
    public ToolPermissionLevel PermissionLevel => ToolPermissionLevel.Low;

    /// <summary>ITool：LLM function calling 参数 schema。</summary>
    public ToolParameterSchema Parameters => new(
        [
            new("task", "string", "子代理要执行的任务描述（必填）"),
            new("agent_template", "string", "Agent 模板 ID，默认 workspace-task-agent。可选：workspace-service-agent, workspace-task-agent, code-agent, workspace-audit-agent"),
            new("model", "string", "LLM 模型，格式 {providerId}/{modelId}，如 mimo/mimo-v2.5-pro。不指定则用平台默认"),
            new("sync", "boolean", "同步模式：true=等待子代理完成（默认），false=异步执行立即返回"),
            new("tools", "string", "允许子代理使用的工具子集，逗号分隔。如 'bash,file_read'。默认继承全部"),
        ],
        ["task"]);

    public SubAgentTool(
        IServiceProvider services,
        ILogger<SubAgentTool> logger)
    {
        _services = services;
        _logger = logger;
    }

    public async Task<SkillResult> ExecuteAsync(SkillInvokeRequest request, CancellationToken ct = default)
    {
        var executionService = _services.GetRequiredService<AgentExecutionService>();
        var eventBus = _services.GetRequiredService<IInternalEventBus>();
        var streamingBus = _services.GetService<IStreamingEventBus>();
        var ssm = _services.GetService<PuddingCode.Abstractions.ISessionStateManager>(); // ADR-016

        var json = TryParseJson(request.Input);

        var task = GetStringProp(json, "task")
                ?? GetStringProp(json, "prompt")
                ?? request.Parameters.GetValueOrDefault("task")
                ?? request.Input?.Trim();

        if (string.IsNullOrWhiteSpace(task))
            return Fail("参数 'task' 是必需的。请提供子代理要执行的任务描述。");

        var templateId = GetStringProp(json, "agent_template")
                      ?? GetStringProp(json, "template")
                      ?? request.Parameters.GetValueOrDefault("template")
                      ?? "workspace-task-agent";

        var isSync = GetBoolProp(json, "sync")
                  ?? (request.Parameters.TryGetValue("sync", out var syncVal)
                        && bool.TryParse(syncVal, out var syncBool) && syncBool);

        // 没有显式指定 sync → 默认同步
        if (!HasProp(json, "sync") && !request.Parameters.ContainsKey("sync"))
            isSync = true;

        var modelId = GetStringProp(json, "model")
                   ?? request.Parameters.GetValueOrDefault("model");

        // 确定子代理模板
        var template = ResolveTemplate(templateId);
        if (template == null)
        {
            var available = string.Join(", ", BuiltInAgentTemplates.GetAll().Select(t => t.TemplateId));
            return Fail($"未知的 Agent 模板 '{templateId}'。可用模板：{available}");
        }

        // 构造子代理的 Capability（继承父代理，可下调不可升级）
        var childCapability = BuildChildCapability(json, request, template);

        // 构造 LlmConfig
        var childLlmConfig = await BuildChildLlmConfigAsync(modelId, request);

        _logger.LogInformation(
            "[SubAgent] Spawning sync={Sync} template={Template} model={Model} session={Session}",
            isSync, template.TemplateId, childLlmConfig?.ModelId ?? "inherit", request.SessionId);

        var childRequest = new RuntimeDispatchRequest
        {
            SessionId = $"{request.SessionId}-sub-{Guid.NewGuid().ToString("N")[..8]}",
            WorkspaceId = request.WorkspaceId,
            AgentTemplateId = template.TemplateId,
            MessageText = task,
            CapabilityPolicy = childCapability,
            LlmConfig = childLlmConfig,
            MaxRounds = 10, // 子代理限制轮数，避免 LLM 错误时无限重试导致 429
        };

        if (isSync)
        {
            var replyBuilder = new System.Text.StringBuilder();
            var logs = new List<string>();
            string? currentThinking = null;

            // 获取父代理 SessionEventHub channel（反射解决 PuddingRuntime→PuddingPlatform 项目引用问题）
            object? hubChannel = null;
            try
            {
                var hubType = Type.GetType("PuddingPlatform.Services.SessionEventHub, PuddingPlatform");
                if (hubType is not null)
                {
                    var hub = _services.GetService(hubType);
                    if (hub is not null)
                        hubChannel = hubType.GetMethod("GetOrCreate")?.Invoke(hub, [request.SessionId]);
                }
            }
            catch { }

            await foreach (var frame in executionService.ExecuteStreamAsync(childRequest, ct))
            {
                // 子代理帧 → 父代理 SessionEventHub（SSE 前端可见）
                var subFrame = new ServerSentEventFrame($"subagent.{frame.Event}", frame.Data);
                try { (hubChannel as dynamic)?.Writer?.TryWrite(subFrame); } catch { }

                // 同时也发布到 StreamingEventBus（DevPanel 等消费）
                if (streamingBus is not null)
                {
                    try { await streamingBus.EmitAsync(new StreamingEvent { Type = $"subagent.{frame.Event}", Data = frame.Data }, ct); }
                    catch { }
                }

                switch (frame.Event)
                {
                    case "delta":
                        try { using var doc = JsonDocument.Parse(frame.Data); if (doc.RootElement.TryGetProperty("delta", out var d)) replyBuilder.Append(d.GetString()); }
                        catch { }
                        break;
                    case "thinking":
                        try { using var doc = JsonDocument.Parse(frame.Data); if (doc.RootElement.TryGetProperty("delta", out var td)) currentThinking = td.GetString(); }
                        catch { }
                        break;
                    case "tool_call":
                        try { using var doc = JsonDocument.Parse(frame.Data); var name = doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() : "?"; logs.Add($"🔧 {name}"); }
                        catch { logs.Add("🔧 tool_call"); }
                        currentThinking = null;
                        break;
                    case "tool_result":
                        try { using var doc = JsonDocument.Parse(frame.Data); var name = doc.RootElement.TryGetProperty("name", out var tn) ? tn.GetString() : "?"; var ok = doc.RootElement.TryGetProperty("exitCode", out var ec) && ec.GetInt32() == 0; logs.Add(ok ? $"  ✅ {name}" : $"  ❌ {name}"); }
                        catch { logs.Add("  ◌ tool_result"); }
                        currentThinking = null;
                        break;
                    case "step":
                        if (!string.IsNullOrWhiteSpace(currentThinking)) { logs.Add($"💭 {TruncateForLog(currentThinking, 80)}"); currentThinking = null; }
                        break;
                }
            }

            // 组合输出
            var header = $"🤖 **子代理** | 模板 `{template.TemplateId}` | 模型 `{childLlmConfig?.ModelId ?? "默认"}` | {logs.Count} 步\n\n";
            var logSection = logs.Count > 0 ? $"### 执行日志\n" + string.Join("\n", logs) + "\n\n" : "";
            var replySection = replyBuilder.Length > 0 ? $"### 回复\n{replyBuilder}" : "*(未生成文本回复)*";

            var output = header + logSection + replySection;

            if (replyBuilder.Length > 0)
                return new SkillResult { Success = true, Output = output.Trim(), ExitCode = 0 };
            else
                return new SkillResult { Success = false, Output = output.Trim(), Error = "子代理未生成文本回复", ExitCode = 1 };
        }

        // ADR-016：追踪异步子代理创建
        var spawnedAt = DateTimeOffset.UtcNow;
        if (ssm is not null)
        {
            _logger.LogDebug("[Diag] SubAgent spawning async parent={Parent} sub={Sub} template={Template} model={Model} task={Task}",
                request.SessionId, childRequest.SessionId, template.TemplateId, childLlmConfig?.ModelId ?? "default",
                task!.Length > 200 ? task[..200] + "..." : task);

            await ssm.TrackSubAgentStartAsync(request.SessionId, new PuddingCode.Abstractions.SubAgentSpawnInfo
            {
                SubSessionId = childRequest.SessionId,
                ParentSessionId = request.SessionId,
                ParentAgentId = request.AgentInstanceId,
                TemplateId = template.TemplateId,
                ModelId = childLlmConfig?.ModelId,
                TaskSummary = task!.Length > 200 ? task[..200] + "..." : task,
                SpawnedAt = spawnedAt,
            }, CancellationToken.None);

            // 向父会话事件日志推送 SubAgentSpawned 帧
            var spawnedFrame = ServerSentEventFrame.Json(SessionEventTypes.SubAgentSpawned, new
            {
                sub_agent_id = childRequest.SessionId,
                template = template.TemplateId,
                model = childLlmConfig?.ModelId ?? "默认",
                task_summary = task!.Length > 200 ? task[..200] + "..." : task,
            });
            _ = ssm.AppendAsync(request.SessionId, request.WorkspaceId ?? "", spawnedFrame, CancellationToken.None);
        }

        // 异步：fire-and-forget，完成后通过事件通知
        _ = Task.Run(async () =>
        {
            try
            {
                // 诊断日志：子代理开始执行
                if (ssm is not null)
                {
                    _ = ssm.AppendAsync(childRequest.SessionId, request.WorkspaceId ?? "",
                        ServerSentEventFrame.Json(SessionEventTypes.Metadata, new
                        {
                            sessionId = childRequest.SessionId,
                            parentSessionId = request.SessionId,
                            status = "execution_started",
                        }), CancellationToken.None);
                }

                var result = await executionService.ExecuteAsync(childRequest, CancellationToken.None);

                // ADR-016：追踪子代理完成 + 向父会话推送 SubAgentCompleted 帧
                var completedAt = DateTimeOffset.UtcNow;
                _logger.LogDebug("[Diag] SubAgent completed async parent={Parent} sub={Sub} success={Success} replyLen={Len} elapsed={Elapsed}ms",
                    request.SessionId, childRequest.SessionId, result.IsSuccess,
                    result.ReplyText?.Length ?? 0, (completedAt - spawnedAt).TotalMilliseconds);

                if (ssm is not null)
                {
                    _ = ssm.TrackSubAgentCompleteAsync(childRequest.SessionId, new PuddingCode.Abstractions.SubAgentResult
                    {
                        Success = result.IsSuccess,
                        Reply = result.ReplyText,
                        Error = result.ErrorMessage,
                        Usage = result.Usage,
                        CompletedAt = completedAt,
                    }, CancellationToken.None);

                    _ = ssm.AppendAsync(request.SessionId, request.WorkspaceId ?? "",
                        ServerSentEventFrame.Json(SessionEventTypes.SubAgentCompleted, new
                        {
                            sub_agent_id = childRequest.SessionId,
                            success = result.IsSuccess,
                            reply = result.ReplyText,
                            error = result.ErrorMessage,
                        }), CancellationToken.None);
                }

                // 发布事件通知父代理
                await eventBus.PublishAsync(new InternalEvent
                {
                    Type = "agent.sub_completed",
                    Priority = EventPriorityLevel.Normal,
                    Source = new EventSource { SourceType = "subagent", SourceId = childRequest.SessionId },
                    WorkspaceId = request.WorkspaceId,
                    SessionId = request.SessionId, // 父代理 session
                    AgentId = request.AgentInstanceId,
                    Payload = new
                    {
                        sub_agent_id = childRequest.SessionId,
                        success = result.IsSuccess,
                        reply = result.ReplyText,
                        error = result.ErrorMessage,
                    },
                    Metadata = new Dictionary<string, string>
                    {
                        ["parent_session"] = request.SessionId,
                        ["parent_agent"] = request.AgentInstanceId,
                    },
                }, CancellationToken.None);

                _logger.LogInformation(
                    "[SubAgent] Async completed sub={SubAgent} parent={Parent} success={Success}",
                    childRequest.SessionId, request.SessionId, result.IsSuccess);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "[SubAgent] Async failed sub={SubAgent} parent={Parent}",
                    childRequest.SessionId, request.SessionId);

                // 诊断日志：子代理执行异常
                if (ssm is not null)
                {
                    _ = ssm.AppendAsync(request.SessionId, request.WorkspaceId ?? "",
                        ServerSentEventFrame.Json(SessionEventTypes.SubAgentCompleted, new
                        {
                            sub_agent_id = childRequest.SessionId,
                            success = false,
                            error = ex.Message,
                        }), CancellationToken.None);
                }
            }
        }, CancellationToken.None);

        _logger.LogInformation(
            "[SubAgent] Async spawned sub={SubAgent} parent={Parent}",
            childRequest.SessionId, request.SessionId);

        return Success(
            $"异步子代理已创建。sub_agent_id = {childRequest.SessionId}。" +
            $"完成后将通过 'agent.sub_completed' 事件通知。",
            new { sub_agent_id = childRequest.SessionId, async = true, status = "running" });
    }

    /// <summary>
    /// ITool 执行入口 — LLM function calling 路径。
    /// 将 JSON 参数反序列化为 SkillInvokeRequest，委托给 IAgentSkill.ExecuteAsync。
    /// </summary>
    async Task<string> ITool.ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var root = doc.RootElement;

            var task = root.TryGetProperty("task", out var t) ? t.GetString() : null;
            if (string.IsNullOrWhiteSpace(task))
                return JsonSerializer.Serialize(new { status = "error", message = "参数 'task' 是必需的" });

            var templateId = root.TryGetProperty("agent_template", out var at) ? at.GetString() : null;
            var sync = root.TryGetProperty("sync", out var s) && s.GetBoolean();
            var modelId = root.TryGetProperty("model", out var m) ? m.GetString() : null;
            var toolsStr = root.TryGetProperty("tools", out var tl) ? tl.GetString() : null;

            var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["task"] = task!,
            };
            if (!string.IsNullOrWhiteSpace(templateId)) parameters["template"] = templateId;
            if (!string.IsNullOrWhiteSpace(modelId)) parameters["model"] = modelId;
            if (!string.IsNullOrWhiteSpace(toolsStr)) parameters["tools"] = toolsStr;
            parameters["sync"] = sync.ToString().ToLowerInvariant();

            var skillRequest = new SkillInvokeRequest
            {
                AgentInstanceId = "llm-fn-call",
                WorkspaceId = "default",
                SessionId = $"fncall-{Guid.NewGuid():N}",
                Input = JsonSerializer.Serialize(new { task, agent_template = templateId, sync, model = modelId, tools = toolsStr }),
                Parameters = parameters,
            };

            var result = await ExecuteAsync(skillRequest, ct);
            return result.Success ? result.Output : JsonSerializer.Serialize(new { status = "error", message = result.Error });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SubAgent] ITool.ExecuteAsync failed");
            return JsonSerializer.Serialize(new { status = "error", message = ex.Message });
        }
    }

    // ── 私有辅助 ──────────────────────────────────────────────────────

    private static JsonObject? TryParseJson(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        try { return JsonNode.Parse(input)?.AsObject(); }
        catch { return null; }
    }

    private static string? GetStringProp(JsonObject? obj, string name)
    {
        if (obj == null) return null;
        return obj.TryGetPropertyValue(name, out var node) ? node?.GetValue<string>() : null;
    }

    private static bool? GetBoolProp(JsonObject? obj, string name)
    {
        if (obj == null) return null;
        if (!obj.TryGetPropertyValue(name, out var node) || node == null) return null;
        if (node.GetValueKind() == JsonValueKind.True || node.GetValueKind() == JsonValueKind.False)
            return node.GetValue<bool>();
        if (bool.TryParse(node.GetValue<string>(), out var b))
            return b;
        return null;
    }

    private static bool HasProp(JsonObject? obj, string name)
    {
        if (obj == null) return false;
        return obj.TryGetPropertyValue(name, out _);
    }

    /// <summary>解析模板 ID，支持精确匹配 + 模糊回退。</summary>
    private static AgentTemplateDefinition? ResolveTemplate(string templateId)
    {
        var exact = BuiltInAgentTemplates.FindById(templateId);
        if (exact != null) return exact;

        // 模糊匹配：尝试加/去 "workspace-" 前缀
        if (!templateId.StartsWith("workspace-", StringComparison.OrdinalIgnoreCase))
        {
            exact = BuiltInAgentTemplates.FindById($"workspace-{templateId}");
            if (exact != null) return exact;
        }
        if (templateId.StartsWith("workspace-", StringComparison.OrdinalIgnoreCase))
        {
            exact = BuiltInAgentTemplates.FindById(templateId["workspace-".Length..]);
            if (exact != null) return exact;
        }

        // 兜底：包含匹配
        return BuiltInAgentTemplates.GetAll()
            .FirstOrDefault(t => t.TemplateId.Contains(templateId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 子代理能力：继承父代理策略，可下调不可升级。
    /// 父代理可通过参数指定 AllowedToolNames 子集。
    /// </summary>
    private static CapabilityPolicy BuildChildCapability(
        JsonObject? json, SkillInvokeRequest request, AgentTemplateDefinition template)
    {
        var basePolicy = template.Capability ?? new CapabilityPolicy();

        // 允许调用的工具子集
        var toolsJson = GetStringProp(json, "tools");
        var toolsParam = request.Parameters.GetValueOrDefault("tools");
        var toolsStr = toolsJson ?? toolsParam;

        if (!string.IsNullOrWhiteSpace(toolsStr))
        {
            var allowedTools = toolsStr.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Trim())
                .Where(t => t.Length > 0)
                .ToList();

            return basePolicy with { AllowedToolNames = allowedTools };
        }

        return basePolicy;
    }

    /// <summary>
    /// 构造子代理的 LLM 配置。验证模型有效性，无效则返回可用列表提示。
    /// </summary>
    /// <summary>
    /// 通过 ILlmResolver 从 DB 注册表解析 LLM 配置。
    /// modelId 格式支持 "provider/model"（如 "deepseek/deepseek-v3"）或纯 modelId。
    /// 不指定时使用平台默认 provider。
    /// </summary>
    private async Task<LlmConfig> BuildChildLlmConfigAsync(string? modelId, SkillInvokeRequest request)
    {
        var resolver = _services.GetRequiredService<ILlmResolver>();

        if (!string.IsNullOrWhiteSpace(modelId) && modelId.Contains('/'))
        {
            // provider/model 格式
            var parts = modelId.Split('/', 2);
            var cfg = await resolver.ResolveAsync(parts[0], parts[1]);
            if (cfg is not null) return cfg;
            _logger.LogWarning("[SubAgent] Provider/model '{ModelId}' not resolved, fallback to default", modelId);
        }

        if (!string.IsNullOrWhiteSpace(modelId))
        {
            // 尝试所有 provider 匹配此 model
            var providerIds = await resolver.ListEnabledProviderIdsAsync();
            foreach (var pid in providerIds)
            {
                var cfg = await resolver.ResolveAsync(pid, modelId);
                if (cfg is not null) return cfg;
            }
            _logger.LogWarning("[SubAgent] Model '{ModelId}' not found in any provider, fallback to default", modelId);
        }

        // 兜底：平台默认
        return await resolver.ResolveDefaultAsync()
            ?? throw new InvalidOperationException("No LLM provider configured. Please add a provider in LLM资源池.");
    }

    private static string TruncateForLog(string text, int maxLen)
        => text.Length <= maxLen ? text : text[..(maxLen - 3)] + "...";

    private static SkillResult Success(string message, object? detail = null)
    {
        var output = detail is not null
            ? JsonSerializer.Serialize(new { summary = message, detail }, JsonOpts)
            : message;
        return new SkillResult { Success = true, Output = output, ExitCode = 0 };
    }

    private static SkillResult Fail(string error) =>
        new() { Success = false, Output = "", Error = error, ExitCode = 1 };
}
