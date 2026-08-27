using System.Text.Json;
using LibGit2Sharp;
using PuddingCode.Models;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.Tools
{
    public sealed record GitCloneArgs
    {
        [ToolParam("Remote repository URL to clone")]
        public required string Url { get; init; }

        [ToolParam("Local directory path where the repository will be cloned")]
        public required string LocalPath { get; init; }

        [ToolParam("Branch to check out after clone (optional, default: remote HEAD)")]
        public string? Branch { get; init; }
    }

    [Tool(
        id: GitConstants.CloneId,
        name: "Git Clone",
        description: "将远程 Git 仓库克隆（clone）到本地目录。何时用：第一次把远程仓库拉到本地开始工作。怎么用/坑：Url 与 LocalPath 必填，LocalPath 必须不存在或为空目录；Branch 可选、缺省检出差远端 HEAD；需要网络与远程凭据，失败常见于网络不通、凭据不足或 URL 错误；克隆包含完整历史，大仓库耗时较长。",
        category: ToolCategory.FileSystem,
        permission: ToolPermissionLevel.Low,
        safety: ToolSafetyFlags.RequiresNetwork | ToolSafetyFlags.ConcurrencySafe,
        SortOrder = 71)]
    // 2026-08-28 裁定：git_clone 为创建型无损写（目标目录必须为空，不覆盖既有数据）；RequiresNetwork 现行不触发门禁（用户原则：仅直接损坏/泄露用户数据需门禁）
    public sealed class GitCloneTool : PuddingToolBase<GitCloneArgs>
    {
        protected override Task<ToolExecutionResult> ExecuteCoreAsync(
            GitCloneArgs args, ToolExecutionContext context, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(args.Url))
                return Task.FromResult(ToolExecutionResult.Fail("Url must not be empty."));

            if (string.IsNullOrWhiteSpace(args.LocalPath))
                return Task.FromResult(ToolExecutionResult.Fail("LocalPath must not be empty."));

            try
            {
                var options = new CloneOptions();
                if (!string.IsNullOrWhiteSpace(args.Branch))
                    options.BranchName = args.Branch;

                var localPath = Repository.Clone(args.Url, args.LocalPath, options);

                return Task.FromResult(ToolExecutionResult.Ok(JsonSerializer.Serialize(new
                {
                    url = args.Url,
                    path = localPath,
                    branch = options.BranchName,
                    cloned = true
                })));
            }
            catch (LibGit2SharpException ex)
            {
                return Task.FromResult(ToolExecutionResult.Fail(
                    $"Git clone failed: {ex.Message}"));
            }
        }
    }
}
