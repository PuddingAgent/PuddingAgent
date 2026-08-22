using System.Text.Json;
using LibGit2Sharp;
using PuddingCode.Models;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.Tools
{
    public sealed record GitPullArgs
    {
        [ToolParam("Remote name (default: origin)")]
        public string Remote { get; init; } = "origin";

        [ToolParam("Branch to pull and merge (default: current branch)")]
        public string? Branch { get; init; }

        [ToolParam("Git repository path (defaults to current working directory)")]
        public string? Path { get; init; }
    }

    [Tool(
        id: GitConstants.PullId,
        name: "Git Pull",
        description: "从远程仓库拉取（pull）并将远程分支合并到当前分支。何时用：与远端保持同步，把别人推送的提交拉到本地并合并。怎么用/坑：Remote 默认 origin，Branch 缺省为当前分支；本质是 fetch + merge，可能产生冲突（返回 has_conflicts=true），需手动解决；工作区有未提交改动时合并可能失败；需要网络。",
        category: ToolCategory.FileSystem,
        permission: ToolPermissionLevel.Medium,
        safety: ToolSafetyFlags.ConcurrencySafe | ToolSafetyFlags.RequiresNetwork,
        SortOrder = 69)]
    public sealed class GitPullTool : PuddingToolBase<GitPullArgs>
    {
        protected override Task<ToolExecutionResult> ExecuteCoreAsync(
            GitPullArgs args, ToolExecutionContext context, CancellationToken ct)
        {
            var repoPath = args.Path
                ?? HostFileToolPaths.ResolveWorkspaceRoot(context.WorkingDirectory);

            try
            {
                using var repo = new Repository(repoPath);

                var remote = repo.Network.Remotes[args.Remote];
                if (remote is null)
                    return Task.FromResult(ToolExecutionResult.Fail(
                        $"Remote '{args.Remote}' does not exist."));

                var branchName = args.Branch ?? repo.Head.FriendlyName;
                var refSpecs = new[]
                {
                    $"refs/heads/{branchName}:refs/remotes/{args.Remote}/{branchName}"
                };

                Commands.Fetch(repo, args.Remote, refSpecs, new FetchOptions(), null);

                var sig = repo.Config.BuildSignature(DateTimeOffset.Now);
                var mergeResult = repo.MergeFetchedRefs(sig, new MergeOptions());

                return Task.FromResult(ToolExecutionResult.Ok(JsonSerializer.Serialize(new
                {
                    path = repoPath,
                    remote = args.Remote,
                    branch = branchName,
                    status = mergeResult.Status.ToString(),
                    commit = mergeResult.Commit?.Sha,
                    has_conflicts = mergeResult.Status == MergeStatus.Conflicts
                })));
            }
            catch (LibGit2SharpException ex)
            {
                return Task.FromResult(ToolExecutionResult.Fail(
                    $"Git pull failed: {ex.Message}"));
            }
        }
    }
}
