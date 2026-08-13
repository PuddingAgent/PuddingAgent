using System.Text.Json;
using LibGit2Sharp;
using PuddingCode.Models;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.Tools
{
    public sealed record GitInitArgs
    {
        [ToolParam("Directory in which to initialize the repository (defaults to current working directory)")]
        public string? Path { get; init; }

        [ToolParam("Create a bare repository (default: false)")]
        public bool Bare { get; init; }
    }

    [Tool(
        id: GitConstants.InitId,
        name: "Git Init",
        description: "在指定目录初始化（init）新的 Git 仓库。Initialize a new Git repository in the specified directory",
        category: ToolCategory.FileSystem,
        permission: ToolPermissionLevel.High,
        safety: ToolSafetyFlags.Destructive | ToolSafetyFlags.ConcurrencySafe,
        SortOrder = 70)]
    public sealed class GitInitTool : PuddingToolBase<GitInitArgs>
    {
        protected override Task<ToolExecutionResult> ExecuteCoreAsync(
            GitInitArgs args, ToolExecutionContext context, CancellationToken ct)
        {
            var initPath = args.Path
                ?? HostFileToolPaths.ResolveWorkspaceRoot(context.WorkingDirectory);

            try
            {
                var repoPath = Repository.Init(initPath, args.Bare);

                return Task.FromResult(ToolExecutionResult.Ok(JsonSerializer.Serialize(new
                {
                    path = repoPath,
                    bare = args.Bare,
                    initialized = true
                })));
            }
            catch (LibGit2SharpException ex)
            {
                return Task.FromResult(ToolExecutionResult.Fail(
                    $"Git init failed: {ex.Message}"));
            }
        }
    }
}
