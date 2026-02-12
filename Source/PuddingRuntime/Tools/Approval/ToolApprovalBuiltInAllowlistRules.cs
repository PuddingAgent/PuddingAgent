using PuddingCode.Tools;

namespace PuddingRuntime.Services.Tools;

internal static class ToolApprovalBuiltInAllowlistRules
{
    public static IReadOnlyList<ToolApprovalAllowlistRule> Create()
    {
        var now = DateTimeOffset.Parse("2026-06-03T00:00:00Z");
        return
        [
            .. new[]
            {
                "pwd",
                "ls",
                "dir",
                "Get-Location",
                "Get-ChildItem",
                "whoami",
                "hostname",
                "date",
                "Get-Date",
                "git status",
                "git branch",
                "dotnet --version",
                "node --version",
                "npm --version",
            }.Select(command => new ToolApprovalAllowlistRule
            {
                RuleId = "builtin_shell_" + command
                    .ToLowerInvariant()
                    .Replace(" ", "_", StringComparison.Ordinal)
                    .Replace("-", "_", StringComparison.Ordinal),
                WorkspaceId = null,
                ToolId = "shell",
                Command = command,
                Source = ToolApprovalAllowlistRuleSource.BuiltIn,
                Status = ToolApprovalAllowlistRuleStatus.Enabled,
                Reason = "Built-in read-only shell command.",
                CreatedAtUtc = now,
            }),
        ];
    }
}
