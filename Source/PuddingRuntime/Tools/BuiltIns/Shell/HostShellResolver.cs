using System.Diagnostics;
using System.Text;

namespace PuddingRuntime.Services.Tools;

/// <summary>
/// shell 选择与进程启动参数的**唯一真源**。
///
/// 为什么需要它（实测依据，勿合并回调用方）：
///   · <c>shell</c> 工具与 <c>terminal_*</c> 工具都执行宿主命令，但历史上各自硬编码了
///     默认引擎——<c>shell</c> 在 Windows 上走 pwsh 7，而 <c>terminal_start</c> 恒走
///     <c>cmd.exe</c>。后果不是「风格不一致」，而是**同一个命令在两个工具里语法不同**：
///     模型在 <c>shell</c> 里验证通过的 PowerShell 写法（如 <c>$ErrorActionPreference</c>、
///     <c>Get-ChildItem</c>）原样搬进 <c>terminal_start</c> 会以 exit 1 失败，
///     反过来 <c>cmd</c> 的 <c>cd /d</c> 在 pwsh 下同样失败。
///   · 默认引擎一旦分叉，任何「本工具里能跑」的结论都不再可迁移，模型只能靠试错。
///
/// 解析链（两个工具共用，不可各自改）：
///   Windows : pwsh 7 > Windows PowerShell 5.1 > cmd
///   其他    : bash > sh
/// </summary>
public static class HostShellResolver
{
    /// <summary>"powershell" 是 PowerShell **家族**的语义标识：具体可执行文件由
    /// <see cref="ResolvePowerShellExecutable"/> 决定（恒优先 pwsh 7），
    /// 因此调用方不需要区分 7.x 与 5.1。</summary>
    public const string PowerShellShell = "powershell";

