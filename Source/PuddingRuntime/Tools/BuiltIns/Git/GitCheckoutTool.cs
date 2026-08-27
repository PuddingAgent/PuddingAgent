using System.Text.Json;
using LibGit2Sharp;
using PuddingCode.Models;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.Tools
{
    public sealed record GitCheckoutArgs
    {
        [ToolParam("Git repository path (defaults to current working directory)")]
        public string? Path { get; init; }

        [ToolParam("Commit, branch or tag to check out")]
        public required string Target { get; init; }
    }

    [Tool(
        id: GitConstants.CheckoutId,
        name: "Git Checkout",
        description: "检出（checkout）提交、分支或标签；检出非分支时会分离 HEAD。何时用：查看历史提交或标签的快照、临时切到某次提交验证行为。怎么用/坑：Target 必填，可为分支、tag 或 commit SHA；检出 commit/tag 进入 detached HEAD，此时新提交不属于任何分支、容易丢失，若想基于旧提交继续工作应先建分支；工作区有未提交改动时可能被拒绝。",
        category: ToolCategory.FileSystem,
        permission: ToolPermissionLevel.Medium,
        safety: ToolSafetyFlags.None,
        SortOrder = 77)]
    public sealed class GitCheckoutTool : PuddingToolBase<GitCheckoutArgs>
    {
        protected override Task<ToolExecutionResult> ExecuteCoreAsync(
            GitCheckoutArgs args, ToolExecutionContext context, CancellationToken ct)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(args.Target))
                {
                    return Task.FromResult(ToolExecutionResult.Fail(
                        "Target is required."));
                }

                var repoPath = args.Path
                    ?? HostFileToolPaths.ResolveWorkspaceRoot(context.WorkingDirectory);

                using var repo = new Repository(repoPath);

                Commands.Checkout(repo, args.Target.Trim());

                return Task.FromResult(ToolExecutionResult.Ok(JsonSerializer.Serialize(new
                {
                    path = repoPath,
                    current_branch = repo.Head.FriendlyName,
                    target = args.Target.Trim()
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
