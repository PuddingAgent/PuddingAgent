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
        description: "Stage file paths into the Git index",
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
                    ?? context.WorkingDirectory
                    ?? Directory.GetCurrentDirectory();

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
