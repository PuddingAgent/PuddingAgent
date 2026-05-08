using System.Text.RegularExpressions;

namespace PuddingRuntime.Services;

/// <summary>
/// 终端安全校验——命令白名单与危险模式拦截。
/// 
/// 四层安全边界：
///   第一层：CapabilityPolicy.AllowShellExecution — 模板级（由 SandboxExecutor 负责）
///   第二层：命令白名单 — 只允许 DefaultWhitelist 中的命令前缀
///   第三层：危险模式拦截 — 即使通过白名单也拦截已知危险模式
///   第四层：WorkingDirectoryIsolation — 工作目录限制（由 SandboxExecutor 的 AllowPathPrefix 负责）
/// </summary>
public static class TerminalSecurity
{
    /// <summary>第二层：默认允许的安全命令前缀白名单。</summary>
    public static readonly string[] DefaultWhitelist =
    [
        "dotnet", "git", "python", "python3", "node", "npm", "pnpm", "yarn",
        "docker", "ls", "dir", "cat", "echo", "mkdir", "rmdir",
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
    /// 校验命令是否允许执行。
    /// </summary>
    /// <param name="command">完整命令行。</param>
    /// <returns>true 表示允许。</returns>
    /// <exception cref="UnauthorizedAccessException">命令不在白名单或匹配危险模式。</exception>
    public static bool IsAllowed(string command)
    {
        var trimmed = command.TrimStart();
        if (trimmed.Length == 0)
            throw new UnauthorizedAccessException("空命令不允许执行。");

        // 提取第一个词作为命令名
        var firstWordEnd = trimmed.IndexOf(' ');
        var firstWord = firstWordEnd > 0 ? trimmed[..firstWordEnd] : trimmed;

        // 第二层：检查白名单
        if (!DefaultWhitelist.Any(w =>
                firstWord.Equals(w, StringComparison.OrdinalIgnoreCase) ||
                firstWord.StartsWith(w, StringComparison.OrdinalIgnoreCase)))
        {
            throw new UnauthorizedAccessException(
                $"命令 '{firstWord}' 不在终端白名单中。允许的命令: {string.Join(", ", DefaultWhitelist.Take(10))}...");
        }

        // 第三层：检查危险模式
        foreach (var pattern in DangerousPatterns)
        {
            if (pattern.IsMatch(command))
            {
                throw new UnauthorizedAccessException(
                    $"命令匹配危险模式 '{pattern}'，已被拦截。");
            }
        }

        return true;
    }
}
