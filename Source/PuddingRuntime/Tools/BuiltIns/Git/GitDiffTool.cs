using System.Text;
using LibGit2Sharp;
using PuddingCode.Models;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.Tools
{
    public sealed record GitDiffArgs
    {
        [ToolParam("Git repository path (defaults to current working directory)")]
        public string? Path { get; init; }

        [ToolParam("Compare staged changes instead of working directory")]
        public bool Staged { get; init; }

        [ToolParam("Specific file paths to diff (optional, default: all)")]
        public string[]? Files { get; init; }
    }

    [Tool(
        id: GitConstants.DiffId,
        name: "Git Diff",
        description: "显示工作区与索引之间、或索引与 HEAD 之间的变更（diff）。Show changes between the working tree and the index or between the index and HEAD",
        category: ToolCategory.FileSystem,
        permission: ToolPermissionLevel.Low,
        safety: ToolSafetyFlags.ReadOnly | ToolSafetyFlags.ConcurrencySafe,
        SortOrder = 62)]
    public sealed class GitDiffTool : PuddingToolBase<GitDiffArgs>
    {
        protected override Task<ToolExecutionResult> ExecuteCoreAsync(
            GitDiffArgs args, ToolExecutionContext context, CancellationToken ct)
        {
            var repoPath = args.Path
                ?? HostFileToolPaths.ResolveWorkspaceRoot(context.WorkingDirectory);

            using var repo = new Repository(repoPath);

            Patch patch;
            if (args.Staged)
            {
                patch = args.Files is { Length: > 0 }
                    ? repo.Diff.Compare<Patch>(repo.Head.Tip?.Tree, DiffTargets.Index, args.Files)
                    : repo.Diff.Compare<Patch>(repo.Head.Tip?.Tree, DiffTargets.Index);
            }
            else
            {
                patch = args.Files is { Length: > 0 }
                    ? repo.Diff.Compare<Patch>(args.Files)
                    : repo.Diff.Compare<Patch>();
            }

            var sb = new StringBuilder();
            sb.Append("# Changes in ");
            sb.AppendLine(repoPath);
            sb.Append("## ");
            sb.Append(args.Staged ? "Staged" : "Working tree");
            sb.AppendLine(" changes vs HEAD");

            foreach (var c in patch)
            {
                sb.Append("### ");
                sb.Append(c.Status.ToString());
                sb.Append(": ");
                sb.AppendLine(c.Path);
                sb.AppendLine(c.Patch);
            }

            return Task.FromResult(ToolExecutionResult.Ok(sb.ToString()));
        }
    }
}
