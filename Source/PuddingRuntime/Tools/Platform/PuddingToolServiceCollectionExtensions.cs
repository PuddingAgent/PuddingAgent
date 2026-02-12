using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Flurl.Http.Configuration;
using PuddingCode.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Runtime;
using PuddingCode.Tools;
using PuddingRuntime.Services;
using PuddingRuntime.Services.Plugins;
using PuddingRuntime.Services.Skills;
using PuddingRuntime.Services.Tools.Handlers;
using System.Reflection;

namespace PuddingRuntime.Services.Tools;

/// <summary>注册统一 Tool 基础设施。</summary>
public static class PuddingToolServiceCollectionExtensions
{
    /// <summary>
    /// 注册 Tool registry/catalog/schema 服务。
    /// Agent 可见工具必须显式注册为 IPuddingTool；旧 IAgentSkill 不再被隐式纳入注册表。
    /// </summary>
    public static IServiceCollection AddPuddingToolRegistry(
        this IServiceCollection services,
        IConfiguration? configuration = null)
    {
        services.TryAddSingleton<IEverythingSdk, EverythingSdk>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IFileSearchProvider, BuiltInRecursiveFileSearchProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IFileSearchProvider, EverythingSearchProvider>());
        services.TryAddSingleton<ITerminalProcessManager>(NoOpTerminalProcessManager.Instance);
        services.TryAddSingleton<IFlurlClientCache, FlurlClientCache>();
        services.TryAddSingleton<IWebClient, FlurlWebClient>();
        services.TryAddSingleton<IHtmlToMarkdownConverter, ReverseMarkdownHtmlToMarkdownConverter>();
        services.TryAddSingleton<IHttpFetchContentFormatter, HttpFetchContentFormatter>();
        services.TryAddSingleton<BookHandler>();
        services.TryAddSingleton<ChapterHandler>();
        services.TryAddSingleton<ReferenceHandler>();
        services.TryAddSingleton<GraphHandler>();
        services.TryAddSingleton<DedupHandler>();
        services.TryAddSingleton<AgentSkillFileService>();
        services.TryAddSingleton<AuditLogger>();
        services.TryAddSingleton<PluginDiagnosticsSink>();
        services.TryAddSingleton<PluginDiagnosticsReader>();
        services.TryAddSingleton<PluginManifestCatalog>();
        services.TryAddSingleton<PluginPackageInstaller>();
        if (!services.Any(d => d.ServiceType == typeof(IPuddingToolSource)
                               && d.ImplementationFactory is not null))
        {
            // TryAddEnumerable 只能用具体实现类型去重；factory 注册会被 DI 视为
            // “接口实现接口”，启动时无法区分多个 IPuddingToolSource。插件工具源需要
            // 按运行环境选择文件目录或空源，所以这里显式做一次服务级别去重。
            services.AddSingleton<IPuddingToolSource>(sp =>
                sp.GetService<PuddingDataPaths>() is null
                    ? new EmptyPuddingToolSource("plugins")
                    : sp.GetRequiredService<PluginManifestCatalog>());
        }

        services.TryAddSingleton<IPuddingToolRegistry>(sp =>
        {
            var nativeTools = sp.GetServices<IPuddingTool>().ToList();
            var toolSources = sp.GetServices<IPuddingToolSource>().ToList();

            return new PuddingToolRegistry(
                nativeTools,
                sp.GetRequiredService<IToolPermissionPolicyService>(),
                toolSources: toolSources);
        });

