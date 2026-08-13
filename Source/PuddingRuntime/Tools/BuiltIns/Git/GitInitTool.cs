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
        description: "在指定目录初始化（init）新的 Git 仓库。何时用：把尚未纳入版本控制的项目目录变成 Git 仓库开始跟踪文件。怎么用/坑：Path 缺省为当前工作目录；Bare=true 创建裸仓库（无工作区，一般用于服务端共享）；新仓库还没有任何提交，git_log 等基于历史的操作可能无输出，也需先配置 user.name/user.email 才能提交。Initialize a new Git repository in the specified directory; use to start version-controlling a project; Path defaults to the working directory, Bare=true creates a bare repo (no working tree), and a fresh repo has no commits yet",
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
