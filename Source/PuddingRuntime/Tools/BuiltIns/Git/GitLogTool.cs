using System.Text.Json;
using LibGit2Sharp;
using PuddingCode.Models;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.Tools
{
    public sealed record GitLogArgs
    {
        [ToolParam("Git repository path (defaults to current working directory)")]
        public string? Path { get; init; }

        [ToolParam("Maximum number of commits to return (default 20, max 100)")]
        public int MaxCount { get; init; } = 20;

        [ToolParam("Branch name (default: current branch)")]
        public string? Branch { get; init; }

        [ToolParam("Filter by file path")]
        public string? FilePath { get; init; }
    }

    [Tool(
        id: GitConstants.LogId,
        name: "Git Log",
        description: "显示提交历史/日志（git log），含作者、日期和消息。何时用：了解最近的提交、定位某次变更的 SHA、按分支或文件过滤历史。怎么用/坑：MaxCount 默认 20、上限 100；Branch 缺省为当前分支；FilePath 可限定到单个文件；只读操作、并发安全。Show commit history with author, date, and message; use to review recent commits, find a SHA, or filter history by branch/file; MaxCount defaults to 20 (max 100); read-only",
        category: ToolCategory.FileSystem,
        permission: ToolPermissionLevel.Low,
        safety: ToolSafetyFlags.ReadOnly | ToolSafetyFlags.ConcurrencySafe,
        SortOrder = 61)]
    public sealed class GitLogTool : PuddingToolBase<GitLogArgs>
    {
        protected override Task<ToolExecutionResult> ExecuteCoreAsync(
            GitLogArgs args, ToolExecutionContext context, CancellationToken ct)
        {
            var repoPath = args.Path
                ?? HostFileToolPaths.ResolveWorkspaceRoot(context.WorkingDirectory);

            using var repo = new Repository(repoPath);
            var count = Math.Clamp(args.MaxCount, 1, 100);

            var filter = new CommitFilter
            {
                SortBy = CommitSortStrategies.Topological | CommitSortStrategies.Time
            };

            if (!string.IsNullOrWhiteSpace(args.Branch))
            {
                var branch = repo.Branches[args.Branch];
                if (branch != null)
                    filter.IncludeReachableFrom = branch.Tip;
            }

            var commits = repo.Commits.QueryBy(filter).Take(count);

            var entries = commits.Select(c => new
            {
                sha = c.Sha,
                author = c.Author.Name,
                email = c.Author.Email,
                date = c.Author.When.ToString("o"),
                message = c.MessageShort
            }).ToList();

            return Task.FromResult(ToolExecutionResult.Ok(JsonSerializer.Serialize(new
            {
                path = repoPath,
                branch = repo.Head.FriendlyName,
                count = entries.Count,
                commits = entries
            })));
        }
    }
}
