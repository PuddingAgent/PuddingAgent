using System.Text.Json;
using LibGit2Sharp;
using PuddingCode.Models;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.Tools
{
    public sealed record GitResetArgs
    {
        [ToolParam("Git repository path (defaults to current working directory)")]
        public string? Path { get; init; }

        [ToolParam("Target commit or ref to reset to (default: HEAD)")]
        public string? Target { get; init; }

        [ToolParam("Reset mode: soft, mixed, or hard (default: mixed)")]
        public string? Mode { get; init; }
    }

    [Tool(
        id: GitConstants.ResetId,
        name: "Git Reset",
        description: "Reset the current branch HEAD to a specified commit (soft, mixed, or hard)",
        category: ToolCategory.FileSystem,
        permission: ToolPermissionLevel.High,
        safety: ToolSafetyFlags.Destructive,
        SortOrder = 78)]
    public sealed class GitResetTool : PuddingToolBase<GitResetArgs>
    {
        protected override Task<ToolExecutionResult> ExecuteCoreAsync(
            GitResetArgs args, ToolExecutionContext context, CancellationToken ct)
        {
            var repoPath = args.Path
                ?? HostFileToolPaths.ResolveWorkspaceRoot(context.WorkingDirectory);

            try
            {
                using var repo = new Repository(repoPath);

                var mode = (args.Mode ?? "mixed").ToLowerInvariant() switch
                {
                    "soft" => ResetMode.Soft,
                    "hard" => ResetMode.Hard,
                    _ => ResetMode.Mixed
                };

                var commit = repo.Lookup<Commit>(args.Target ?? "HEAD");
                if (commit is null)
                    return Task.FromResult(ToolExecutionResult.Fail(
                        $"Target '{args.Target ?? "HEAD"}' is not a valid commit."));

                repo.Reset(mode, commit);

                return Task.FromResult(ToolExecutionResult.Ok(JsonSerializer.Serialize(new
                {
                    path = repoPath,
                    mode = args.Mode ?? "mixed",
                    target = args.Target ?? "HEAD"
                })));
            }
            catch (LibGit2SharpException ex)
            {
                return Task.FromResult(ToolExecutionResult.Fail(
                    $"Git reset failed: {ex.Message}"));
            }
            catch (ArgumentException ex)
            {
                return Task.FromResult(ToolExecutionResult.Fail(
                    $"Git reset failed: {ex.Message}"));
            }
        }
    }
}