        services.TryAddSingleton<IToolPermissionPolicyService, ToolPermissionPolicyService>();
        services.TryAddSingleton<IAgentFirewall>(sp => new AgentFirewall(
            runtime: sp.GetService<IRuntimeControlService>(),
            policySvc: sp.GetService<IToolPermissionPolicyService>(),
            toolRegistry: sp.GetService<IPuddingToolRegistry>(),
            authzSvc: sp.GetService<IToolAuthorizationService>(),
            approvalSvc: sp.GetService<IToolApprovalService>(),
            availabilityProvider: sp.GetService<IAgentExecutionAvailabilityProvider>(),
            logger: sp.GetService<ILogger<AgentFirewall>>()));
        services.TryAddSingleton<IPuddingToolCatalogService, PuddingToolCatalogService>();
        services.TryAddSingleton<PuddingToolSchemaService>();
        services.TryAddSingleton<IToolAuthorizationService, InMemoryToolAuthorizationService>();
        if (configuration is not null)
        {
            services.Configure<ToolApprovalRuntimeOptions>(
                configuration.GetSection(ToolApprovalRuntimeOptions.SectionName));
            services.Configure<ToolApprovalLlmOptions>(
                configuration.GetSection($"{ToolApprovalRuntimeOptions.SectionName}:Llm"));
        }
        else
        {
            services.AddOptions<ToolApprovalRuntimeOptions>();
            services.AddOptions<ToolApprovalLlmOptions>();
        }

        services.TryAddSingleton<IToolApprovalLlmProfileResolver, StrictConfiguredToolApprovalLlmProfileResolver>();
        services.TryAddSingleton<IToolApprovalLlmClient, InvocationToolApprovalLlmClient>();
        services.TryAddSingleton<IToolApprovalReviewer>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<ToolApprovalRuntimeOptions>>().Value;
            if (options.RequireAuditAgent)
                return ActivatorUtilities.CreateInstance<LlmToolApprovalReviewer>(sp);

            var reviewer = string.IsNullOrWhiteSpace(options.Reviewer)
                ? ToolApprovalRuntimeOptions.FakeReviewer
                : options.Reviewer.Trim();

            if (string.Equals(reviewer, ToolApprovalRuntimeOptions.FakeReviewer, StringComparison.OrdinalIgnoreCase))
                return new FakeToolApprovalReviewer();
            if (string.Equals(reviewer, ToolApprovalRuntimeOptions.LlmReviewer, StringComparison.OrdinalIgnoreCase))
                return ActivatorUtilities.CreateInstance<LlmToolApprovalReviewer>(sp);

