using System.Text.Json;
using LibGit2Sharp;
using PuddingCode.Models;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.Tools
{
    public sealed record GitTagArgs
    {
        [ToolParam("Git repository path (defaults to current working directory)")]
        public string? Path { get; init; }

        [ToolParam("Tag name to create; when empty, lists all tags")]
        public string? Name { get; init; }
    }

    [Tool(
        id: GitConstants.TagId,
        name: "Git Tag",
        description: "Create a lightweight tag at HEAD, or list all tags in the repository",
        category: ToolCategory.FileSystem,
        permission: ToolPermissionLevel.Medium,
        safety: ToolSafetyFlags.None,
        SortOrder = 75)]
    public sealed class GitTagTool : PuddingToolBase<GitTagArgs>
    {
        protected override Task<ToolExecutionResult> ExecuteCoreAsync(
            GitTagArgs args, ToolExecutionContext context, CancellationToken ct)
        {
            try
            {
                var repoPath = args.Path
                    ?? context.WorkingDirectory
                    ?? Directory.GetCurrentDirectory();

                using var repo = new Repository(repoPath);

                if (string.IsNullOrWhiteSpace(args.Name))
                {
                    var tags = repo.Tags
                        .Select(t => new
                        {
                            name = t.FriendlyName,
                            commit = t.Target.Sha
                        })
                        .ToList();

                    return Task.FromResult(ToolExecutionResult.Ok(JsonSerializer.Serialize(new
                    {
                        path = repoPath,
                        count = tags.Count,
                        tags
                    })));
                }

                repo.ApplyTag(args.Name.Trim());

                return Task.FromResult(ToolExecutionResult.Ok(JsonSerializer.Serialize(new
                {
                    path = repoPath,
                    tag = args.Name.Trim(),
                    created = true
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
