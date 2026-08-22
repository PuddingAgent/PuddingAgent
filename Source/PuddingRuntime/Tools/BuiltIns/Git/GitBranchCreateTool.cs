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
        description: "在仓库中创建新分支（branch）。何时用：开始新功能或修复前，从当前 HEAD 拉一条独立分支，避免直接污染主干。怎么用/坑：BranchName 必填；创建后不会自动切换，需再用 git_branch_switch 或 git_checkout 切过去；分支名不能含空格或非法字符。",
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
                    ?? HostFileToolPaths.ResolveWorkspaceRoot(context.WorkingDirectory);

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
