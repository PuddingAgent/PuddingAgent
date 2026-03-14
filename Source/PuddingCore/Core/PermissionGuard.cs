using PuddingCode.Abstractions;

namespace PuddingCode.Core;

/// <summary>
/// 路径沙盒 + 命令白名单 + 危险模式检测。
/// 在 ShellTool/FileTool 执行前拦截非法操作。
/// </summary>
public sealed class PermissionGuard
{
    private readonly string _projectRoot;

    public PermissionGuard(string projectRoot)
    {
        ArgumentNullException.ThrowIfNull(projectRoot);
        _projectRoot = Path.GetFullPath(projectRoot);
    }

    // ──── L0 只读命令 ────

    private static readonly HashSet<string> s_l0Commands = new(StringComparer.OrdinalIgnoreCase)
    {
        "ls", "dir", "cat", "type", "head", "tail", "grep", "egrep", "findstr",
        "find", "where", "which", "rg", "fd", "ag", "pwd", "cd",
        "tree", "stat", "file", "diff", "echo", "env", "printenv",
        "set", "uname", "hostname", "whoami", "date", "df", "du",
        "ps", "tasklist", "systeminfo", "awk", "sort", "uniq", "cut",
        "tr", "jq", "yq", "wc", "less", "more", "ping", "nslookup",
        "dig", "traceroute", "tracert", "netstat", "ss", "ipconfig",
        "ifconfig", "ip", "javac"
    };

    // ──── L1 项目写入命令 ────

    private static readonly HashSet<string> s_l1Commands = new(StringComparer.OrdinalIgnoreCase)
    {
        "dotnet", "msbuild", "nuget", "npm", "npx", "yarn", "pnpm",
        "node", "python", "python3", "pip", "pip3", "cargo", "rustc",
        "go", "java", "mvn", "gradle",
        "git", "gh",
        "mkdir", "cp", "copy", "mv", "move", "touch", "patch",
        "sed", "xargs",
        "tar", "zip", "unzip", "gzip", "gunzip", "7z",
        "docker", "kubectl", "az", "aws", "gcloud"
    };

    // ──── 绝对黑名单 ────

