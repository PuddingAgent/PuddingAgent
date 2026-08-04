using System.Text.Json;
using LibGit2Sharp;
using PuddingCode.Models;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.Tools
{
    public sealed record GitFetchArgs
    {
        [ToolParam("Git repository path (defaults to current working directory)")]
        public string? Path { get; init; }

        [ToolParam("Remote name (default: origin)")]
        public string? Remote { get; init; }
    }

    [Tool(
        id: GitConstants.FetchId,
        name: "Git Fetch",
        description: "Fetch updates from a remote repository without merging",
        category: ToolCategory.FileSystem,
        permission: ToolPermissionLevel.Medium,
        safety: ToolSafetyFlags.RequiresNetwork | ToolSafetyFlags.ConcurrencySafe,
        SortOrder = 79)]
    public sealed class GitFetchTool : PuddingToolBase<GitFetchArgs>
    {
        protected override Task<ToolExecutionResult> ExecuteCoreAsync(
            GitFetchArgs args, ToolExecutionContext context, CancellationToken ct)
        {
            var repoPath = args.Path
                ?? context.WorkingDirectory
                ?? Directory.GetCurrentDirectory();

            try
            {
                using var repo = new Repository(repoPath);

                Commands.Fetch(repo, args.Remote ?? "origin", new string[0], new FetchOptions(), null);

                return Task.FromResult(ToolExecutionResult.Ok(JsonSerializer.Serialize(new
                {
                    path = repoPath,
                    remote = args.Remote ?? "origin",
                    fetched = true
                })));
            }
            catch (LibGit2SharpException ex)
            {
                return Task.FromResult(ToolExecutionResult.Fail(
                    $"Git fetch failed: {ex.Message}"));
            }
            catch (ArgumentException ex)
            {
                return Task.FromResult(ToolExecutionResult.Fail(
                    $"Git fetch failed: {ex.Message}"));
            }
        }
    }
}
