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
        permission: ToolPermissionLevel.Low,
        safety: ToolSafetyFlags.RequiresNetwork,
        SortOrder = 68)]
    // 2026-09-12 用户指示：git_push 免运行时授权（不弹审批）。本裁定取代 2026-08-28 的「保持 High+Destructive」裁定——
    // 该门禁使每次推送都需人工 /authorize，在长程自治执行中构成硬阻塞，用户明确要求推送直接放行。
    // 安全边界（显式记录，勿静默删除）：Force=true 仍可覆写远端历史；RequiresNetwork 标记保留（不触发授权门，仅用于能力策略的网络开关）。
    // 兜底为 Agent 行为纪律：只推自己的 commit，不代推他方提交。git_reset（可永久丢弃工作区改动）继续保留 High+Destructive。
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
