using System.Text.RegularExpressions;

namespace PuddingRuntime.Services;

public interface ITerminalCommandPolicy
{
    TerminalCommandDecision EvaluateInvariant(string command);
    TerminalCommandDecision Evaluate(string command, bool isYoloMode);
    void EnsureInvariantAllowed(string command);
    void EnsureAllowed(string command, bool isYoloMode);
}

public sealed record TerminalCommandDecision(
    bool Allowed,
    TerminalCommandDenyReason DenyReason = TerminalCommandDenyReason.None,
    string? Message = null,
    string? FirstWord = null,
    string? MatchedPattern = null,
    bool PermissionChecksBypassed = false);

public enum TerminalCommandDenyReason
{
    None,
    EmptyCommand,
    CommandNotAllowlisted,
    DangerousPattern,
    ProcessTerminationCommand,
}

/// <summary>
/// 终端命令策略——Normal 模式执行命令白名单与危险模式拦截；
/// YOLO 模式跳过权限策略，但仍执行宿主安全不变量。
///
/// Shell 安全边界：
///   1. 进程终止命令属于不可绕过的宿主安全不变量；只能通过 terminal_cancel
///      终止由当前会话创建并持有稳定 job id 的进程。
///   2. 模板能力与运行时授权由 ToolPermissionPolicyService + AgentFirewall 负责。
///   3. Normal 模式下，命令白名单只允许 DefaultWhitelist 中的命令前缀。
///   4. Normal 模式下，危险模式拦截会拒绝已知高危命令片段。
///   5. YOLO 仅跳过权限检查，不跳过宿主安全不变量。
/// </summary>
public sealed class DefaultTerminalCommandPolicy : ITerminalCommandPolicy
{
    public static readonly DefaultTerminalCommandPolicy Instance = new();

    /// <summary>第二层：默认允许的安全命令前缀白名单。</summary>
    public static readonly string[] DefaultWhitelist =
    [
        // .NET / 主流运行时、包管理器与前端工具链
        "dotnet", "git", "python", "python3", "node", "npm", "npx", "pnpm", "yarn", "bun", "docker",
        "cargo", "go", "java", "javac", "mvn", "gradle", "pip", "pip3", "conda",
        // 文件系统与文本查看（非破坏形态）
        "ls", "dir", "cat", "echo", "mkdir", "rmdir", "type",
        "cp", "mv", "copy", "move", "del", "erase", "tar", "zip", "unzip",
        // 网络与诊断
        "curl", "wget", "ping", "nslookup", "ipconfig", "ifconfig",
        "find", "grep", "findstr", "rg", "tail", "head", "wc",
        // Windows shell 内建与目录导航（非破坏形态）
        "cd", "pushd", "popd", "set", "setlocal", "endlocal", "cls", "where", "ver", "timeout",
        // Shell 宿主与注册表查询（破坏形态由 DangerousPatterns 兜底）
        "pwsh", "powershell", "cmd", "reg",
        // 进程 / 系统查看
        "chmod", "chown", "whoami", "hostname", "date", "time",
        "ps", "top", "df", "du", "netstat", "ss",
    ];

