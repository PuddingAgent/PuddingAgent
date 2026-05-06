using Microsoft.Extensions.DependencyInjection;
using PuddingCode.Abstractions;
using PuddingMemoryEngine;
using PuddingMemoryEngine.Data;
using PuddingRuntime.Services;
using PuddingRuntime.Services.AgentLoop;

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

        // LLM 客户端 — V1 直连（不经过 Controller 中转）
        services.AddSingleton<IRuntimeLlmClient, DirectLlmClient>();
        services.AddSingleton<IMemoryLlmClient, DirectMemoryLlmClient>();

        // Agent 执行子服务（职责拆分）
        services.AddSingleton<SystemPromptBuilder>();
        services.AddSingleton<ContextWindowManager>();

        // Agent 执行服务
        services.AddSingleton<AgentExecutionService>();

        return services;
    }
}
