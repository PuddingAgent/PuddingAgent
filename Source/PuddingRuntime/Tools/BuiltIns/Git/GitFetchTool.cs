using System.Text.Json;
using LibGit2Sharp;
using PuddingCode.Models;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.Tools
{
    public sealed record GitFetchArgs
    {
        [ToolParam("Git repository path (defaults to current working directory)")]
        public string? Path { get; init; }

        [ToolParam("Remote name (default: origin)")]
        public string? Remote { get; init; }
    }

    [Tool(
        id: GitConstants.FetchId,
        name: "Git Fetch",
        description: "从远程仓库获取（fetch）更新，不进行合并。何时用：只想把远端最新提交下载到本地（更新 origin/xxx 引用）而不动当前分支与工作区，先 fetch 再决定 merge/rebase。怎么用/坑：Remote 默认 origin；需要网络；fetch 不会改动工作区，是安全的查看远端进展方式。Fetch updates from a remote repository without merging; use to download remote refs (e.g. origin/main) without touching your working tree, then decide whether to merge or rebase; needs network access",
        category: ToolCategory.FileSystem,
        permission: ToolPermissionLevel.Medium,
        safety: ToolSafetyFlags.RequiresNetwork | ToolSafetyFlags.ConcurrencySafe,
        SortOrder = 79)]
    public sealed class GitFetchTool : PuddingToolBase<GitFetchArgs>
    {
        protected override Task<ToolExecutionResult> ExecuteCoreAsync(
            GitFetchArgs args, ToolExecutionContext context, CancellationToken ct)
        {
            var repoPath = args.Path
                ?? HostFileToolPaths.ResolveWorkspaceRoot(context.WorkingDirectory);

            try
            {
                using var repo = new Repository(repoPath);

                Commands.Fetch(repo, args.Remote ?? "origin", new string[0], new FetchOptions(), null);

                return Task.FromResult(ToolExecutionResult.Ok(JsonSerializer.Serialize(new
                {
                    path = repoPath,
                    remote = args.Remote ?? "origin",
                    fetched = true
                })));
            }
            catch (LibGit2SharpException ex)
            {
                return Task.FromResult(ToolExecutionResult.Fail(
                    $"Git fetch failed: {ex.Message}"));
            }
            catch (ArgumentException ex)
            {
                return Task.FromResult(ToolExecutionResult.Fail(
                    $"Git fetch failed: {ex.Message}"));
            }
        }
    }
}
