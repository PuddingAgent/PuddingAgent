using PuddingCode.Runtime;
using PuddingExecutionResources.Contracts;

namespace PuddingRuntime.Services;

/// <summary>
/// Shared conversion from the host-bound <see cref="RuntimeWorkBudgetBinding"/> carried on the execution identity to the
/// per-send <see cref="LlmAttemptBudget"/> consumed by the invocation facade. A managed execution must never fall back to a
/// direct provider client, and an invalid carrier fails closed before dispatch.
/// </summary>
internal static class WorkBudgetAttempt
{
    public static bool HasManagedBinding(RuntimeExecutionIdentity? identity) => identity?.WorkBudget is not null;

    /// <summary>
    /// Builds the per-send budget attempt. A distinct attempt id is created for every model send; the Work account and its
    /// frozen limits are reused. Returns null for unmanaged executions (legacy behavior unchanged).
    /// </summary>
    public static LlmAttemptBudget? Create(RuntimeExecutionIdentity? identity, string? attemptId = null)
    {
        var binding = identity?.WorkBudget;
        if (binding is null)
            return null;
        if (!binding.IsValid)
        {
            throw new ModelAttemptBudgetException(BudgetErrorCodes.RequestInvalid,
                "The managed work budget carrier is invalid.");
        }

        var id = string.IsNullOrWhiteSpace(attemptId) ? Guid.NewGuid().ToString("N") : attemptId;
        return new LlmAttemptBudget(
            binding.AccountId,
            [.. binding.Ancestors],
            binding.RootKind,
            binding.RootId,
            binding.WorkspaceId,
            binding.AgentId,
            binding.SessionId,
            id,
            binding.ModelRevision,
            binding.PriceVersion,
            binding.MaxInputTokensPerAttempt,
            binding.MaxOutputTokensPerAttempt,
            binding.CostUnit);
    }

    /// <summary>Managed executions require the invocation facade; the direct LLM client is a legacy-only fallback.</summary>
    public static void RequireInvocationFacade(RuntimeExecutionIdentity? identity, object? facade)
    {
        if (!HasManagedBinding(identity))
            return;
        if (facade is null)
        {
            throw new ModelAttemptBudgetException(BudgetErrorCodes.DispatchInvalid,
                "A managed work execution requires the LLM invocation facade; refusing a direct provider call.");
        }
    }

    /// <summary>The carrier must describe exactly this invocation's workspace/agent/session before any dispatch.</summary>
    public static void EnsureIdentityMatches(
        RuntimeExecutionIdentity? identity, string workspaceId, string agentInstanceId, string sessionId)
    {
        var binding = identity?.WorkBudget;
        if (binding is null)
            return;
        if (!string.Equals(binding.WorkspaceId, workspaceId, StringComparison.Ordinal)
            || !string.Equals(binding.AgentId, agentInstanceId, StringComparison.Ordinal)
            || !string.Equals(binding.SessionId, sessionId, StringComparison.Ordinal))
        {
            throw new ModelAttemptBudgetException(BudgetErrorCodes.RequestInvalid,
                "The managed work budget identity does not match the invocation workspace/agent/session.");
        }
    }
}
