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
        description: "检出并切换到已有分支（branch checkout/switch）。何时用：在已存在的分支之间切换工作上下文。怎么用/坑：BranchName 必填且分支必须已存在（不存在先用 git_branch_create 创建）；工作区有未提交改动且与目标分支冲突时切换会失败，先 commit 或 stash；切换会直接改变工作区文件内容。",
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
