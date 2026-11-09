using Microsoft.Extensions.DependencyInjection.Extensions;
using PuddingAgentWork.Contracts;

namespace PuddingPlatform.Services.AgentWork;

public static class AgentWorkServiceCollectionExtensions
{
    public static IServiceCollection AddAgentWorkApi(this IServiceCollection services)
    {
        services.TryAddSingleton<IAgentWorkBackend, CanonicalAgentWorkBackend>();
        services.TryAddScoped<IAgentWorkTargetResolver, AgentWorkTargetResolver>();
        services.TryAddSingleton<AgentWorkApiFactory>();
        return services;
    }
}
