using LibGit2Sharp;

namespace PuddingRuntime.Services.Tools
{
    /// <summary>
    /// HEAD 的身份描述（2026-11-26）。
    ///
    /// LibGit2Sharp 在游离 HEAD（detached）下把 <c>Head.FriendlyName</c> 报成字面量 <c>"(no branch)"</c>。
    /// 这个值既不是分支名、也不能当分支名用：
    /// - 报告类回执里它读起来像是「仓库没有分支」，把「HEAD 游离在某个提交上」这个唯一有用的事实藏掉了；
    /// - 推拉类工具会拿它拼出 <c>refs/heads/(no branch)</c>，失败信息于是指向一个根本不存在的分支。
    ///
    /// 所以两种用途分开表达：报告用 <see cref="Describe"/>，动作前取真实分支名用
    /// <see cref="TryResolveBranchName"/>。四处调用点（git_log / git_status / git_push / git_pull）
    /// 共用这一份判据，避免各自演变出不同的说法。
    /// </summary>
    internal static class GitHeadIdentity
    {
        private const int ShortShaLength = 7;

        /// <summary>
        /// 报告形态：在分支上返回规范分支名；游离 HEAD 返回 <c>detached at &lt;短 sha&gt;</c>。
        /// </summary>
        public static string Describe(Repository repo)
        {
            if (IsHeadSymbolic(repo))
                return repo.Head.FriendlyName;

            var sha = repo.Head.Tip?.Sha;
            return sha is null
                ? "detached (HEAD has no commit)"
                : $"detached at {Shorten(sha)}";
        }

        /// <summary>
        /// 动作形态：拿到可以写进 refspec 的真实分支名。
        ///
        /// 调用方显式给了分支名就直接用（哪怕当前 HEAD 游离——显式指定分支本来就是合法用法，保持旧行为）；
        /// 没给且 HEAD 游离时失败关闭，而不是把 <c>(no branch)</c> 当分支名拼进 refspec。
        /// </summary>
        /// <returns>解析成功返回 true；失败时 <paramref name="error"/> 是可直接回给调用方的原因。</returns>
        public static bool TryResolveBranchName(
            Repository repo, string? requested, out string name, out string? error)
        {
            name = string.Empty;
            error = null;

            if (!string.IsNullOrWhiteSpace(requested))
            {
                name = requested.Trim();
                return true;
            }

            if (!IsHeadSymbolic(repo))
            {
                var sha = repo.Head.Tip?.Sha;
                var where = sha is null ? "detached" : $"detached at {Shorten(sha)}";
                error =
                    $"HEAD is {where}, so there is no local branch to use. " +
                    "Pass branch explicitly, or check out a branch first (git_branch_switch).";
                return false;
            }

            name = repo.Head.FriendlyName;
            return true;
        }

        /// <summary>
        /// HEAD 是不是**符号引用**（指向 <c>refs/heads/&lt;name&gt;</c>）。
        ///
        /// 为什么不用 <c>Repository.Info.IsHeadDetached</c>：该属性在「游离且 HEAD 没有提交」时报
        /// <c>false</c>（libgit2 的 head_detached 对 unborn HEAD 返回错误码而非 1），于是
        /// <see cref="Describe"/> 会掉进「在分支上」的支路，把 <c>(no branch)</c> 当分支名报出去——
        /// 正是本类型要消灭的那个字面量。改为直接看 HEAD 的引用形态：符号引用的 TargetIdentifier 形如
        /// <c>refs/heads/x</c>，直接引用（游离）是 40 位 sha。
        /// </summary>
        private static bool IsHeadSymbolic(Repository repo)
        {
            var head = repo.Refs.Head;
            var target = head?.TargetIdentifier;
            return target is not null && target.StartsWith("refs/", StringComparison.Ordinal);
        }

        private static string Shorten(string sha)
            => sha.Length <= ShortShaLength ? sha : sha.Substring(0, ShortShaLength);
    }
}
