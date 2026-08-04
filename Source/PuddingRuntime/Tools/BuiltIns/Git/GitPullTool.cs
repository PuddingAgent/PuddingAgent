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
        description: "Fetch from a remote repository and merge the remote branch into the current branch",
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
                ?? context.WorkingDirectory
                ?? Directory.GetCurrentDirectory();

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
