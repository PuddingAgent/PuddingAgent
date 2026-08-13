using System.Text.Json;
using LibGit2Sharp;
using PuddingCode.Models;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.Tools
{
    public sealed record GitRemoteArgs
    {
        [ToolParam("Git repository path (defaults to current working directory)")]
        public string? Path { get; init; }
    }

    [Tool(
        id: GitConstants.RemoteId,
        name: "Git Remote",
        description: "列出仓库配置的全部远程仓库（remote）。何时用：执行 clone/pull/push/fetch 前确认远端名称与 URL，或排查远端配置问题。怎么用/坑：只读操作；结果含远端名与 URL；未配置任何 remote 时 count 为 0，此时 pull/push 会失败，需先用 git clone 或手动添加远端。List all remotes configured for the repository; use to confirm remote names and URLs before clone/pull/push/fetch; read-only, and pull/push will fail when no remotes are configured",
        category: ToolCategory.FileSystem,
        permission: ToolPermissionLevel.Low,
        safety: ToolSafetyFlags.None,
        SortOrder = 76)]
    public sealed class GitRemoteTool : PuddingToolBase<GitRemoteArgs>
    {
        protected override Task<ToolExecutionResult> ExecuteCoreAsync(
            GitRemoteArgs args, ToolExecutionContext context, CancellationToken ct)
        {
            try
            {
                var repoPath = args.Path
                    ?? HostFileToolPaths.ResolveWorkspaceRoot(context.WorkingDirectory);

                using var repo = new Repository(repoPath);

                var remotes = repo.Network.Remotes
                    .Select(r => new
                    {
                        name = r.Name,
                        url = r.Url
                    })
                    .ToList();

                return Task.FromResult(ToolExecutionResult.Ok(JsonSerializer.Serialize(new
                {
                    path = repoPath,
                    count = remotes.Count,
                    remotes
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