    public static readonly IReadOnlySet<string> SupportedShells = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "auto",
        "wsl",
        "bash",
        "cmd",
        PowerShellShell,
        "pwsh",
    };

    /// <summary>
    /// 把调用方给的 shell 名归一到内部标识。
    /// <paramref name="requestedShell"/> 为空/空白视同 <c>auto</c>。
    /// </summary>
    public static string ResolveShell(string? requestedShell)
    {
        var requested = string.IsNullOrWhiteSpace(requestedShell) ? "auto" : requestedShell.Trim();

        // pwsh 与 powershell 都是「PowerShell 家族」：内部统一成 powershell，
        // 真实可执行文件仍由 ResolvePowerShellExecutable 优先 pwsh 7。
        if (requested.Equals("pwsh", StringComparison.OrdinalIgnoreCase))
            return PowerShellShell;

        if (!requested.Equals("auto", StringComparison.OrdinalIgnoreCase))
            return requested.ToLowerInvariant();

        if (!OperatingSystem.IsWindows())
            return CommandExists("bash") ? "bash" : "sh";

        return CommandExists("pwsh") || CommandExists("powershell.exe") || CommandExists("powershell")
            ? PowerShellShell
            : "cmd";
    }

    /// <summary>
    /// PowerShell 家族的实际可执行文件：**恒优先 pwsh 7**，只有它缺失时才退到
    /// Windows PowerShell 5.1。返回 null 表示宿主两个都没有。
    /// </summary>
    public static string? ResolvePowerShellExecutable()
    {
        if (CommandExists("pwsh"))
            return "pwsh";

        return OperatingSystem.IsWindows() && CommandExists("powershell.exe")
            ? "powershell.exe"
            : null;
    }

    /// <summary>命令是否可在 PATH 上找到（Windows 用 where.exe，其他用 which）。</summary>
    public static bool CommandExists(string command)
    {
        try
        {
            var check = new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "where.exe" : "which",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            check.ArgumentList.Add(command);
            using var process = Process.Start(check);
            if (process is null)
                return false;
            process.WaitForExit(3000);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 统一子进程编码环境。两个工具都要设，否则 dotnet 的 UTF-8 输出会被按
    /// 活动代码页解码成乱码（详见 TerminalProcessManager 中长注释的实测记录）。
    /// </summary>
    public static void ConfigureUtf8Environment(ProcessStartInfo psi)
    {
        psi.StandardOutputEncoding = Encoding.UTF8;
        psi.StandardErrorEncoding = Encoding.UTF8;
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUTF8"] = "1";
        // 模型不需要 ANSI 颜色码；NO_COLOR 是跨工具标准。
        psi.Environment["NO_COLOR"] = "1";
    }

    /// <summary>
    /// PowerShell 命令包装：强制 UTF-8 输出并关闭 pwsh 7 的格式化高亮。
    /// 5.1 没有 <c>$PSStyle</c>，用 try/catch 保持兼容。
    /// </summary>
    public static string WrapPowerShellCommand(string command) =>
        "[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false; " +
        "$OutputEncoding = [Console]::OutputEncoding; " +
        "try { $PSStyle.OutputRendering = 'PlainText' } catch { }; " +
        command;

    /// <summary>
    /// 按 <paramref name="shell"/> 填充 <paramref name="psi"/> 的 FileName 与参数。
    /// 返回 false 并给出原因表示该 shell 在本机不可用。
    ///
    /// ⚠️ argv 传递方式按 shell 区分，不可统一：
    ///   · PowerShell / bash / wsl 走 <see cref="ProcessStartInfo.ArgumentList"/>；
    ///   · cmd.exe 必须走 <see cref="ProcessStartInfo.Arguments"/> 原样拼接——
    ///     .NET 的 ArgumentList 会把内层 <c>"</c> 转义成 <c>\"</c>，而 cmd 只认
    ///     <c>""</c> 与 <c>^"</c>，含引号命令会被静默破坏成「exit≠0 且输出全空」
    ///     （实测 2026-09-19）。
    /// </summary>
    public static bool ConfigureStartInfo(
        ProcessStartInfo psi,
        string shell,
        string command,
        string workingDirectory,
        out string? error)
    {
        error = null;

        if (!shell.Equals("wsl", StringComparison.OrdinalIgnoreCase))
            psi.WorkingDirectory = workingDirectory;

        switch (shell.ToLowerInvariant())
        {
            case PowerShellShell:
            {
                var exe = ResolvePowerShellExecutable();
                if (exe is null)
                {
                    error = "Neither pwsh nor powershell.exe is available.";
                    return false;
                }
                psi.FileName = exe;
                psi.ArgumentList.Add("-NoProfile");
                psi.ArgumentList.Add("-ExecutionPolicy");
                psi.ArgumentList.Add("Bypass");
                psi.ArgumentList.Add("-Command");
                psi.ArgumentList.Add(WrapPowerShellCommand(command));
                return true;
            }

            case "cmd":
                if (!OperatingSystem.IsWindows())
                {
                    error = "cmd shell is only available on Windows hosts.";
                    return false;
                }
                psi.FileName = "cmd.exe";
                // /d 跳过 AutoRun；/s 只剥离最外层引号、按原样执行。
                psi.Arguments = "/d /s /c \"" + command + "\"";
                return true;

            case "wsl":
                if (!OperatingSystem.IsWindows())
                {
                    error = "wsl shell is only available on Windows hosts.";
                    return false;
                }
                if (!CommandExists("wsl.exe"))
                {
                    error = "wsl.exe is not available on this host.";
                    return false;
                }
                psi.FileName = "wsl.exe";
                psi.ArgumentList.Add("--cd");
                psi.ArgumentList.Add(workingDirectory);
                psi.ArgumentList.Add("--");
                psi.ArgumentList.Add("bash");
                psi.ArgumentList.Add("-lc");
                psi.ArgumentList.Add(command);
                return true;

            case "bash":
            case "sh":
                if (!CommandExists(shell))
                {
                    error = $"{shell} is not available on this host.";
                    return false;
                }
                psi.FileName = shell;
                psi.ArgumentList.Add("-lc");
                psi.ArgumentList.Add(command);
                return true;

            default:
                error = $"Unsupported shell '{shell}'.";
                return false;
        }
    }
}
