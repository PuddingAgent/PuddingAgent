using System.Text.Json;
using LibGit2Sharp;
using PuddingCode.Models;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.Tools
{
    public sealed record GitAddArgs
    {
        [ToolParam("Git repository path (defaults to current working directory)")]
        public string? Path { get; init; }

        [ToolParam("File paths (relative to repository root) to stage into the index")]
        public required string[] Files { get; init; }
    }

    [Tool(
        id: GitConstants.AddId,
        name: "Git Add",
        description: "将文件路径暂存（stage）到 Git 索引，用于提交前准备。何时用：commit 前需要把改动加入暂存区，或只想提交部分文件时精确指定 Files。怎么用/坑：Files 必填、相对仓库根目录、可传多个；未跟踪的新文件必须先 add 才能被提交；误暂存可用 git_reset 撤销。",
        category: ToolCategory.FileSystem,
        permission: ToolPermissionLevel.Medium,
        safety: ToolSafetyFlags.None,
        SortOrder = 66)]
    public sealed class GitAddTool : PuddingToolBase<GitAddArgs>
    {
        protected override Task<ToolExecutionResult> ExecuteCoreAsync(
            GitAddArgs args, ToolExecutionContext context, CancellationToken ct)
        {
            try
            {
                if (args.Files is not { Length: > 0 })
                {
                    return Task.FromResult(ToolExecutionResult.Fail(
                        "Files is required."));
                }

                var repoPath = args.Path
                    ?? HostFileToolPaths.ResolveWorkspaceRoot(context.WorkingDirectory);

                using var repo = new Repository(repoPath);

                Commands.Stage(repo, args.Files);

                return Task.FromResult(ToolExecutionResult.Ok(JsonSerializer.Serialize(new
                {
                    path = repoPath,
                    count = args.Files.Length,
                    staged = args.Files
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
