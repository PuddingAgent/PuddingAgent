using Microsoft.Extensions.DependencyInjection;
using PuddingCode.Abstractions;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingMemoryEngine;
using PuddingMemoryEngine.Data;
using PuddingMemoryEngine.Services;
using PuddingRuntime.Services;
using PuddingRuntime.Services.AgentLoop;
using PuddingRuntime.Services.Background;
using System.Threading.Channels;

namespace PuddingRuntime;

/// <summary>
/// PuddingRuntime DI 扩展 — V1 最小注册（无 Docker/Sandbox，直连 LLM）。
/// </summary>
public static class RuntimeServiceExtensions
{
    public static IServiceCollection AddPuddingRuntime(this IServiceCollection services)
    {
        // Session & Agent 管理
        services.AddSingleton<AgentSessionManager>();
        services.AddSingleton<InMemoryRuntimeSessionStore>();

        // 记忆引擎
        services.AddSingleton<SessionMemoryStore>();
        services.AddSingleton<WorkspaceMemoryStore>();
        services.AddSingleton<MemoryBoundaryService>();
        services.AddSingleton<MemoryEngine>();
        services.AddSingleton<IMemoryEngine>(sp => sp.GetRequiredService<MemoryEngine>());
        services.AddSingleton<IMemoryIndexer, TagTreeIndexer>();

        // Agent Loop 护栏与执行控制
        services.AddSingleton<AgentExecutionGuardrails>();
        services.AddSingleton<ExecutionControlRegistry>();
        services.AddSingleton<ExecutionJournal>();
        services.AddSingleton<CompletionPolicy>();

        // Agent Loop Hooks
        services.AddSingleton<IAgentLoopHook, LoggingAgentLoopHook>();

        // 潜意识后台整合基础设施
        var subconsciousChannel = Channel.CreateUnbounded<ConsolidationJob>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
            });
        services.AddSingleton(subconsciousChannel);
        services.AddSingleton<ISubconsciousOrchestrator, SubconsciousOrchestrator>();
        services.AddSingleton<SubconsciousConsolidationHook>();
        services.AddSingleton<IAgentLoopHook>(sp => sp.GetRequiredService<SubconsciousConsolidationHook>());
        services.AddHostedService<SubconsciousWorkerService>();

        // LLM 客户端 — V1 直连（不经过 Controller 中转）
        services.AddSingleton<IRuntimeLlmClient, DirectLlmClient>();
        services.AddSingleton<IMemoryLlmClient, DirectMemoryLlmClient>();
        services.AddSingleton<IEmbeddingService, OpenAiEmbeddingService>();

        // Agent 执行子服务（职责拆分）
        services.AddSingleton<SystemPromptBuilder>();
        services.AddSingleton<ContextPipeline>();
        services.AddSingleton<IContextAssemblyService, ContextAssemblyService>();
        services.AddSingleton<ContextWindowManager>();

        // 会话归档
        services.AddSingleton<SessionArchiver>();

        // Agent 执行服务
        services.AddSingleton<AgentExecutionService>();

        return services;
    }
}
