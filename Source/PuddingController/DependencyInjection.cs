using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PuddingCode.Platform;
using PuddingController.Services;
using PuddingGateway;

namespace PuddingController;

/// <summary>
/// PuddingController DI 扩展 — V1 最小注册（InMemory，无 PostgreSQL/Redis）。
/// </summary>
public static class ControllerServiceExtensions
{
    public static IServiceCollection AddPuddingController(this IServiceCollection services)
    {
        // Gateway（V1 最小：无实际出站适配器）
        services.AddSingleton(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<GatewayAdapterHost>>();
            return new GatewayAdapterHost(
                onEventReceived: (_, _) => Task.CompletedTask,
                log: msg => logger.LogInformation("{Msg}", msg));
        });
        services.AddSingleton<GatewayEgressService>();

        // Workspace & Session
        services.AddSingleton<InMemoryWorkspaceCatalog>();
        services.AddSingleton<InMemorySessionRepository>();

        // 审计 & 路由
        services.AddSingleton<InMemoryAuditEventStore>();
        services.AddSingleton<InMemoryRouteDecisionStore>();
        services.AddSingleton<AuthorizationService>();
        services.AddSingleton<AgentTemplateRegistry>();

        // Runtime 调度
        services.AddSingleton<RuntimeRegistryService>();
        services.AddSingleton<RuntimeDispatcher>();

        // Session 路由（核心）
        services.AddSingleton<SessionRouter>();

        return services;
    }
}
