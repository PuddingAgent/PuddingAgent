using System.Text.RegularExpressions;

namespace PuddingRuntime.Services;

public interface ITerminalCommandPolicy
{
    TerminalCommandDecision Evaluate(string command, bool isYoloMode);
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
}

/// <summary>
/// 终端命令策略——Normal 模式执行命令白名单与危险模式拦截，YOLO 模式完全放行。
///
/// Shell 安全边界：
///   1. 模板能力与运行时授权由 ToolPermissionPolicyService + AgentFirewall 负责。
///   2. Normal 模式下，命令白名单只允许 DefaultWhitelist 中的命令前缀。
///   3. Normal 模式下，危险模式拦截会拒绝已知高危命令片段。
///   4. YOLO 是用户临时授予的无限权限，跳过本策略所有权限检查与限制。
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
    /// 判断命令是否允许执行。
    /// </summary>
    /// <param name="command">完整命令行。</param>
    /// <param name="isYoloMode">YOLO 模式下跳过所有命令权限检查与限制。</param>
    public TerminalCommandDecision Evaluate(string command, bool isYoloMode)
    {
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
