using System.Linq;
using System.Text.Json;
using LibGit2Sharp;
using PuddingCode.Models;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.Tools
{
    public sealed record GitBlameArgs
    {
        [ToolParam("Git repository path (defaults to current working directory)")]
        public string? Path { get; init; }

        [ToolParam("File path (relative to repository root) to blame")]
        public required string FilePath { get; init; }
    }

    [Tool(
        id: GitConstants.BlameId,
        name: "Git Blame",
        description: "显示文件中每一行最后一次被哪个提交（commit）和作者（author）修改（blame）。何时用：定位某段代码是谁、在哪个提交里引入或修改的，排查历史变更原因。怎么用/坑：FilePath 必填、相对仓库根目录；基于当前文件内容，重排或大改后行归属可能失真；只读操作、并发安全。",
        category: ToolCategory.FileSystem,
        permission: ToolPermissionLevel.Low,
        safety: ToolSafetyFlags.ReadOnly | ToolSafetyFlags.ConcurrencySafe,
        SortOrder = 74)]
    // 2026-08-28 裁定（依据用户 2026-08-28 指示）：git_blame 纯本地只读查询（ReadOnly 标注），不写/删任何数据，Low+ReadOnly|ConcurrencySafe 免审直通
    public sealed class GitBlameTool : PuddingToolBase<GitBlameArgs>
    {
        protected override Task<ToolExecutionResult> ExecuteCoreAsync(
            GitBlameArgs args, ToolExecutionContext context, CancellationToken ct)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(args.FilePath))
                {
                    return Task.FromResult(ToolExecutionResult.Fail(
                        "FilePath is required."));
                }

                var repoPath = args.Path
                    ?? HostFileToolPaths.ResolveWorkspaceRoot(context.WorkingDirectory);

                using var repo = new Repository(repoPath);

                var blame = repo.Blame(args.FilePath.Trim(), new BlameOptions());

                return Task.FromResult(ToolExecutionResult.Ok(JsonSerializer.Serialize(new
                {
                    path = repoPath,
                    file = args.FilePath,
                    hunks = blame.Select(h => new
                    {
                        line_start = h.InitialStartLineNumber,
                        line_count = h.LineCount,
                        commit = h.InitialCommit.Sha,
                        author = h.InitialCommit.Author.Name,
                        summary = h.InitialCommit.MessageShort
                    })
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