    /// <summary>第三层：危险模式正则拦截列表。</summary>
    public static readonly Regex[] DangerousPatterns =
    [
        // rm -rf / 或 rm --recursive /
        new(@"rm\s+(-rf?|--recursive)\s+.*/", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        // dd if= 磁盘操作
        new(@"dd\s+if=", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        // curl ... | sh / bash 管道执行
        new(@"curl.*\|\s*(ba)?sh", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        // > /dev/sdX 写入磁盘设备
        new(@">\s*/dev/sd[a-z]", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        // mkfs.* 格式化
        new(@"mkfs\.", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        // chmod 777 / 等危险权限
        new(@"chmod\s+777\s+/", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        // fork bomb
        new(@":\(\)\s*\{", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        // wget ... | sh
        new(@"wget.*\|\s*(ba)?sh", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        // 删除系统目录
        new(@"rm\s+(-rf?|--recursive).*/etc|.*/var|.*/usr|.*/boot", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        // PowerShell 危险命令
        new(@"Remove-Item\s+-Recurse\s+-Force\s+[A-Z]:\\", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        // Windows 磁盘/分区格式化（直接执行或经 pwsh/cmd -Command 嵌套绕行）
        new(@"\bformat\s+[A-Za-z]:", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        // 注册表删除键：reg delete（reg query 保持允许）
        new(@"\breg\s+delete\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        // Windows 递归删除：del /s、erase /s、rmdir /s（覆盖 del /s /q 形态）
        new(@"\b(del|erase|rmdir)\b[^\r\n]*?/s\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        // rm -rf 指向 Windows 盘符（经 pwsh/cmd 嵌套的绕行场景）
        new(@"\brm\s+(-rf?|--recursive)\s+[A-Za-z]:", RegexOptions.IgnoreCase | RegexOptions.Compiled),
    ];

    /// <summary>
    /// 不受 Normal/YOLO 模式影响的宿主安全不变量。
    /// 原始 shell 不拥有任意 OS 进程的生命周期；进程终止必须通过 terminal_cancel，
    /// 由 ITerminalProcessManager 按当前会话持有的 job id 执行。
    /// </summary>
    public static readonly Regex[] InvariantDenyPatterns =
    [
        // 只匹配命令起点或 shell 分隔符后的可执行位置，避免阻断
        // `rg "taskkill"` / `Select-String "Stop-Process"` 等诊断查询。
        new(
            @"(?:^|[\r\n;&|])\s*(?:&\s*)?[""']?(?:sudo\s+)?[""']?(?:taskkill|tskill)(?:\.exe)?\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(
            @"(?:^|[\r\n;&|])\s*(?:&\s*)?[""']?(?:Stop-Process)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(
            @"(?:^|[\r\n;&|])\s*(?:&\s*)?[""']?(?:sudo\s+)?[""']?(?:kill|killall|pkill)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled),
    ];

    public TerminalCommandDecision EvaluateInvariant(string command)
    {
        var executableText = MaskQuotedSeparators(command);
        foreach (var pattern in InvariantDenyPatterns)
        {
            if (pattern.IsMatch(executableText))
            {
                return new TerminalCommandDecision(
                    Allowed: false,
                    DenyReason: TerminalCommandDenyReason.ProcessTerminationCommand,
                    Message: "Raw process-termination commands are prohibited, including in YOLO mode. Use terminal_cancel with a job_id created by this session.",
                    MatchedPattern: pattern.ToString());
            }
        }

        return new TerminalCommandDecision(Allowed: true);
    }

    private static string MaskQuotedSeparators(string command)
    {
        var chars = command.ToCharArray();
        char? quote = null;
        var escaped = false;

        for (var i = 0; i < chars.Length; i++)
        {
            var current = chars[i];
            if (quote is null)
            {
                if (current is '"' or '\'')
                    quote = current;
                continue;
            }

            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (current is '\\' or '`')
            {
                escaped = true;
                continue;
            }

            if (current == quote)
            {
                quote = null;
                continue;
            }

            if (current is ';' or '|' or '&' or '\r' or '\n')
                chars[i] = ' ';
        }

        return new string(chars);
    }

    /// <summary>
    /// 判断命令是否允许执行。
    /// </summary>
    /// <param name="command">完整命令行。</param>
    /// <param name="isYoloMode">YOLO 模式下跳过命令权限策略，但不跳过宿主安全不变量。</param>
    public TerminalCommandDecision Evaluate(string command, bool isYoloMode)
    {
        var invariantDecision = EvaluateInvariant(command);
        if (!invariantDecision.Allowed)
            return invariantDecision;

        if (isYoloMode)
        {
            return new TerminalCommandDecision(
                Allowed: true,
                PermissionChecksBypassed: true);
        }

        var trimmed = command.TrimStart();
        if (trimmed.Length == 0)
        {
            return new TerminalCommandDecision(
                Allowed: false,
                DenyReason: TerminalCommandDenyReason.EmptyCommand,
                Message: "空命令不允许执行。");
        }

        // 提取第一个词作为命令名
        var firstWordEnd = trimmed.IndexOf(' ');
        var firstWord = firstWordEnd > 0 ? trimmed[..firstWordEnd] : trimmed;

        // 第二层：检查白名单——精确匹配，或仅接受 `<命令>.<扩展名>` 变体（git.exe、npx.cmd、pwsh.exe）。
        // 不用纯前缀匹配：避免 cmdkey/regedit/regsvr32/setx 等衍生命令被 cmd/reg/set 前缀误放行。
        var allowlisted = DefaultWhitelist.Any(w =>
            firstWord.Equals(w, StringComparison.OrdinalIgnoreCase) ||
            (firstWord.Length > w.Length
             && firstWord.StartsWith(w, StringComparison.OrdinalIgnoreCase)
             && firstWord[w.Length] == '.'));
        if (!allowlisted)
        {
            return new TerminalCommandDecision(
                Allowed: false,
                DenyReason: TerminalCommandDenyReason.CommandNotAllowlisted,
                Message: $"命令 '{firstWord}' 不在终端白名单中。允许的命令: {string.Join(", ", DefaultWhitelist.Take(10))}...",
                FirstWord: firstWord);
        }

        // 第三层：检查危险模式
        foreach (var pattern in DangerousPatterns)
        {
            if (pattern.IsMatch(command))
            {
                return new TerminalCommandDecision(
                    Allowed: false,
                    DenyReason: TerminalCommandDenyReason.DangerousPattern,
                    Message: $"命令匹配危险模式 '{pattern}'，已被拦截。",
                    FirstWord: firstWord,
                    MatchedPattern: pattern.ToString());
            }
        }

        return new TerminalCommandDecision(Allowed: true, FirstWord: firstWord);
    }

    public void EnsureInvariantAllowed(string command)
    {
        var decision = EvaluateInvariant(command);
        if (!decision.Allowed)
            throw new UnauthorizedAccessException(decision.Message);
    }

    /// <summary>
    /// 校验命令是否允许执行。
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">Normal 模式下命令不在白名单或匹配危险模式。</exception>
    public void EnsureAllowed(string command, bool isYoloMode)
    {
        var decision = Evaluate(command, isYoloMode);
        if (!decision.Allowed)
            throw new UnauthorizedAccessException(decision.Message);
    }
}

/// <summary>
/// 兼容旧调用点的静态 facade。新工具应注入 ITerminalCommandPolicy。
/// </summary>
public static class TerminalSecurity
{
    public static readonly string[] DefaultWhitelist = DefaultTerminalCommandPolicy.DefaultWhitelist;
    public static readonly Regex[] DangerousPatterns = DefaultTerminalCommandPolicy.DangerousPatterns;

    public static bool IsAllowed(string command) => IsAllowed(command, isYoloMode: false);

    public static bool IsAllowed(string command, bool isYoloMode)
    {
        DefaultTerminalCommandPolicy.Instance.EnsureAllowed(command, isYoloMode);
        return true;
    }
}
