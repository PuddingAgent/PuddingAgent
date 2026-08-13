using System.Text.Json;
using LibGit2Sharp;
using PuddingCode.Models;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.Tools
{
    public sealed record GitMergeArgs
    {
        [ToolParam("Git repository path (defaults to current working directory)")]
        public string? Path { get; init; }

        [ToolParam("Name of the branch to merge into the current branch")]
        public required string Branch { get; init; }
    }

    [Tool(
        id: GitConstants.MergeId,
        name: "Git Merge",
        description: "将指定分支合并（merge）到当前分支。何时用：把已完成的分支（如 feature）合入主干，或把他人改动并入当前分支。怎么用/坑：Branch 必填且须存在；合并前建议先 commit/stash 未提交改动，保证工作区干净；若产生冲突，结果 status 为 Conflicts，需手动解决后再次提交；快进场景不会产生 merge commit。Merge the given branch into the current branch; use to integrate a finished feature branch or sync others' work; Branch is required, keep the working tree clean first, and conflicts (status=Conflicts) must be resolved manually",
        category: ToolCategory.FileSystem,
        permission: ToolPermissionLevel.High,
        safety: ToolSafetyFlags.Destructive,
        SortOrder = 73)]
    public sealed class GitMergeTool : PuddingToolBase<GitMergeArgs>
    {
        protected override Task<ToolExecutionResult> ExecuteCoreAsync(
            GitMergeArgs args, ToolExecutionContext context, CancellationToken ct)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(args.Branch))
                {
                    return Task.FromResult(ToolExecutionResult.Fail(
                        "Branch is required."));
                }

                var repoPath = args.Path
                    ?? HostFileToolPaths.ResolveWorkspaceRoot(context.WorkingDirectory);

                using var repo = new Repository(repoPath);

                var sig = repo.Config.BuildSignature(DateTimeOffset.UtcNow);
                if (sig is null)
                {
                    return Task.FromResult(ToolExecutionResult.Fail(
                        "Unable to build a merge signature. Configure user.name and user.email in Git config."));
                }

                var branch = repo.Branches[args.Branch.Trim()];
                if (branch is null)
                {
                    return Task.FromResult(ToolExecutionResult.Fail(
                        $"Branch '{args.Branch}' was not found in this repository."));
                }

                var result = repo.Merge(branch, sig, new MergeOptions());

                return Task.FromResult(ToolExecutionResult.Ok(JsonSerializer.Serialize(new
                {
                    path = repoPath,
                    merged_branch = args.Branch,
                    status = result.Status.ToString()
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
