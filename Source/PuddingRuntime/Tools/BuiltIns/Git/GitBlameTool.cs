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
        description: "Show which commit and author last modified each line of a file",
        category: ToolCategory.FileSystem,
        permission: ToolPermissionLevel.Low,
        safety: ToolSafetyFlags.ReadOnly | ToolSafetyFlags.ConcurrencySafe,
        SortOrder = 74)]
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
