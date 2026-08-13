using System.Text.Json;
using LibGit2Sharp;
using PuddingCode.Models;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.Tools
{
    public sealed record GitBranchSwitchArgs
    {
        [ToolParam("Git repository path (defaults to current working directory)")]
        public string? Path { get; init; }

        [ToolParam("Name of the branch to switch to")]
        public required string BranchName { get; init; }
    }

    [Tool(
        id: GitConstants.BranchSwitchId,
        name: "Git Branch Switch",
        description: "检出并切换到已有分支（branch checkout/switch）。Checkout and switch to an existing branch",
        category: ToolCategory.FileSystem,
        permission: ToolPermissionLevel.Medium,
        safety: ToolSafetyFlags.None,
        SortOrder = 65)]
    public sealed class GitBranchSwitchTool : PuddingToolBase<GitBranchSwitchArgs>
    {
        protected override Task<ToolExecutionResult> ExecuteCoreAsync(
            GitBranchSwitchArgs args, ToolExecutionContext context, CancellationToken ct)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(args.BranchName))
                {
                    return Task.FromResult(ToolExecutionResult.Fail(
                        "BranchName is required."));
                }

                var repoPath = args.Path
                    ?? HostFileToolPaths.ResolveWorkspaceRoot(context.WorkingDirectory);

                using var repo = new Repository(repoPath);

                var branch = repo.Branches[args.BranchName.Trim()];
                if (branch is null)
                {
                    return Task.FromResult(ToolExecutionResult.Fail(
                        $"Branch '{args.BranchName}' was not found in this repository."));
                }

                Commands.Checkout(repo, branch);

                return Task.FromResult(ToolExecutionResult.Ok(JsonSerializer.Serialize(new
                {
                    path = repoPath,
                    name = repo.Head.FriendlyName,
                    switched = true
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
