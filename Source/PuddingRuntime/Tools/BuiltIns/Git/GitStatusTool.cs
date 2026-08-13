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
        description: "显示工作区状态（status）：已修改、已添加、已删除和未跟踪的文件。何时用：开始任何 Git 操作前先看状态，确认哪些文件有改动、当前分支名、工作区是否干净。怎么用/坑：只读操作、并发安全；返回 has_changes（工作区是否脏）与逐文件 state；state 为枚举字符串，可据此区分未跟踪/已暂存/已修改等状态。Show the working tree status: modified, added, deleted, and untracked files; use before any Git operation to confirm what changed and whether the tree is clean; read-only, returns has_changes plus per-file states",
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
