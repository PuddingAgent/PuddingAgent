using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Flurl.Http.Configuration;
using PuddingCode.Abstractions;
using PuddingCode.Classification;
using PuddingCode.Configuration;
using PuddingCode.Runtime;
using PuddingCode.Tools;
using PuddingRuntime.Classification;
using PuddingRuntime.Services;
using PuddingRuntime.Services.Plugins;
using PuddingRuntime.Services.Search;
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
        services.TryAddSingleton<ISearchAttemptLedger, SearchAttemptLedger>();
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
            var workspaceToolSources = sp.GetServices<IWorkspacePuddingToolSource>().ToList();

            return new PuddingToolRegistry(
                nativeTools,
                sp.GetRequiredService<IToolPermissionPolicyService>(),
                toolSources: toolSources,
                workspaceToolSources: workspaceToolSources);
        });

        services.TryAddSingleton<IToolPermissionPolicyService, ToolPermissionPolicyService>();
        // Agent 级访问级别（用户 2026-09-19）：权限跟随 Agent 主体。
        // 作为可选依赖注入 PuddingToolExecutionService，未注册的主机退化为纯全局模式。
        services.TryAddSingleton<IAgentAccessLevelService, AgentAccessLevelService>();
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
        // ADR-091 §4.4/F01：审查依赖缺席（含未注册 ILlmInvocationService / 日志）必须产生 typed
        // 依赖等待，不能让 DI 在解析 reviewer 时直接崩溃。
        services.TryAddSingleton<IToolApprovalLlmClient>(sp => new InvocationToolApprovalLlmClient(
            sp.GetService<PuddingCode.Runtime.ILlmInvocationService>(),
            sp.GetRequiredService<IToolApprovalLlmProfileResolver>(),
            sp.GetService<ILogger<InvocationToolApprovalLlmClient>>()));
        // S3c-1（安全分类器方案 v2 §14.13）：组装分类器管线并注册为 IToolCallClassifier 单例。
        // 仲裁位：Jev 决策端口已注册 ⇒ JevToolCallClassifier；未注册 ⇒ fail-closed 占位（返回 Unknown）。
        // 两种形态都不会让 DI 解析或启动抛异常；管线自带仲裁独立 3000ms 超时。
        services.TryAddSingleton<IToolCallClassifier>(sp => BuildToolCallClassifierPipeline(sp, configuration));
        services.TryAddSingleton<IToolApprovalReviewer>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<ToolApprovalRuntimeOptions>>().Value;
            // 未配置时的默认选择（Jev自动审批方案 §3.2）：Jev 决策端口已注册 ⇒ jev；
            // 否则保留旧行为 llm。这是组合期的一次性确定选择，不是运行期跨模型静默回退。
            var reviewer = string.IsNullOrWhiteSpace(options.Reviewer)
                ? sp.GetService<IJevDecisionService>() is not null
                    ? ToolApprovalRuntimeOptions.JevReviewer
                    : ToolApprovalRuntimeOptions.LlmReviewer
                : options.Reviewer.Trim();

            if (string.Equals(reviewer, ToolApprovalRuntimeOptions.LlmReviewer, StringComparison.OrdinalIgnoreCase))
                return ActivatorUtilities.CreateInstance<LlmToolApprovalReviewer>(sp);

            if (string.Equals(reviewer, ToolApprovalRuntimeOptions.JevReviewer, StringComparison.OrdinalIgnoreCase))
                return ActivatorUtilities.CreateInstance<JevToolApprovalReviewer>(sp);

            if (string.Equals(reviewer, ToolApprovalRuntimeOptions.ClassifierReviewer, StringComparison.OrdinalIgnoreCase))
            {
                // S3c-1：分类器链路（IToolCallClassifier 管线 + 审计存储）已在上方注册，
                // 仅当显式配置 Reviewer=classifier 时选中；默认路径仍是 llm，不在此翻转。
                return ActivatorUtilities.CreateInstance<ClassifierToolApprovalReviewer>(sp);
            }

            // ADR-091 §5/F02：生产注册没有 fake 放行路径；即使旧配置传 Reviewer=fake 也必须拒绝。
            // 假实现只能在测试组合里通过显式 DI 注册。
            throw new InvalidOperationException(
                $"ToolApproval reviewer '{options.Reviewer}' is not supported. The production registration only supports 'llm', 'jev', or 'classifier'; inject test doubles through test DI.");
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

    /// <summary>
    /// 组装分类器管线（S3c-1，方案 v2 §14.13）：<see cref="SystemRuleClassifier"/>（规则快路径）
    /// + 仲裁位（Jev 端口已注册 ⇒ <see cref="JevToolCallClassifier"/>，否则 fail-closed 占位）
    /// + 审计存储 ⇒ <see cref="ToolCallClassifierPipeline"/>。
    /// 仲裁调用有独立 3000ms 超时（管线默认 <see cref="ToolCallClassifierPipelineOptions.DefaultArbiterTimeoutMs"/>）。
    /// Jev 端口未注册时不抛异常：仲裁退化为返回 Unknown 的占位（fail-closed，由管线转 deferred 语义）。
    /// </summary>
    private static IToolCallClassifier BuildToolCallClassifierPipeline(
        IServiceProvider serviceProvider,
        IConfiguration? configuration)
    {
        var ruleClassifier = new SystemRuleClassifier(
            serviceProvider.GetRequiredService<IToolApprovalAllowlistStore>(),
            serviceProvider.GetService<TimeProvider>());

        var jevDecisionService = serviceProvider.GetService<IJevDecisionService>();
        IToolCallClassifier arbiter = jevDecisionService is null
            ? UnregisteredJevArbiterClassifier.Instance
            : new JevToolCallClassifier(
                jevDecisionService,
                configuration?.GetSection(ToolApprovalJevOptions.SectionName).Get<ToolApprovalJevOptions>()
                    ?? new ToolApprovalJevOptions(),
                serviceProvider.GetService<TimeProvider>());

        return new ToolCallClassifierPipeline(
            [ruleClassifier],
            arbiter,
            serviceProvider.GetRequiredService<IToolApprovalAuditStore>(),
            serviceProvider.GetService<TimeProvider>());
    }

    /// <summary>
    /// Jev 决策端口未注册时的仲裁占位（S3c-1）：fail-closed 返回 <see cref="ClassificationOutcome.Unknown"/>，
    /// 由管线按 §14.13.2 场景③④转 <c>classifier.pipeline.arbiter_unavailable</c>（上层再按 §14.7 转 deferred）；
    /// 绝不放行、绝不折叠为 Deny。
    /// </summary>
    internal sealed class UnregisteredJevArbiterClassifier : IToolCallClassifier
    {
        public static readonly UnregisteredJevArbiterClassifier Instance = new();

        private UnregisteredJevArbiterClassifier()
        {
        }

        /// <summary>分类器稳定标识（审计溯源）。</summary>
        public string ClassifierId => "arbiter.not-registered";

        public Task<ClassificationVerdict> ClassifyAsync(
            ToolCallClassificationContext context,
            CancellationToken ct = default)
            => Task.FromResult(new ClassificationVerdict
            {
                Outcome = ClassificationOutcome.Unknown,
                Reason = "IJevDecisionService is not registered in this container; the classification arbiter is unavailable (fail-closed).",
                ReasonCode = "classifier.arbiter.not_registered",
                ClassifierId = ClassifierId,
                ClassifierModel = null,
                AppliedRuleId = null,
            });
    }
}