            throw new InvalidOperationException(
                $"Unknown ToolApproval reviewer '{options.Reviewer}'. Valid values are 'fake' and 'llm'.");
        });
        services.TryAddSingleton<IToolApprovalTicketStore>(sp =>
            sp.GetService<PuddingDataPaths>() is null
                ? new InMemoryToolApprovalTicketStore()
                : ActivatorUtilities.CreateInstance<FileToolApprovalTicketStore>(sp));
        services.TryAddSingleton<IToolApprovalAllowlistStore>(sp =>
            sp.GetService<PuddingDataPaths>() is null
                ? new InMemoryToolApprovalAllowlistStore()
                : ActivatorUtilities.CreateInstance<FileToolApprovalAllowlistStore>(sp));
        services.TryAddSingleton<IToolApprovalAuditStore>(sp =>
            sp.GetService<PuddingDataPaths>() is null
                ? new InMemoryToolApprovalAuditStore()
                : ActivatorUtilities.CreateInstance<FileToolApprovalAuditStore>(sp));
        services.TryAddSingleton<IToolApprovalService, InMemoryToolApprovalService>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IPuddingTool, RequestToolApprovalTool>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IPuddingTool, ListToolApprovalsTool>());
        services.TryAddSingleton<IPuddingToolExecutionService, PuddingToolExecutionService>();

        return services;
    }

    /// <summary>注册一个原生 Pudding Tool，并自动纳入统一注册表。</summary>
    public static IServiceCollection AddPuddingTool<TTool>(this IServiceCollection services)
        where TTool : class, IPuddingTool
    {
        services.TryAddSingleton<TTool>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IPuddingTool, TTool>());
        return services;
    }

    /// <summary>
    /// 注册一个 Agent 可调用工具。原生 IPuddingTool 由 assembly scan 纳入 registry；
    /// 尚未迁移完成的 IAgentSkill 通过显式 adapter 过渡，避免 registry 隐式依赖旧接口。
    /// </summary>
    public static IServiceCollection AddPuddingAgentTool<TTool>(this IServiceCollection services)
        where TTool : class
    {
        if (typeof(TTool) == typeof(SendMessageTool))
        {
            services.TryAddSingleton<SendMessageTool>(sp =>
                new SendMessageTool(sp.GetRequiredService<IServiceScopeFactory>()));
        }
        else if (typeof(TTool) == typeof(ReceiveMessagesTool))
        {
            services.TryAddSingleton<ReceiveMessagesTool>(sp =>
                new ReceiveMessagesTool(sp.GetRequiredService<IServiceScopeFactory>()));
        }
        else
        {
            services.TryAddSingleton<TTool>();
        }

        if (typeof(IPuddingTool).IsAssignableFrom(typeof(TTool)))
        {
            if (!HasPuddingToolRegistration(services, typeof(TTool)))
            {
                services.AddSingleton<IPuddingTool>(sp =>
                    (IPuddingTool)sp.GetRequiredService<TTool>());
                services.AddSingleton(new PuddingToolRegistrationMarker(typeof(TTool)));
            }

            return services;
        }

        if (!typeof(IAgentSkill).IsAssignableFrom(typeof(TTool)))
        {
            throw new InvalidOperationException(
                $"Tool type '{typeof(TTool).FullName}' must implement IPuddingTool or IAgentSkill.");
        }

        RegisterAdaptedAgentSkillTool<TTool>(services);
        return services;
    }

    /// <summary>
    /// 从程序集自动发现并注册带 <see cref="ToolAttribute"/> 的原生 Pudding Tool。
    /// 用于让新增 Tool 只关注自身实现，不需要修改分发、schema 或执行服务。
    /// </summary>
    public static IServiceCollection AddPuddingToolsFromAssembly(
        this IServiceCollection services,
        Assembly assembly)
    {
        foreach (var toolType in assembly.GetTypes()
                     .Where(t => !t.IsAbstract
                                 && typeof(IPuddingTool).IsAssignableFrom(t)
                                 && t.GetCustomAttribute<ToolAttribute>() is not null))
        {
            if (services.Any(d => d.ServiceType == toolType))
            {
                if (!HasPuddingToolRegistration(services, toolType))
                {
                    services.AddSingleton(typeof(IPuddingTool), sp =>
                        (IPuddingTool)sp.GetRequiredService(toolType));
                    services.AddSingleton(new PuddingToolRegistrationMarker(toolType));
                }

                continue;
            }

            services.TryAddEnumerable(ServiceDescriptor.Singleton(typeof(IPuddingTool), toolType));
        }

        return services;
    }

    private static bool HasPuddingToolRegistration(IServiceCollection services, Type toolType) =>
        services.Any(d => d.ServiceType == typeof(IPuddingTool)
                          && (d.ImplementationType == toolType
                              || d.ImplementationInstance?.GetType() == toolType))
        || services.Any(d => d.ServiceType == typeof(PuddingToolRegistrationMarker)
                             && d.ImplementationInstance is PuddingToolRegistrationMarker marker
                             && marker.ToolType == toolType);

    private static void RegisterAdaptedAgentSkillTool<TTool>(IServiceCollection services)
        where TTool : class
    {
        if (services.Any(d => d.ServiceType == typeof(PuddingAgentToolRegistrationMarker)
                              && d.ImplementationInstance is PuddingAgentToolRegistrationMarker marker
                              && marker.ToolType == typeof(TTool)))
        {
            return;
        }

        services.AddSingleton<IPuddingTool>(sp =>
            new AgentSkillToolAdapter((IAgentSkill)sp.GetRequiredService<TTool>()));
        services.AddSingleton(new PuddingAgentToolRegistrationMarker(typeof(TTool)));
    }

    private sealed record PuddingAgentToolRegistrationMarker(Type ToolType);

    private sealed record PuddingToolRegistrationMarker(Type ToolType);

    private sealed class EmptyPuddingToolSource(string sourceId) : IPuddingToolSource
    {
        public string SourceId { get; } = sourceId;
        public IReadOnlyList<IPuddingTool> ListTools() => [];
    }
}
