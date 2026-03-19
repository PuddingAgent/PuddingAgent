using System.Collections.Concurrent;
using PuddingAgent;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingMemoryEngine;

namespace PuddingRuntime.Services;

/// <summary>
/// Agent 执行服务——接收 RuntimeDispatchRequest，创建/复用 Agent 实例，
/// 调用 LLM，返回结果。V1 使用 PuddingCore 已有的 OpenAiLlmGateway。
/// 集成 MemoryEngine（短期/长期记忆注入 + 写回）和 SandboxExecutor（能力门控）。
/// </summary>
public sealed class AgentExecutionService
{
    private readonly AgentSessionManager _sessionManager;
    private readonly InMemoryRuntimeSessionStore _runtimeSessionStore;
    private readonly MemoryEngine _memory;
    private readonly SandboxExecutor _sandbox;
    private readonly IRuntimeLlmClient _llmClient;
    private readonly ILogger<AgentExecutionService> _logger;

    // 每个 Session 的对话历史（内存）
    private readonly ConcurrentDictionary<string, List<ChatMessage>> _histories = new();

    public AgentExecutionService(
        AgentSessionManager sessionManager,
        InMemoryRuntimeSessionStore runtimeSessionStore,
        MemoryEngine memory,
        SandboxExecutor sandbox,
        IRuntimeLlmClient llmClient,
        ILogger<AgentExecutionService> logger)
    {
        _sessionManager = sessionManager;
        _runtimeSessionStore = runtimeSessionStore;
        _memory = memory;
        _sandbox = sandbox;
        _llmClient = llmClient;
        _logger = logger;
    }

    /// <summary>执行 Agent 逻辑——接收消息、调 LLM、返回回复。</summary>
    public async Task<RuntimeDispatchResult> ExecuteAsync(RuntimeDispatchRequest request, CancellationToken ct = default)
    {
        _logger.LogInformation("[AgentExec] session={SessionId} template={Template} msg length={Len}",
            request.SessionId, request.AgentTemplateId, request.MessageText.Length);

        try
        {
            // 1. 获取/创建 Agent 实例
            var instance = _sessionManager.GetOrCreate(request.SessionId, request.AgentTemplateId);

            // 2. 登记/更新 RuntimeSessionStore 热状态
            _runtimeSessionStore.GetOrCreate(
                request.SessionId,
                instance.AgentInstanceId,
                request.WorkspaceId,
                request.AgentTemplateId);

            // 3. 获取 Agent 模板
            var template = BuiltInAgentTemplates.FindById(request.AgentTemplateId)
                           ?? BuiltInAgentTemplates.WorkspaceServiceAgent;

            // 4. 构建对话历史（首轮注入系统提示 + 记忆上下文）
            var history = _histories.GetOrAdd(request.SessionId, _ => []);
            if (history.Count == 0)
            {
                var systemContent = BuildSystemPrompt(template, request.SessionId, request.WorkspaceId);
                history.Add(new ChatMessage(ChatRole.System, systemContent));
            }
            else if (template.Memory?.EnableSessionMemory == true || template.Memory?.EnableWorkspaceMemory == true)
            {
                // 非首轮：刷新系统提示中的记忆片段（保持记忆上下文最新）
                var systemContent = BuildSystemPrompt(template, request.SessionId, request.WorkspaceId);
                if (history[0].Role == ChatRole.System)
                    history[0] = new ChatMessage(ChatRole.System, systemContent);
            }

            history.Add(new ChatMessage(ChatRole.User, request.MessageText));

            // 5. 调用 LLM
            var response = await _llmClient.ChatAsync(
                request.WorkspaceId,
                request.SessionId,
                request.AgentTemplateId,
                history,
                ct);

            // 6. 提取回复
            var replyText = response.Content ?? "(no response)";
            history.Add(new ChatMessage(ChatRole.Assistant, replyText));

            // 7. 记忆写回——从 LLM 回复中解析 REMEMBER[tag] 标记
            if (template.Memory?.EnableSessionMemory == true || template.Memory?.EnableWorkspaceMemory == true)
            {
                _memory.WriteBack(replyText, request.SessionId, request.WorkspaceId, instance.AgentInstanceId);
            }

            // 8. 裁剪历史以避免超 token（简单策略：保留 system + 最近 N 条）
            TrimHistory(history, template.Runtime?.MaxContextTokens ?? 8192);

            _sessionManager.Touch(request.SessionId);
            _runtimeSessionStore.Touch(request.SessionId);

            _logger.LogInformation("[AgentExec] session={SessionId} reply length={Len}",
                request.SessionId, replyText.Length);

            return new RuntimeDispatchResult
            {
                SessionId = request.SessionId,
                AgentInstanceId = instance.AgentInstanceId,
                ReplyText = replyText,
                IsSuccess = true,
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[AgentExec] Failed for session={SessionId}", request.SessionId);
            return new RuntimeDispatchResult
            {
                SessionId = request.SessionId,
                AgentInstanceId = "",
                IsSuccess = false,
                ErrorMessage = ex.Message,
            };
        }
    }

    /// <summary>构建注入了记忆上下文的系统提示。</summary>
    private string BuildSystemPrompt(AgentTemplateDefinition template, string sessionId, string? workspaceId)
    {
        var basePrompt = template.SystemPrompt ?? "You are a helpful assistant.";

        // 如果记忆策略关闭，直接返回基础提示
        if (template.Memory is null
            || (!template.Memory.EnableSessionMemory && !template.Memory.EnableWorkspaceMemory))
            return basePrompt;

        var memoryContext = _memory.BuildMemoryContext(sessionId, workspaceId);
        if (memoryContext is null) return basePrompt;

        return basePrompt + "\n\n---\n" + memoryContext;
    }

    private static void TrimHistory(List<ChatMessage> history, int maxTokenBudget)
    {
        // 简化策略：保留 system 消息 + 最近 40 条（约 8k token budget）
        const int maxMessages = 40;
        if (history.Count <= maxMessages + 1) return;

        var system = history.FirstOrDefault(m => m.Role == ChatRole.System);
        var recent = history.TakeLast(maxMessages).ToList();

        history.Clear();
        if (system is not null) history.Add(system);
        history.AddRange(recent);
    }
}

