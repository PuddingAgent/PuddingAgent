using Microsoft.Extensions.Configuration;
using PuddingCode.Abstractions;
using PuddingCode.Runtime;
using PuddingModels;
using PuddingPlatform.Services.AgentWork;
using PuddingPlatform.Services.ExecutionBudgets;
using PuddingPlatform.Services.Models;

namespace PuddingAgent.Services;

/// <summary>
/// CORE-I1-05 composition: managed single-turn Work budget binding. Kept in its own partial file so the frozen
/// platform registration file (contract-config freezed) does not need to change; disabled by default preserves legacy
/// behavior for every existing entry point.
/// </summary>
public static partial class PuddingServiceCollectionExtensions
{
    public static void AddManagedWorkBudgetBinding(WebApplicationBuilder builder)
    {
        var section = builder.Configuration.GetSection("Pudding:ExecutionBudgets:ManagedWork");
        var options = new ManagedWorkBudgetOptions
        {
            Enabled = section.GetValue<bool>("Enabled"),
            Mode = section.GetValue<string>("Mode") ?? "Enforced",
            CostUnit = section.GetValue<string>("CostUnit") ?? "",
            MaxOutputTokens = section.GetValue<long?>("MaxOutputTokens"),
            MaxCost = section.GetValue<decimal?>("MaxCost"),
            MaxCalls = section.GetValue<int?>("MaxCalls"),
            MaxConcurrentAttempts = section.GetValue<int?>("MaxConcurrentAttempts") ?? 2,
            MaxInputTokensPerRequest = section.GetValue<long?>("MaxInputTokensPerRequest"),
        };
        builder.Services.AddSingleton<IManagedWorkBudgetPolicyProvider>(
            new ConfigurationManagedWorkBudgetPolicyProvider(options));
        // Product composition must provide the account mapper as well; the resolver resolves it for every Coordinator
        // construction (even while the policy is disabled), so a missing registration breaks ordinary chat too.
        builder.Services.AddSingleton<ExecutionBudgetAccountMapper>();
        builder.Services.AddScoped<IManagedWorkBudgetBindingResolver, ManagedWorkBudgetBindingResolver>();
        // CORE-I1-06: managed sends take a real per-model concurrency slot from the host lease generation.
        builder.Services.AddSingleton<IModelAttemptConcurrencyPort>(sp => new ModelLeaseConcurrencyPort(
            sp.GetRequiredService<ModelLeaseRegistry>(),
            sp.GetRequiredService<ILlmConfigService>()));
    }
}
