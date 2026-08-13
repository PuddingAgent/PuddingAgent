using System.Text.Json;
using LibGit2Sharp;
using PuddingCode.Models;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.Tools
{
    public sealed record GitBranchListArgs
    {
        [ToolParam("Git repository path (defaults to current working directory)")]
        public string? Path { get; init; }
    }

    [Tool(
        id: GitConstants.BranchListId,
        name: "Git Branch List",
        description: "List all local branches with the current branch marked",
        category: ToolCategory.FileSystem,
        permission: ToolPermissionLevel.Low,
        safety: ToolSafetyFlags.ReadOnly | ToolSafetyFlags.ConcurrencySafe,
        SortOrder = 63)]
    public sealed class GitBranchListTool : PuddingToolBase<GitBranchListArgs>
    {
        protected override Task<ToolExecutionResult> ExecuteCoreAsync(
            GitBranchListArgs args, ToolExecutionContext context, CancellationToken ct)
        {
            var repoPath = args.Path
                ?? HostFileToolPaths.ResolveWorkspaceRoot(context.WorkingDirectory);

            using var repo = new Repository(repoPath);

            var branches = repo.Branches
                .Where(b => !b.IsRemote)
                .Select(b => new
                {
                    name = b.FriendlyName,
                    is_current = b.IsCurrentRepositoryHead
                })
                .ToList();

            return Task.FromResult(ToolExecutionResult.Ok(JsonSerializer.Serialize(new
            {
                path = repoPath,
                count = branches.Count,
                branches
            })));
        }
    }
}
