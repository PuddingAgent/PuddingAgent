using System.Text.Json;
using LibGit2Sharp;
using PuddingCode.Models;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.Tools
{
    public sealed record GitLogArgs
    {
        [ToolParam("Git repository path (defaults to current working directory)")]
        public string? Path { get; init; }

        [ToolParam("Maximum number of commits to return (default 20, max 100)")]
        public int MaxCount { get; init; } = 20;

        [ToolParam("Branch name (default: current branch)")]
        public string? Branch { get; init; }

        [ToolParam("Filter by file path")]
        public string? FilePath { get; init; }
    }

    [Tool(
        id: GitConstants.LogId,
        name: "Git Log",
        description: "显示提交历史/日志（git log），含作者、日期和消息。何时用：了解最近的提交、定位某次变更的 SHA、按分支或文件过滤历史。怎么用/坑：MaxCount 默认 20、上限 100；数量被夹取或历史没取完时回执带 truncated 与 notice；Branch 缺省为当前分支，**指定的分支不存在会直接失败**（不静默回落）；FilePath 支持相对仓库根的路径或绝对路径；只读操作、并发安全。",
        category: ToolCategory.FileSystem,
        permission: ToolPermissionLevel.Low,
        safety: ToolSafetyFlags.ReadOnly | ToolSafetyFlags.ConcurrencySafe,
        SortOrder = 61)]
    // 2026-08-28 裁定（依据用户 2026-08-28 指示）：git_log 纯本地只读查询（ReadOnly 标注），不写/删任何数据，Low+ReadOnly|ConcurrencySafe 免审直通
    public sealed class GitLogTool : PuddingToolBase<GitLogArgs>
    {
        /// <summary>max_count 的硬上限。回执里的建议文案必须与它同源，否则会出现「叫人调到 200」这类做不到的建议。</summary>
        internal const int MaxCountCeiling = 100;

        protected override Task<ToolExecutionResult> ExecuteCoreAsync(
            GitLogArgs args, ToolExecutionContext context, CancellationToken ct)
        {
            var repoPath = args.Path
                ?? HostFileToolPaths.ResolveWorkspaceRoot(context.WorkingDirectory);

            using var repo = new Repository(repoPath);

            // 2026-11-26：修三处「回执说的和实际做的不是一回事」（同一段代码里并存，一并处理）。
            // ① FilePath 声明了参数、写进了工具描述，却从未被读取 ⇒ 传了过滤条件仍拿到整支分支历史；
            // ② 分支名不存在时 Branches[...] 返回 null，旧代码静默回落到当前分支，回执里 branch 还写着 HEAD；
            // ③ 数量被截断时没有任何信号 ⇒ 无法区分「到上限了」与「历史就这么长」，与 list_dir 的 MaxEntriesNotice 同类。
            var requestedCount = args.MaxCount;
            var count = Math.Clamp(requestedCount, 1, MaxCountCeiling);

            var filter = new CommitFilter
            {
                SortBy = CommitSortStrategies.Topological | CommitSortStrategies.Time
            };

            // ④ 回执的 branch 必须是「实际查询的那一支」，不是当前 HEAD —— 否则指定分支查出的历史
            //    会被自己的回执标成另一支（2026-11-26 运行时实测：branch=feature/r1-xss-fix 拿到该分支
            //    tip e59b9c0，回执 branch 却写 master）。
            string? queriedBranch = null;
            if (!string.IsNullOrWhiteSpace(args.Branch))
            {
                var branch = repo.Branches[args.Branch];
                if (branch is null)
                {
                    return Task.FromResult(ToolExecutionResult.Fail(
                        $"Branch '{args.Branch}' not found. Use git_branch_list to see the branches that exist."));
                }

                filter.IncludeReachableFrom = branch.Tip;
                // FriendlyName 是 git 的规范名：Windows 上传入 "MASTER" 会被解析成 master，
                // 回执跟着规范名走，调用方才能确认自己问的是哪一支。
                queriedBranch = branch.FriendlyName;
            }

            var filePath = NormalizeFilterPath(repo, args.FilePath);

            // 多取一条判定截断：比「是否还有下一条」走完整条历史便宜得多。
            // LogEntry 不是 Commit 的子类（它持有一个 Commit），所以要显式投影，
            // 两个 QueryBy 重载才能在同一个变量里汇合。
            var query = filePath is null
                ? repo.Commits.QueryBy(filter).AsEnumerable()
                : repo.Commits.QueryBy(filePath, filter).Select(e => e.Commit);
            var probe = query.Take(count + 1).ToList();
            var truncated = probe.Count > count;
            var commits = truncated ? probe.Take(count).ToList() : probe;

            var entries = commits.Select(c => new
            {
                sha = c.Sha,
                author = c.Author.Name,
                email = c.Author.Email,
                date = c.Author.When.ToString("o"),
                message = c.MessageShort
            }).ToList();

            return Task.FromResult(ToolExecutionResult.Ok(JsonSerializer.Serialize(new
            {
                path = repoPath,
                branch = queriedBranch ?? GitHeadIdentity.Describe(repo),
                file_path = filePath,
                count = entries.Count,
                truncated,
                notice = BuildNotice(requestedCount, count, truncated),
                commits = entries
            })));
        }

        /// <summary>
        /// 把调用方给的文件过滤路径规整成 git 认的仓库相对形式（正斜杠）。
        /// 绝对路径按仓库工作目录换算；换算后仍在仓库外则原样返回——LibGit2Sharp 会得到一个
        /// 匹配不到任何提交的 pathspec，回执里 count=0 与 file_path 原值足以让调用方看出没匹配上。
        /// </summary>
        private static string? NormalizeFilterPath(Repository repo, string? filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return null;

            var candidate = filePath.Trim();
            if (Path.IsPathRooted(candidate))
            {
                var root = repo.Info.WorkingDirectory;
                if (!string.IsNullOrEmpty(root))
                {
                    var relative = Path.GetRelativePath(root, candidate);
                    if (!relative.StartsWith("..", StringComparison.Ordinal))
                        candidate = relative;
                }
            }

            return candidate.Replace('\\', '/');
        }

        /// <summary>
        /// 只在「调用方要的和拿到的不是一回事」时才产出说明；正常查询返回 null，避免给每次调用加噪声。
        /// </summary>
        private static string? BuildNotice(int requested, int applied, bool truncated)
        {
            // 夹取有两个方向：削到上限（requested > applied）与抬到下限（requested < applied）。
            // 到达上限后「raise max_count」是做不到的事 —— 上限就是 100，再写多大的数还是 100，
            // 这条建议会把调用方领进死胡同；只有抬到下限时「raise max_count」才真的有效。
            var clampedAtCeiling = requested > applied;

            var parts = new List<string>();
            if (requested != applied)
            {
                parts.Add(
                    $"max_count={requested} was clamped to the supported range 1..{MaxCountCeiling} (applied {applied})");
            }

            if (truncated)
            {
                var advice = clampedAtCeiling
                    ? $"narrow the query with branch/file_path (max_count is already at the {MaxCountCeiling} ceiling)"
                    : "raise max_count or narrow the query with branch/file_path";
                parts.Add(
                    $"showing the {applied} newest commit(s); older history was not returned — {advice}");
            }

            return parts.Count == 0 ? null : string.Join("; ", parts);
        }
    }
}
