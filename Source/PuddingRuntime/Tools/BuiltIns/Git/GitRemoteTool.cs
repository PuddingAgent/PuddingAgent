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
        description: "列出仓库配置的全部远程仓库（remote）。List all remotes configured for the repository",
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
