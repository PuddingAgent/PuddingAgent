using System.Collections.Immutable;
using PuddingCode.Runtime;
using PuddingExecutionResources.Contracts;

namespace PuddingPlatform.Services.ExecutionBudgets;

/// <summary>Host configuration for managed single-turn Work budgets. Disabled by default: legacy paths stay legacy.</summary>
public sealed record ManagedWorkBudgetOptions
{
    public bool Enabled { get; init; }
    /// <summary>Only Enforced/Shadow are valid for managed work; Legacy is a policy error, never a silent pass.</summary>
    public string Mode { get; init; } = "Enforced";
    /// <summary>Account currency; must match the reviewed model catalog currency of the execution model.</summary>
    public string CostUnit { get; init; } = "";
    public long? MaxOutputTokens { get; init; }
    public decimal? MaxCost { get; init; }
    public int? MaxCalls { get; init; }
    public int MaxConcurrentAttempts { get; init; } = 2;
    /// <summary>Per-request input capacity. When omitted, the reviewed model context window is used.</summary>
    public long? MaxInputTokensPerRequest { get; init; }
}

public static class ManagedWorkBudgetErrorCodes
{
    public const string PolicyInvalid = "work.budget_policy_invalid";
    public const string EvidenceMissing = "work.budget_evidence_missing";
    public const string IdentityMismatch = "work.budget_identity_mismatch";
    public const string ModelUnpriced = "work.budget_model_unpriced";
}

/// <summary>A managed Work binding could not be produced. Callers must not treat this as an unmanaged result.</summary>
public sealed class ManagedWorkBudgetException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>The frozen result of resolving a command lease into a managed Work budget binding.</summary>
public sealed record ManagedWorkBudgetBinding(
    RuntimeWorkBudgetBinding Runtime,
    string WorkId,
    BudgetAccountId AccountId,
    ManagedWorkBudgetOptions Policy)
{
    public bool IsManaged => true;
}

/// <summary>Explicit outcome: either the command is not a managed Work (legacy path) or a frozen binding exists.</summary>
public sealed record ManagedWorkBudgetBindingResult(ManagedWorkBudgetBinding? Binding)
{
    public bool IsManaged => Binding is not null;
    public static ManagedWorkBudgetBindingResult NotManaged { get; } = new((ManagedWorkBudgetBinding?)null);
}

public interface IManagedWorkBudgetPolicyProvider
{
    /// <summary>Returns null when managed Work budgets are disabled; an invalid policy throws instead of falling back.</summary>
    ManagedWorkBudgetOptions? GetPolicy();
}

/// <summary>Binds the host configuration section into the immutable policy snapshot; defaults to disabled.</summary>
public sealed class ConfigurationManagedWorkBudgetPolicyProvider(ManagedWorkBudgetOptions options)
    : IManagedWorkBudgetPolicyProvider
{
    public ManagedWorkBudgetOptions? GetPolicy() => options.Enabled ? options : null;
}

/// <summary>Validated limits/identity derived from the policy; invalid policy is an explicit managed failure.</summary>
internal static class ManagedWorkBudgetPolicyValidator
{
    public static BudgetLimits ValidateLimits(ManagedWorkBudgetOptions policy)
    {
        if (policy is null)
            throw new ManagedWorkBudgetException(ManagedWorkBudgetErrorCodes.PolicyInvalid, "A managed work budget policy is required.");
        var mode = ParseMode(policy.Mode);
        if (mode is BudgetEnforcementMode.Legacy)
            throw new ManagedWorkBudgetException(ManagedWorkBudgetErrorCodes.PolicyInvalid,
                "Managed work budgets cannot run in Legacy mode; choose Enforced or Shadow.");
        if (string.IsNullOrWhiteSpace(policy.CostUnit) || policy.CostUnit.Length > 32)
            throw new ManagedWorkBudgetException(ManagedWorkBudgetErrorCodes.PolicyInvalid, "A bounded account currency is required.");
        if (policy.MaxConcurrentAttempts < 1)
            throw new ManagedWorkBudgetException(ManagedWorkBudgetErrorCodes.PolicyInvalid, "Concurrent attempts must be at least one.");
        if (policy.MaxOutputTokens is < 0 || policy.MaxCost is < 0m || policy.MaxCalls is < 0
            || policy.MaxInputTokensPerRequest is < 0)
            throw new ManagedWorkBudgetException(ManagedWorkBudgetErrorCodes.PolicyInvalid, "Budget limits cannot be negative.");
        if (policy.MaxOutputTokens is null && policy.MaxCost is null && policy.MaxCalls is null
            && policy.MaxInputTokensPerRequest is null)
            throw new ManagedWorkBudgetException(ManagedWorkBudgetErrorCodes.PolicyInvalid,
                "A managed work budget policy needs at least one trusted limit.");
        return new BudgetLimits(policy.MaxOutputTokens, policy.MaxCost, policy.MaxCalls,
            policy.MaxConcurrentAttempts, policy.MaxInputTokensPerRequest);
    }

    public static BudgetEnforcementMode ParseMode(string? mode) => mode switch
    {
        "Enforced" => BudgetEnforcementMode.Enforced,
        "Shadow" => BudgetEnforcementMode.Shadow,
        "Legacy" => BudgetEnforcementMode.Legacy,
        _ => throw new ManagedWorkBudgetException(ManagedWorkBudgetErrorCodes.PolicyInvalid,
            "The enforcement mode must be Enforced, Shadow or Legacy."),
    };
}
