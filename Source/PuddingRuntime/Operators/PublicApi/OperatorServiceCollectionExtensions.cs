using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pudding.ToolPlugins.Contracts;
using PuddingOperatorAccess;
using PuddingOperatorAccess.Contracts;
using PuddingRuntime.Services.Improvement.SkillValue;

namespace PuddingRuntime.Operators.PublicApi;

public static class OperatorServiceCollectionExtensions
{
    public static IServiceCollection AddPublicOperators(this IServiceCollection services)
    {
        // Caller-provided statistics are not pushed into approval auditing or authoritative host telemetry.
        services.TryAddSingleton<SkillValueAccessAdapter>(sp => new(new SkillValueScorer(new OperatorEnvironment { Clock = sp.GetService<TimeProvider>() })));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IOperatorBackend, SkillValueAccessAdapter>(sp => sp.GetRequiredService<SkillValueAccessAdapter>()));
        services.TryAddSingleton<OperatorAccessService>();
        services.TryAddSingleton<IPluginOperatorApiLeaseFactory, PluginOperatorApiLeaseFactory>();
        services.TryAddSingleton<PuddingExecutionResources.Contracts.IModelResourcePool, PuddingExecutionResources.ModelResourcePool>();
        services.TryAddSingleton<PuddingModelEvaluation.ModelEvaluationService>();
        services.TryAddSingleton<IExecutionModelBinder, ConfiguredExecutionModelBinder>();
        services.TryAddSingleton<ModelEvaluationApiFactory>();
        return services;
    }
}
