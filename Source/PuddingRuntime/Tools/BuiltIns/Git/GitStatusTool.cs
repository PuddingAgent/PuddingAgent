using System.Text.Json;
using LibGit2Sharp;
using PuddingCode.Models;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.Tools
{
    public sealed record GitStatusArgs
    {
        [ToolParam("Git repository path (defaults to current working directory)")]
        public string? Path { get; init; }
    }

    [Tool(
        id: GitConstants.StatusId,
        name: "Git Status",
        description: "Show the working tree status: modified, added, deleted, and untracked files",
        category: ToolCategory.FileSystem,
        permission: ToolPermissionLevel.Low,
        safety: ToolSafetyFlags.ReadOnly | ToolSafetyFlags.ConcurrencySafe,
        SortOrder = 60)]
    public sealed class GitStatusTool : PuddingToolBase<GitStatusArgs>
    {
        protected override Task<ToolExecutionResult> ExecuteCoreAsync(
            GitStatusArgs args, ToolExecutionContext context, CancellationToken ct)
        {
            var repoPath = args.Path
                ?? HostFileToolPaths.ResolveWorkspaceRoot(context.WorkingDirectory);

            using var repo = new Repository(repoPath);
            var status = repo.RetrieveStatus();

            var entries = status.Select(e => new
            {
                path = e.FilePath,
                state = e.State.ToString()
            }).ToList();

            return Task.FromResult(ToolExecutionResult.Ok(JsonSerializer.Serialize(new
            {
                path = repoPath,
                branch = repo.Head.FriendlyName,
                has_changes = status.IsDirty,
                entries
            })));
        }
    }
}
