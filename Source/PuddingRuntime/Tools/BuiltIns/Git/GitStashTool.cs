using System.Linq;
using System.Text.Json;
using LibGit2Sharp;
using PuddingCode.Models;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.Tools
{
    public sealed record GitStashArgs
    {
        [ToolParam("Git repository path (defaults to current working directory)")]
        public string? Path { get; init; }

        [ToolParam("Stash message (optional, defaults to \"WIP\")")]
        public string? Message { get; init; }
    }

    [Tool(
        id: GitConstants.StashId,
        name: "Git Stash",
        description: "将当前工作目录与索引的改动暂存（stash）起来，稍后可恢复复用。Stash the current working directory and index changes away for later reuse",
        category: ToolCategory.FileSystem,
        permission: ToolPermissionLevel.Medium,
        safety: ToolSafetyFlags.None,
        SortOrder = 72)]
    public sealed class GitStashTool : PuddingToolBase<GitStashArgs>
    {
        protected override Task<ToolExecutionResult> ExecuteCoreAsync(
            GitStashArgs args, ToolExecutionContext context, CancellationToken ct)
        {
            try
            {
                var repoPath = args.Path
                    ?? HostFileToolPaths.ResolveWorkspaceRoot(context.WorkingDirectory);

                using var repo = new Repository(repoPath);

                var sig = repo.Config.BuildSignature(DateTimeOffset.UtcNow);
                if (sig is null)
                {
                    return Task.FromResult(ToolExecutionResult.Fail(
                        "Unable to build a stash signature. Configure user.name and user.email in Git config."));
                }

                var stash = repo.Stashes.Add(sig, args.Message ?? "WIP");

                return Task.FromResult(ToolExecutionResult.Ok(JsonSerializer.Serialize(new
                {
                    path = repoPath,
                    stashed = true,
                    count = repo.Stashes.Count()
                })));
            }
            catch (LibGit2SharpException ex)
            {
                return Task.FromResult(ToolExecutionResult.Fail(ex.Message));
            }
            catch (ArgumentException ex)
            {
                return Task.FromResult(ToolExecutionResult.Fail(ex.Message));
            }
        }
    }
}
