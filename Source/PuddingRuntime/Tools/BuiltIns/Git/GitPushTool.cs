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
        description: "将本地分支的提交推送（push）到远程仓库。何时用：把本地已提交的成果发布到远端，让其他人可见。怎么用/坑：Remote 默认 origin，Branch 缺省为当前分支；远端领先于本地时推送会被拒绝，需先 pull 合并；Force=true 强制覆盖远端历史（带 lease 语义），仅当确定要改写远端历史时使用，否则有丢失他人提交的风险；需要网络与推送权限。",
        category: ToolCategory.FileSystem,
        permission: ToolPermissionLevel.High,
        safety: ToolSafetyFlags.RequiresNetwork | ToolSafetyFlags.Destructive,
        SortOrder = 68)]
    // 2026-08-28 复审裁定（依据用户 2026-08-28 裁定矩阵 (A)）：git_push 支持 Force=true（+refs/heads 强制推送）可覆写远端历史，
    // 属「可直接损坏/覆写远端用户数据」，恢复 High+Destructive 运行时授权门禁；普通 push（Force=false）为无损路径，
    // 但同一工具暴露强制覆写能力，故整工具保持 High 待用户在场授权（与 git_reset 同理）
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
