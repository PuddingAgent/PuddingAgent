using System.Text.RegularExpressions;

namespace PuddingToolGuidance.Rules;

/// <summary>
/// Shell 方言误用规则组。
/// <para>
/// 全部判据都来自**命令字面量 + shell 方言**，不依赖执行结果 —— 因为这类错误往往「成功」返回
/// （cmd 里写 <c>tail</c> 报错，但 <c>findstr</c> 搜中文会安静地返回空），只看成功与否会漏。
/// </para>
/// </summary>
public static class ShellDialectRules
{
    /// <summary>cmd 不认识的 Unix 命令词。</summary>
    private static readonly string[] UnixCommands =
    [
        "tail", "head", "grep", "sed", "awk", "wc", "which", "touch", "chmod", "chown", "cat"
    ];

    /// <summary>cmd 不认识的 PowerShell cmdlet。</summary>
    private static readonly string[] PowerShellCmdlets =
    [
        "Remove-Item", "Get-ChildItem", "Get-Content", "Select-String", "Test-Path",
        "Write-Output", "Measure-Object", "Set-Content"
    ];

    private static readonly Regex BashVariable = new(@"\$[A-Za-z_]\w*", RegexOptions.CultureInvariant);

    public static IReadOnlyList<IGuidanceRule> All { get; } =
    [
        new UnixCommandInCmdRule(),
        new PowerShellCmdletInCmdRule(),
        new BashVariableOutsideBashRule(),
        new FindstrWithCjkRule(),
        new CmdAmpersandChainRule(),
        // 低效循环检测与方言无关，对所有工具生效。
        new RepeatedFailureRule()
    ];

    private static bool IsCmd(string? dialect) =>
        string.Equals(dialect, "cmd", StringComparison.OrdinalIgnoreCase);

    private static bool IsBashLike(string? dialect) =>
        string.Equals(dialect, "bash", StringComparison.OrdinalIgnoreCase)
        || string.Equals(dialect, "wsl", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsCjk(string text)
    {
        foreach (var ch in text)
        {
            if (ch is >= '\u4E00' and <= '\u9FFF')
                return true;
        }

        return false;
    }

    /// <summary>命令文本里是否出现某个独立词（按非字母数字边界判定，避免 <c>cat</c> 命中 <c>concatenate</c>）。</summary>
    private static bool ContainsWord(string command, string word) =>
        Regex.IsMatch(command, $@"(^|[^\w-]){Regex.Escape(word)}([^\w-]|$)", RegexOptions.CultureInvariant);

    /// <summary>cmd 里写了 Unix 命令（<c>tail</c> / <c>grep</c> / <c>sed</c> …）。</summary>
    private sealed class UnixCommandInCmdRule : IGuidanceRule
    {
        public string Id => "shell.unix_command_in_cmd";

        public GuidanceAdvice? Evaluate(GuidanceInput input)
        {
            if (!IsCmd(input.ShellDialect) || string.IsNullOrWhiteSpace(input.Command))
                return null;

            foreach (var word in UnixCommands)
            {
                if (!ContainsWord(input.Command, word))
                    continue;

                // `findstr` 本身是 cmd 命令，不会被上面的词表命中；这里只处理真的 Unix 命令。
                return new GuidanceAdvice(Id,
                    $"cmd 没有 `{word}`。改用 shell=powershell，或用 cmd 自带的等价命令（findstr / type）。");
            }

            return null;
        }
    }

    /// <summary>cmd 里写了 PowerShell cmdlet。</summary>
    private sealed class PowerShellCmdletInCmdRule : IGuidanceRule
    {
        public string Id => "shell.pwsh_cmdlet_in_cmd";

        public GuidanceAdvice? Evaluate(GuidanceInput input)
        {
            if (!IsCmd(input.ShellDialect) || string.IsNullOrWhiteSpace(input.Command))
                return null;

            foreach (var cmdlet in PowerShellCmdlets)
            {
                if (!ContainsWord(input.Command, cmdlet))
                    continue;

                return new GuidanceAdvice(Id,
                    $"cmd 不认识 PowerShell 的 `{cmdlet}`。改用 shell=powershell（或 shell=pwsh）。");
            }

            return null;
        }
    }

    /// <summary>非 bash 方言里用了 Bash 变量语法。</summary>
    private sealed class BashVariableOutsideBashRule : IGuidanceRule
    {
        public string Id => "shell.bash_variable_outside_bash";

        public GuidanceAdvice? Evaluate(GuidanceInput input)
        {
            if (string.IsNullOrWhiteSpace(input.Command) || IsBashLike(input.ShellDialect))
                return null;

            // `$env:FOO`（pwsh）与 `$FOO`（bash）形状不同，先排除 pwsh 的驱动式写法。
            if (input.Command.Contains("$env:", StringComparison.OrdinalIgnoreCase))
                return null;

            if (!BashVariable.IsMatch(input.Command))
                return null;

            return new GuidanceAdvice(Id,
                "这是 Bash 变量语法。cmd 用 %VAR%，PowerShell 用 $env:VAR，或显式 shell=wsl。");
        }
    }

    /// <summary>findstr 配中文关键词 —— 对 UTF-8 无 BOM 文件恒不命中，会误判「不存在」。</summary>
    private sealed class FindstrWithCjkRule : IGuidanceRule
    {
        public string Id => "encoding.findstr_with_cjk";

        public GuidanceAdvice? Evaluate(GuidanceInput input)
        {
            if (string.IsNullOrWhiteSpace(input.Command))
                return null;

            if (!ContainsWord(input.Command, "findstr"))
                return null;

            if (!ContainsCjk(input.Command))
                return null;

            return new GuidanceAdvice(Id,
                "findstr 对 UTF-8 无 BOM 的中文恒不命中，空结果不代表「不存在」。改用 py -3 或 [IO.File]::ReadAllLines(..., UTF8)。");
        }
    }

    /// <summary>
    /// cmd 里多个 <c>&amp;</c> 串联多个子命令 —— 是**串行**，不是并行。
    /// <para>
    /// 只在 <c>&amp;</c> 数量足够多（≥3）时提示：两三个 <c>&amp;&amp;</c> 做顺序门禁是常见正常写法，
    /// 提示它只会变成噪声。
    /// </para>
    /// </summary>
    private sealed class CmdAmpersandChainRule : IGuidanceRule
    {
        public string Id => "shell.cmd_ampersand_is_sequential";

        public GuidanceAdvice? Evaluate(GuidanceInput input)
        {
            if (!IsCmd(input.ShellDialect) || string.IsNullOrWhiteSpace(input.Command))
                return null;

            var count = input.Command.Count(c => c == '&');
            if (count < 3)
                return null;

            return new GuidanceAdvice(Id,
                "cmd 的 `&` 是串行执行（不是并行）。并行请分多次 terminal_start，或改用 PowerShell 的 Start-Job。");
        }
    }
}
