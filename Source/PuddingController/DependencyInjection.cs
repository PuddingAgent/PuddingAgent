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

        // 审批（进程内实现）。此前从未在这里注册，而 InMemoryApprovalService 又硬依赖
        // 同样未注册的 IConnectionMultiplexer ⇒ /api/approval/* 四个端点在已认证请求下
        // 恒 500（死接口）。现在实现已改为名副其实的进程内存储，注册即生效。
        services.AddSingleton<InMemoryApprovalService>();

        return services;
    }
}
