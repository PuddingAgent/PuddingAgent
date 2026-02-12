using System.Collections.Concurrent;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.Tools;

/// <summary>In-memory store for fast automatic approval allowlist rules.</summary>
public sealed class InMemoryToolApprovalAllowlistStore : IToolApprovalAllowlistStore
{
    private readonly ConcurrentDictionary<string, ToolApprovalAllowlistRule> _rules = new(StringComparer.Ordinal);

    public InMemoryToolApprovalAllowlistStore()
    {
        foreach (var rule in ToolApprovalBuiltInAllowlistRules.Create())
            _rules[rule.RuleId] = rule;
    }

    public Task SaveAsync(ToolApprovalAllowlistRule rule, CancellationToken ct = default)
    {
        _rules[rule.RuleId] = rule;
        return Task.CompletedTask;
    }

    public Task<ToolApprovalAllowlistRule?> GetAsync(string ruleId, CancellationToken ct = default)
    {
        _rules.TryGetValue(ruleId, out var rule);
        return Task.FromResult(rule);
    }

    public Task<IReadOnlyList<ToolApprovalAllowlistRule>> ListAsync(CancellationToken ct = default)
    {
        IReadOnlyList<ToolApprovalAllowlistRule> rules = _rules.Values
            .OrderBy(r => r.Source)
            .ThenBy(r => r.WorkspaceId ?? "")
            .ThenBy(r => r.ToolId)
            .ThenBy(r => r.Command ?? r.ArgumentsJson ?? "")
            .ToArray();
        return Task.FromResult(rules);
    }
}
