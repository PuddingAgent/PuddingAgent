using System.Text.RegularExpressions;

namespace PuddingToolGuidance.Rules;

/// <summary>
/// 本机工具链别名提示（P1）。
/// <para>
/// 判据来自**本机实测**而不是通用知识：在这台 Windows 上 <c>python</c> / <c>python3</c> 指向应用商店
/// 占位符，运行会弹出 Store 或直接失败。正确入口是 <c>py -3</c>。
/// 这类规则天然是环境绑定的 —— 环境变了就该删，所以文案里写明「本机」，避免被当成通用真理。
/// </para>
/// </summary>
public static class HostToolingRules
{
    private static readonly Regex PythonAlias = new(
        @"(?:^|[&|(]\s*)(?<exe>python3?)(?:\.exe)?(?=\s|$|[""'])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static IReadOnlyList<IGuidanceRule> All { get; } =
    [
        new PythonAliasRule()
    ];

    /// <summary><c>python</c> / <c>python3</c> 在本机是商店占位符。</summary>
    private sealed class PythonAliasRule : IGuidanceRule
    {
        public string Id => "host.python_alias_is_store_stub";

        public GuidanceAdvice? Evaluate(GuidanceInput input)
        {
            if (string.IsNullOrWhiteSpace(input.Command))
                return null;

            var match = PythonAlias.Match(input.Command);
            if (!match.Success)
                return null;

            var exe = match.Groups["exe"].Value.ToLowerInvariant();

            return new GuidanceAdvice(Id,
                $"本机的 `{exe}` 是应用商店占位符。改用 `py -3`，并设置 `PYTHONIOENCODING=utf-8` 以正确输出中文。");
        }
    }
}
