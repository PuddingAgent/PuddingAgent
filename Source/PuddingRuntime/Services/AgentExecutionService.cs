using System.Collections.Concurrent;
using PuddingAgent;
using PuddingCode.Abstractions;
using PuddingCode.Core;
using PuddingCode.Models;
using PuddingCode.Platform;

namespace PuddingRuntime.Services;

/// <summary>
/// Agent 执行服务——接收 RuntimeDispatchRequest，创建/复用 Agent 实例，
/// 调用 LLM，返回结果。V1 使用 PuddingCore 已有的 OpenAiLlmGateway。
/// </summary>
public sealed class AgentExecutionService
{
    private readonly AgentSessionManager _sessionManager;
    private readonly ILogger<AgentExecutionService> _logger;
    private readonly IConfiguration _configuration;

    // 每个 Session 的对话历史（内存）
    private readonly ConcurrentDictionary<string, List<ChatMessage>> _histories = new();

    public AgentExecutionService(
        AgentSessionManager sessionManager,
        ILogger<AgentExecutionService> logger,
        IConfiguration configuration)
    {
        _sessionManager = sessionManager;
        _logger = logger;
        _configuration = configuration;
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

            // 2. 获取 Agent 模板
            var template = BuiltInAgentTemplates.FindById(request.AgentTemplateId)
                           ?? BuiltInAgentTemplates.WorkspaceServiceAgent;

            // 3. 构建对话历史
            var history = _histories.GetOrAdd(request.SessionId, _ => []);
            if (history.Count == 0 && !string.IsNullOrEmpty(template.SystemPrompt))
            {
                history.Add(new ChatMessage(ChatRole.System, template.SystemPrompt));
            }
            history.Add(new ChatMessage(ChatRole.User, request.MessageText));

            // 4. 调用 LLM
            var llm = CreateLlmGateway();
            var response = await llm.ChatAsync(history, [], ct);

            // 5. 提取回复
            var replyText = response.Content ?? "(no response)";
            history.Add(new ChatMessage(ChatRole.Assistant, replyText));

            // 6. 裁剪历史以避免超 token（简单策略：保留 system + 最近 N 条）
            TrimHistory(history, template.Runtime.MaxContextTokens);

            _sessionManager.Touch(request.SessionId);

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

    private ILlmGateway CreateLlmGateway()
    {
        var endpoint = _configuration["Pudding:LlmEndpoint"] ?? "https://api.openai.com";
        var apiKey = _configuration["Pudding:LlmApiKey"] ?? "";
        var model = _configuration["Pudding:LlmModel"] ?? "gpt-4o-mini";

        var options = new LlmOptions(endpoint, apiKey, model);

        return new OpenAiLlmGateway(new HttpClient(), options);
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
