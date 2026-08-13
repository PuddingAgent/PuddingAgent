using System.Text.Json;
using LibGit2Sharp;
using PuddingCode.Models;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.Tools
{
    public sealed record GitPushArgs
    {
        [ToolParam("Remote name (default: origin)")]
        public string Remote { get; init; } = "origin";

        [ToolParam("Branch to push (default: current branch)")]
        public string? Branch { get; init; }

        [ToolParam("Force-push with lease semantics (default: false)")]
        public bool Force { get; init; }

        [ToolParam("Git repository path (defaults to current working directory)")]
        public string? Path { get; init; }
    }

    [Tool(
        id: GitConstants.PushId,
        name: "Git Push",
        description: "将本地分支的提交推送（push）到远程仓库。Push commits from a local branch to a remote repository",
        category: ToolCategory.FileSystem,
        permission: ToolPermissionLevel.High,
        safety: ToolSafetyFlags.Destructive | ToolSafetyFlags.ConcurrencySafe,
        SortOrder = 68)]
    public sealed class GitPushTool : PuddingToolBase<GitPushArgs>
    {
        protected override Task<ToolExecutionResult> ExecuteCoreAsync(
            GitPushArgs args, ToolExecutionContext context, CancellationToken ct)
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
                var pushRefSpec = args.Force
                    ? $"+refs/heads/{branchName}:refs/heads/{branchName}"
                    : $"refs/heads/{branchName}:refs/heads/{branchName}";

                repo.Network.Push(remote, pushRefSpec, new PushOptions());

                return Task.FromResult(ToolExecutionResult.Ok(JsonSerializer.Serialize(new
                {
                    path = repoPath,
                    remote = args.Remote,
                    branch = branchName,
                    forced = args.Force,
                    pushed = true
                })));
            }
            catch (LibGit2SharpException ex)
            {
                return Task.FromResult(ToolExecutionResult.Fail(
                    $"Git push failed: {ex.Message}"));
            }
        }
    }
}
