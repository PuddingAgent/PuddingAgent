using System.Text.Json;
using LibGit2Sharp;
using PuddingCode.Models;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.Tools
{
    public sealed record GitBranchCreateArgs
    {
        [ToolParam("Git repository path (defaults to current working directory)")]
        public string? Path { get; init; }

        [ToolParam("Name of the new branch to create")]
        public required string BranchName { get; init; }
    }

    [Tool(
        id: GitConstants.BranchCreateId,
        name: "Git Branch Create",
        description: "Create a new branch in the repository",
        category: ToolCategory.FileSystem,
        permission: ToolPermissionLevel.Medium,
        safety: ToolSafetyFlags.None,
        SortOrder = 64)]
    public sealed class GitBranchCreateTool : PuddingToolBase<GitBranchCreateArgs>
    {
        protected override Task<ToolExecutionResult> ExecuteCoreAsync(
            GitBranchCreateArgs args, ToolExecutionContext context, CancellationToken ct)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(args.BranchName))
                {
                    return Task.FromResult(ToolExecutionResult.Fail(
                        "BranchName is required."));
                }

                var repoPath = args.Path
                    ?? context.WorkingDirectory
                    ?? Directory.GetCurrentDirectory();

                using var repo = new Repository(repoPath);

                var branch = repo.CreateBranch(args.BranchName.Trim());
                if (branch is null)
                {
                    return Task.FromResult(ToolExecutionResult.Fail(
                        $"Branch '{args.BranchName}' could not be created."));
                }

                return Task.FromResult(ToolExecutionResult.Ok(JsonSerializer.Serialize(new
                {
                    path = repoPath,
                    name = branch.FriendlyName,
                    created = true
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
