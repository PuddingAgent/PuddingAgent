using System.Text.Json;
using LibGit2Sharp;
using PuddingCode.Models;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.Tools
{
    public sealed record GitMergeArgs
    {
        [ToolParam("Git repository path (defaults to current working directory)")]
        public string? Path { get; init; }

        [ToolParam("Name of the branch to merge into the current branch")]
        public required string Branch { get; init; }
    }

    [Tool(
        id: GitConstants.MergeId,
        name: "Git Merge",
        description: "Merge the given branch into the current branch",
        category: ToolCategory.FileSystem,
        permission: ToolPermissionLevel.High,
        safety: ToolSafetyFlags.Destructive,
        SortOrder = 73)]
    public sealed class GitMergeTool : PuddingToolBase<GitMergeArgs>
    {
        protected override Task<ToolExecutionResult> ExecuteCoreAsync(
            GitMergeArgs args, ToolExecutionContext context, CancellationToken ct)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(args.Branch))
                {
                    return Task.FromResult(ToolExecutionResult.Fail(
                        "Branch is required."));
                }

                var repoPath = args.Path
                    ?? HostFileToolPaths.ResolveWorkspaceRoot(context.WorkingDirectory);

                using var repo = new Repository(repoPath);

                var sig = repo.Config.BuildSignature(DateTimeOffset.UtcNow);
                if (sig is null)
                {
                    return Task.FromResult(ToolExecutionResult.Fail(
                        "Unable to build a merge signature. Configure user.name and user.email in Git config."));
                }

                var branch = repo.Branches[args.Branch.Trim()];
                if (branch is null)
                {
                    return Task.FromResult(ToolExecutionResult.Fail(
                        $"Branch '{args.Branch}' was not found in this repository."));
                }

                var result = repo.Merge(branch, sig, new MergeOptions());

                return Task.FromResult(ToolExecutionResult.Ok(JsonSerializer.Serialize(new
                {
                    path = repoPath,
                    merged_branch = args.Branch,
                    status = result.Status.ToString()
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
