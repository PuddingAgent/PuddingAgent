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
        description: "在 HEAD 创建轻量标签（tag），或列出仓库中的所有标签。何时用：为发布版本或里程碑打标（如 v1.0.0），或查看仓库已有标签。怎么用/坑：Name 为空时列出全部标签，非空时在 HEAD 创建轻量标签；标签名重复会失败；轻量标签不含打标者信息与消息，需要附注标签时需另行扩展。",
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
                    ?? HostFileToolPaths.ResolveWorkspaceRoot(context.WorkingDirectory);

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
