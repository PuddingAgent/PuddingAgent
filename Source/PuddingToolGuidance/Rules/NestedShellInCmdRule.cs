using System.Text.RegularExpressions;

namespace PuddingToolGuidance.Rules;

/// <summary>
/// cmd 里嵌套调用别的 shell（<c>powershell</c> / <c>pwsh</c> / <c>wsl</c> / <c>bash</c>）。
/// <para>
/// 现象与代价：在 <c>shell=cmd</c> 下写 <c>powershell -NoProfile -Command "..."</c> 或 <c>wsl bash -lc "..."</c>，
/// 会多一层引号转义、多一层输出编码转换（GBK→UTF-8 再回来），并且退出码语义也变了
/// （嵌套 shell 的失败常常只体现为一串文本）。而这些场景本来只要换一个<b>参数</b>就能直接进对应方言。
/// </para>
/// <para>
/// 判据刻意收紧：只认**命令位置**（行首，或 <c>&amp;</c> / <c>&amp;&amp;</c> / <c>|</c> / <c>(</c> 之后）上的 shell 名，
/// 避免把 <c>findstr bash file.txt</c>、<c>echo powershell</c> 这类「只是提到了名字」的命令误判为嵌套调用。
/// </para>
/// </summary>
public sealed class NestedShellInCmdRule : IGuidanceRule
{
    private static readonly Regex NestedShell = new(
        @"(?:^|[&|(]\s*)(?<shell>powershell|pwsh|wsl|bash)(?:\.exe)?(?=\s|$|[""'])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public string Id => "shell.nested_shell_in_cmd";

    public GuidanceAdvice? Evaluate(GuidanceInput input)
    {
        if (!string.Equals(input.ShellDialect, "cmd", StringComparison.OrdinalIgnoreCase))
            return null;

        if (string.IsNullOrWhiteSpace(input.Command))
            return null;

        var match = NestedShell.Match(input.Command);
        if (!match.Success)
            return null;

        var shell = match.Groups["shell"].Value.ToLowerInvariant();

        return new GuidanceAdvice(Id,
            $"cmd 里嵌套调用了 `{shell}`。直接把 shell 参数换成 `powershell` 或 `wsl` 即可 —— " +
            "省掉一层引号转义与编码转换，退出码也是真实的。");
    }
}
