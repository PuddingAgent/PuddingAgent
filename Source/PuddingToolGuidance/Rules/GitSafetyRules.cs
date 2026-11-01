using System.Text.RegularExpressions;

namespace PuddingToolGuidance.Rules;

/// <summary>
/// Git 与工具链的安全提示（P1）。
/// <para>
/// 这三条的共同点：**不会报错，只会静默地做错事** —— 暂存了不该暂存的文件、改动被锁在 stash 里、
/// 命令跑的是商店占位符。它们比「命令直接失败」危险得多，因为失败会让人停下，静默不会。
/// </para>
/// <para>
/// 全部只看命令字面量，不读文件系统：规则要能在热路径上以纯函数方式判定。
/// 「目录里到底有没有临时文件」这件事交给提示去问人（见文案），而不是规则自己去查盘。
/// </para>
/// </summary>
public static class GitSafetyRules
{
    private static readonly Regex GitAddAll = new(
        @"git\s+add\s+(?:-A|--all|\.)(?:\s|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex GitStash = new(
        @"git\s+stash\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static IReadOnlyList<IGuidanceRule> All { get; } =
    [
        new GitAddAllRule(),
        new GitStashChainRule()
    ];

    /// <summary>
    /// <c>git add -A</c> / <c>git add .</c>：会把目录里未跟踪的临时产物一并暂存。
    /// 2026-11-01 实测：一次 <c>git add -A Source</c> 把 helper 脚本生成的 <c>*.tail.txt</c> 收进了索引。
    /// </summary>
    private sealed class GitAddAllRule : IGuidanceRule
    {
        public string Id => "git.add_all_may_include_temp_artifacts";

        public GuidanceAdvice? Evaluate(GuidanceInput input)
        {
            if (string.IsNullOrWhiteSpace(input.Command) || !GitAddAll.IsMatch(input.Command))
                return null;

            return new GuidanceAdvice(Id,
                "`git add -A` / `git add .` 会把目录里未跟踪的临时产物（如 temp/*.txt、*.bak）一并暂存。" +
                "改用显式路径，或先 `git status --porcelain` 核对一遍。");
        }
    }

    /// <summary>
    /// <c>git stash</c> 与后续动作挂在同一条 <c>&amp;&amp;</c> 链上：链一断，pop 就不执行，
    /// 改动被锁在 stash 里而你以为已经恢复。
    /// 2026-11-01 实测：一条链中间的命令报错，stash pop 没跑，差点丢掉两处 csproj 改动。
    /// </summary>
    private sealed class GitStashChainRule : IGuidanceRule
    {
        public string Id => "git.stash_recovery_on_same_chain";

        public GuidanceAdvice? Evaluate(GuidanceInput input)
        {
            if (string.IsNullOrWhiteSpace(input.Command))
                return null;

            if (!GitStash.IsMatch(input.Command))
                return null;

            // 单独一条 git stash 没问题；和别的动作串在一条链上才有「链断则 pop 不执行」的风险。
            if (!input.Command.Contains("&&", StringComparison.Ordinal))
                return null;

            return new GuidanceAdvice(Id,
                "`git stash` 的恢复（pop）不要和其它动作挂在同一条 `&&` 链上 —— " +
                "链中间一旦报错，pop 不执行，改动会被锁在 stash 里。分两次调用。");
        }
    }
}
