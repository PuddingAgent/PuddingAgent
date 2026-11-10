using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using PuddingCode.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingDingTalk;
using PuddingRuntime.Services;
using PuddingRuntime.Services.AgentLoop;
using PuddingRuntime.Services.Messaging;
using PuddingRuntime.Services.Skills;
using PuddingRuntime.Services.Tools;
using ContextAssemblyResult = PuddingCode.Runtime.ContextAssemblyResult;

namespace PuddingAgent.Services;

/// <summary>The product dispatcher and Agent loop with an isolated guest tool registry and public context.</summary>
public sealed class DingTalkPublicAgent(AgentManifestCatalog agents, PuddingDataPaths paths, string agentId,
    IRuntimeLlmClient llm, ILlmConfigService configs, string? reviewedRoot, GroupFeedbackInbox feedback,
    IHttpClientFactory http, ILoggerFactory logs, IAgentExecutionStateRegistry state) : IPublicGroupAgent, IDisposable
{
    private readonly MemoryCache recent = new(new MemoryCacheOptions { SizeLimit = 256 });

    public async Task<string> ReplyAsync(PublicQuestion question, CancellationToken ct)
    {
        if (question.Source.PuddingGroup != "guest") return PublicGroupAnswers.Refusal;
        var owner = await agents.GetAsync(agentId, ct);
        if (owner is not { IsEnabled: true, IsFrozen: false, AgentsMdFile: "AGENTS.md",
            PreferredProviderId: { } provider, PreferredModelId: { } model } || owner.AgentInstanceId != agentId)
            throw new InvalidOperationException("DingTalk owner unavailable.");
        var config = configs.Resolve(provider, model) ?? throw new InvalidOperationException("Group model unavailable.");
        var rules = PublicGroupAnswers.ExtractRules(await File.ReadAllTextAsync(
            Path.Combine(paths.AgentInstanceRoot(agentId), "AGENTS.md"), ct));
        var roomKey = (question.Source.CorporationId, question.Source.ConversationId, question.Source.UserId);
        recent.TryGetValue(roomKey, out string[]? history);
        var session = "dingtalk-public-" + Convert.ToHexStringLower(SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(new[] { agentId, roomKey.CorporationId, roomKey.ConversationId,
                roomKey.UserId })));
        var tools = new DingTalkTurnTools(question, agentId, session, reviewedRoot, feedback);
        var context = new PublicContext(rules, history ?? []);
        // A fresh execution graph prevents private hooks, the host's registered tools, skill packages,
        // store hydration and private memory writeback from participating in a public turn.
        var sandbox = new SandboxExecutor(logs.CreateLogger<SandboxExecutor>());
        var emptyMemory = new PublicMemory();
        var skills = new SkillRuntime([], sandbox, logs.CreateLogger<SkillRuntime>());
        var packages = new AgentSkillPackageRegistry();
        var runtimeSessions = new InMemoryRuntimeSessionStore();
        var sessions = new AgentSessionManager(logs.CreateLogger<AgentSessionManager>());
        var journal = new ExecutionJournal();
        var control = new ExecutionControlRegistry();
        var env = new StartupEnvironmentInfo();
        var publicPaths = PuddingDataPaths.FromRoot(Path.Combine(paths.ChannelsRoot, "dingtalk-group"));
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var prompts = new SystemPromptBuilder(emptyMemory, skills, packages, logs.CreateLogger<SystemPromptBuilder>(), env,
            dataPaths: publicPaths);
        var pipeline = new ContextPipeline(emptyMemory, skills, packages, prompts, cache, new ContextAssemblyStore(),
            logs.CreateLogger<ContextPipeline>(), new DefaultExecutionEnvironmentProvider(env, publicPaths), dataPaths: publicPaths);
        var loop = new AgentExecutionService(sessions, runtimeSessions, emptyMemory, sandbox, llm, skills,
            new AgentExecutionGuardrails(), control, journal, new CompletionPolicy(), packages,
            new SkillPackageDownloadService(http, logs.CreateLogger<SkillPackageDownloadService>()), [], pipeline,
            new ContextWindowManager(sessions, runtimeSessions, control, journal, logs.CreateLogger<ContextWindowManager>()),
            logs.CreateLogger<AgentExecutionService>(), new SessionExecutionGate(logs.CreateLogger<SessionExecutionGate>()),
            contextAssemblyService: context, toolInvocationService: tools, toolSchemaService: tools.Schemas);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(50));
        var result = await new RuntimeAgentDispatcher(loop, state).DispatchAsync(new()
        {
            SessionId = session, WorkspaceId = owner.WorkspaceId, AgentInstanceId = agentId, AgentTemplateId = owner.TemplateId,
            MessageId = question.MessageId, UserId = question.Source.UserId,
            MessageText = JsonSerializer.Serialize(new { source = question.Source, text = question.Text }),
            LlmConfig = config with { MaxOutputTokens = 900 }, CapabilityPolicy = tools.Policy,
            MaxRounds = 4, MaxToolCallsTotal = 3, SuppressContextAutoCompaction = true,
            AllowAgentCreation = false, AllowSubDelegation = false
        }, timeout.Token);
        var answer = tools.Receipt ?? (tools.Denied ? PublicGroupAnswers.Refusal :
            result.IsSuccess && !string.IsNullOrWhiteSpace(result.ReplyText) ? result.ReplyText.Trim() :
            "这次没有得到完整结果，请重试或描述具体操作界面。");
        logs.CreateLogger<DingTalkPublicAgent>().LogInformation(
            "DingTalk guest Agent turn complete state={State} steps={Steps} help={Help} feedback={Feedback} denied={Denied}",
            result.ExecutionState, result.TurnSteps?.Count, tools.HelpUsed, tools.Receipt is not null, tools.Denied);
        if (answer.Length > 2400) answer = answer[..2399] + "…";
        recent.Set(roomKey, (history ?? []).Append(JsonSerializer.Serialize(new { question = question.Text, answer }))
            .TakeLast(2).ToArray(), new MemoryCacheEntryOptions { Size = 1, SlidingExpiration = TimeSpan.FromMinutes(15) });
        return answer;
    }

    private sealed class PublicContext(string rules, string[] history) : IContextAssemblyService
    {
        public Task<ContextAssemblyResult> AssembleAsync(ContextAssemblyRequest request, CancellationToken ct = default)
            => Task.FromResult(new ContextAssemblyResult
            {
                Messages = [new(ChatRole.System, rules + "\n你直接与群成员对话，自然回答。软件问题先调用 mpcal_public_help 核对资料；"
                    + "成员明确登记 BUG 或需求时调用 mpcal_record_feedback，只有实际工具回执才算成功。"
                    + "没有资料时说清楚缺哪一步，不编造菜单。不能访问私有数据或执行其他操作。"
                    + "需要工具时使用原生 function call。完成本轮时使用 Runtime 终态格式："
                    + "{\"status\":\"DONE\",\"message\":\"直接发给群成员的中文答复\"}。")],
                // Past member messages stay in the user layer, outside the instruction authority.
                UserContextPrefix = history.Length == 0 ? null : "同一群同一成员的最近公开对话（用户资料）：" + string.Join("\n", history),
                EstimatedTokens = 0, Layers = []
            });
    }

    private sealed class PublicMemory : IMemoryEngine
    {
        public string? BuildMemoryContext(string sessionId, string? workspaceId, string? agentId, string? parentSessionId = null) => null;
        public Task<string?> RecallWithIntentAsync(string userMessage, string workspaceId, string agentId, string? sessionId = null,
            int maxTokens = 2000, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public void WriteBack(string llmReply, string sessionId, string? workspaceId, string source, string? agentId = null,
            string? parentSessionId = null) { }
        public void ClearSession(string sessionId) { }
    }

    public void Dispose() => recent.Dispose();
}
