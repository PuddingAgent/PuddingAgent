using System.Text.Json;
using LibGit2Sharp;
using PuddingCode.Models;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.Tools
{
    public sealed record GitCloneArgs
    {
        [ToolParam("Remote repository URL to clone")]
        public required string Url { get; init; }

        [ToolParam("Local directory path where the repository will be cloned")]
        public required string LocalPath { get; init; }

        [ToolParam("Branch to check out after clone (optional, default: remote HEAD)")]
        public string? Branch { get; init; }
    }

    [Tool(
        id: GitConstants.CloneId,
        name: "Git Clone",
        description: "将远程 Git 仓库克隆（clone）到本地目录。Clone a remote Git repository into a local directory",
        category: ToolCategory.FileSystem,
        permission: ToolPermissionLevel.High,
        safety: ToolSafetyFlags.Destructive | ToolSafetyFlags.ConcurrencySafe,
        SortOrder = 71)]
    public sealed class GitCloneTool : PuddingToolBase<GitCloneArgs>
    {
        protected override Task<ToolExecutionResult> ExecuteCoreAsync(
            GitCloneArgs args, ToolExecutionContext context, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(args.Url))
                return Task.FromResult(ToolExecutionResult.Fail("Url must not be empty."));

            if (string.IsNullOrWhiteSpace(args.LocalPath))
                return Task.FromResult(ToolExecutionResult.Fail("LocalPath must not be empty."));

            try
            {
                var options = new CloneOptions();
                if (!string.IsNullOrWhiteSpace(args.Branch))
                    options.BranchName = args.Branch;

                var localPath = Repository.Clone(args.Url, args.LocalPath, options);

                return Task.FromResult(ToolExecutionResult.Ok(JsonSerializer.Serialize(new
                {
                    url = args.Url,
                    path = localPath,
                    branch = options.BranchName,
                    cloned = true
                })));
            }
            catch (LibGit2SharpException ex)
            {
                return Task.FromResult(ToolExecutionResult.Fail(
                    $"Git clone failed: {ex.Message}"));
            }
        }
    }
}