    private static readonly HashSet<string> s_blockedCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "sudo", "su", "runas", "format", "mkfs", "dd",
        "shutdown", "reboot", "halt", "poweroff", "eval"
    };

    // ──── 危险参数模式 ────

    private static readonly string[] s_dangerousPatterns =
    [
        "| bash", "| sh", "| cmd", "| powershell",
        "--no-preserve-root", "> /dev/sd", ":(){ :",
        "curl|", "wget|", "rm -rf /", "del /s /q C:\\"
    ];

    // ──── 禁止写入的文件扩展名 ────

    private static readonly HashSet<string> s_blockedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".so", ".dylib", ".app", ".msi", ".deb", ".rpm",
        ".sh", ".bat", ".cmd", ".ps1", ".psm1",
        ".pem", ".key", ".pfx", ".p12", ".cer", ".crt",
        ".reg", ".sys", ".inf", ".service"
    };

    /// <summary>校验 shell 命令是否允许执行。</summary>
    public PermissionResult ValidateCommand(string command)
    {
        var cmdName = ExtractCommandName(command);

        // 1. 绝对黑名单
        if (s_blockedCommands.Contains(cmdName))
            return PermissionResult.Denied(
                $"[SECURITY ERROR]: Command blocked.\n" +
                $"Command: {cmdName}\n" +
                $"Reason: This command is permanently blocked for safety.\n" +
                $"Hint: If you need elevated access, ask the user directly.");

        // 2. 危险参数模式
        foreach (var pattern in s_dangerousPatterns)
        {
            if (command.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                return PermissionResult.Denied(
                    $"[SECURITY ERROR]: Dangerous pattern detected.\n" +
                    $"Command: {command}\n" +
                    $"Pattern: {pattern}\n" +
                    $"Hint: Break this into safer individual steps.");
        }

        // 3. 命令级别判定
        if (s_l0Commands.Contains(cmdName))
            return PermissionResult.Allowed(SecurityLevel.ReadOnly);

        if (s_l1Commands.Contains(cmdName))
            return PermissionResult.Allowed(SecurityLevel.ProjectWrite);

        // 4. 未知命令 → 需人工确认（当前版本拒绝）
        return PermissionResult.Denied(
            $"[SECURITY ERROR]: Permission denied.\n" +
            $"Command: {cmdName}\n" +
            $"Reason: Command is not in the whitelist.\n" +
            $"Hint: Only whitelisted development tools are allowed.");
    }

    /// <summary>校验文件路径是否允许读取。</summary>
    public PermissionResult ValidateFileRead(string filePath)
    {
        var fullPath = Path.GetFullPath(filePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (IsForbiddenPath(fullPath))
            return PermissionResult.Denied(
                $"[SECURITY ERROR]: Permission denied.\n" +
                $"Action: read_file\n" +
                $"Path: {fullPath}\n" +
                $"Reason: Path is in a forbidden zone.");

        return PermissionResult.Allowed(SecurityLevel.ReadOnly);
    }

    /// <summary>校验文件写入是否允许。</summary>
    public PermissionResult ValidateFileWrite(string filePath)
    {
        var fullPath = Path.GetFullPath(filePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // 禁区
        if (IsForbiddenPath(fullPath))
            return PermissionResult.Denied(
                $"[SECURITY ERROR]: Permission denied.\n" +
                $"Action: write_file\n" +
                $"Path: {fullPath}\n" +
                $"Reason: Path is in a forbidden zone.");

        // 项目外 — 使用规范化路径比较，防止路径穿越攻击
        var normalizedRoot = _projectRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!fullPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !fullPath.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            return PermissionResult.Denied(
                $"[SECURITY ERROR]: Permission denied.\n" +
                $"Action: write_file\n" +
                $"Path: {fullPath}\n" +
                $"Reason: Path is outside the project boundary.\n" +
                $"Hint: You are only allowed to write within the project directory.");

        // 禁止写入的文件类型
        var ext = Path.GetExtension(fullPath);
        if (!string.IsNullOrEmpty(ext) && s_blockedExtensions.Contains(ext))
            return PermissionResult.Denied(
                $"[SECURITY ERROR]: Permission denied.\n" +
                $"Action: write_file\n" +
                $"Path: {fullPath}\n" +
                $"Reason: Writing '{ext}' files is not allowed.\n" +
                $"Hint: Only source code and config files can be written.");

        return PermissionResult.Allowed(SecurityLevel.ProjectWrite);
    }

    /// <summary>校验目录列表是否允许。</summary>
    public PermissionResult ValidateDirectoryList(string dirPath)
    {
        var fullPath = Path.GetFullPath(dirPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (IsForbiddenPath(fullPath))
            return PermissionResult.Denied(
                $"[SECURITY ERROR]: Permission denied.\n" +
                $"Action: list_directory\n" +
                $"Path: {fullPath}\n" +
                $"Reason: Path is in a forbidden zone.");

        return PermissionResult.Allowed(SecurityLevel.ReadOnly);
    }

    private static bool IsForbiddenPath(string fullPath)
    {
        string[] forbidden = OperatingSystem.IsWindows()
            ? [@"C:\Windows\", @"C:\Program Files\", @"C:\Program Files (x86)\"]
            : OperatingSystem.IsMacOS()
                ? ["/System/", "/usr/sbin/", "/private/var/", "/Library/LaunchDaemons/"]
                : ["/boot/", "/sbin/", "/usr/sbin/", "/proc/", "/sys/", "/dev/"];

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] sensitiveHome =
        [
            Path.Combine(home, ".ssh"),
            Path.Combine(home, ".gnupg"),
            Path.Combine(home, ".aws"),
            Path.Combine(home, ".azure"),
            Path.Combine(home, ".kube")
        ];

        foreach (var dir in forbidden.Concat(sensitiveHome))
        {
            if (fullPath.StartsWith(dir, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static string ExtractCommandName(string command)
    {
        var trimmed = command.TrimStart();
        var spaceIdx = trimmed.IndexOf(' ');
        var name = spaceIdx > 0 ? trimmed[..spaceIdx] : trimmed;
        return Path.GetFileNameWithoutExtension(name);
    }
}
