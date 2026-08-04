using System.Text.Json;
using LibGit2Sharp;
using PuddingCode.Models;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.Tools
{
    public sealed record GitCommitArgs
    {
        [ToolParam("Git repository path (defaults to current working directory)")]
        public string? Path { get; init; }

        [ToolParam("Commit message")]
        public required string Message { get; init; }

        [ToolParam("Optional file paths to stage before committing")]
        public string[]? Files { get; init; }

        [ToolParam("Allow an empty commit when there are no staged changes (default false)")]
        public bool AllowEmpty { get; init; } = false;
    }

    [Tool(
        id: GitConstants.CommitId,
        name: "Git Commit",
        description: "Create a commit with the given message, optionally staging files first",
        category: ToolCategory.FileSystem,
        permission: ToolPermissionLevel.High,
        safety: ToolSafetyFlags.Destructive,
        SortOrder = 67)]
    public sealed class GitCommitTool : PuddingToolBase<GitCommitArgs>
    {
        protected override Task<ToolExecutionResult> ExecuteCoreAsync(
            GitCommitArgs args, ToolExecutionContext context, CancellationToken ct)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(args.Message))
                {
                    return Task.FromResult(ToolExecutionResult.Fail(
                        "Message is required."));
                }

                var repoPath = args.Path
                    ?? context.WorkingDirectory
                    ?? Directory.GetCurrentDirectory();

                using var repo = new Repository(repoPath);

                if (args.Files is { Length: > 0 })
                {
                    Commands.Stage(repo, args.Files);
                }

                var signature = repo.Config.BuildSignature(DateTimeOffset.Now);
                if (signature is null)
                {
                    return Task.FromResult(ToolExecutionResult.Fail(
                        "Unable to build a commit signature. Configure user.name and user.email in Git config."));
                }

                var options = new CommitOptions
                {
                    AllowEmptyCommit = args.AllowEmpty
                };

                var commit = repo.Commit(args.Message.Trim(), signature, signature, options);

                return Task.FromResult(ToolExecutionResult.Ok(JsonSerializer.Serialize(new
                {
                    path = repoPath,
                    sha = commit.Sha,
                    message = commit.MessageShort
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
