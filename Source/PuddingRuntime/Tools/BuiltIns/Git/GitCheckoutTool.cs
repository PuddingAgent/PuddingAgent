using System.Text.Json;
using LibGit2Sharp;
using PuddingCode.Models;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.Tools
{
    public sealed record GitCheckoutArgs
    {
        [ToolParam("Git repository path (defaults to current working directory)")]
        public string? Path { get; init; }

        [ToolParam("Commit, branch or tag to check out")]
        public required string Target { get; init; }
    }

    [Tool(
        id: GitConstants.CheckoutId,
        name: "Git Checkout",
        description: "Check out a commit, branch or tag (detaches HEAD when checking out a non-branch)",
        category: ToolCategory.FileSystem,
        permission: ToolPermissionLevel.High,
        safety: ToolSafetyFlags.Destructive,
        SortOrder = 77)]
    public sealed class GitCheckoutTool : PuddingToolBase<GitCheckoutArgs>
    {
        protected override Task<ToolExecutionResult> ExecuteCoreAsync(
            GitCheckoutArgs args, ToolExecutionContext context, CancellationToken ct)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(args.Target))
                {
                    return Task.FromResult(ToolExecutionResult.Fail(
                        "Target is required."));
                }

                var repoPath = args.Path
                    ?? context.WorkingDirectory
                    ?? Directory.GetCurrentDirectory();

                using var repo = new Repository(repoPath);

                Commands.Checkout(repo, args.Target.Trim());

                return Task.FromResult(ToolExecutionResult.Ok(JsonSerializer.Serialize(new
                {
                    path = repoPath,
                    current_branch = repo.Head.FriendlyName,
                    target = args.Target.Trim()
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
