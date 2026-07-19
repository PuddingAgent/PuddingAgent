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
        "dotnet", "git", "python", "python3", "node", "npm", "pnpm", "yarn",
        "ls", "dir", "cat", "echo", "mkdir", "rmdir",
        "curl", "wget", "ping", "nslookup", "ipconfig", "ifconfig",
        "type", "find", "grep", "findstr", "tail", "head", "wc",
        "cp", "mv", "copy", "move", "tar", "zip", "unzip",
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

        // 第二层：检查白名单
        if (!DefaultWhitelist.Any(w =>
                firstWord.Equals(w, StringComparison.OrdinalIgnoreCase) ||
                firstWord.StartsWith(w, StringComparison.OrdinalIgnoreCase)))
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
