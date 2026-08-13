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
        description: "列出所有本地分支（branch），并标记当前所在分支。何时用：执行分支相关操作前先确认仓库里有哪些本地分支、当前 HEAD 在哪个分支，避免切错或改错。怎么用/坑：只读操作、并发安全；只列本地分支，远端分支（origin/xxx）不在结果中。List all local branches with the current branch marked; use before branch operations to confirm what exists and where HEAD is; read-only and remote-tracking branches are excluded",
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
