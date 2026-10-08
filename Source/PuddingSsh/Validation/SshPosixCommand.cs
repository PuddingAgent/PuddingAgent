using System.Text;
using PuddingSsh.Contracts;
using PuddingSsh.Diagnostics;

namespace PuddingSsh.Validation;

/// <summary>执行封装的结果：**审批前即可确定的精确字符串**（设计 §7.3）。</summary>
public sealed record SshPosixExecutionPlan(string ExecutedCommand, string? EffectiveCwd, bool UsedDefaultCwd);

/// <summary>
/// POSIX 非交互执行的封装合同（设计 §7.3）：
/// <c>cd -- &lt;单引号编码的 cwd&gt; &amp;&amp; &lt;原始 command&gt;</c>。
/// <para>
/// 不允许：env、stdin、shellKind 覆盖、PTY、自动 <c>sudo</c>；不在本地 shell 里拼接地址与命令。
/// <c>cd</c> 失败时 <c>&amp;&amp;</c> 保证后续命令**不会执行**（这是封装里唯一的语义保证，
/// 远端 shell 方言差异不在首期承诺内）。
/// </para>
/// </summary>
public static class SshPosixCommand
{
    /// <summary>POSIX 单引号编码：把 <c>'</c> 变成 <c>'\''</c>，其余字符原样保留。</summary>
    public static string Quote(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }

    /// <summary>
    /// cwd 必须是干净的 POSIX 绝对路径：以 <c>/</c> 开头，无 NUL、无控制字符（含换行/制表）。
    /// 相对路径、<c>~</c>、空串一律拒绝——组件不替远端 shell 猜路径。
    /// </summary>
    public static bool IsValidCwd(string? cwd)
    {
        if (string.IsNullOrEmpty(cwd) || cwd[0] != '/')
        {
            return false;
        }

        foreach (var ch in cwd)
        {
            if (ch == '\0' || char.IsControl(ch))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 组合最终执行字符串。请求未给 cwd 时用主机默认 cwd；两者都没有则不加 <c>cd</c> 前缀
    /// （远端在登录目录执行）——这与「显式指定了非法 cwd」是两种不同事实，必须区分。
    /// </summary>
    public static SshPosixExecutionPlan Compose(string command, string? requestedCwd, string? defaultCwd)
    {
        ValidateCommand(command);

        if (requestedCwd is not null && !IsValidCwd(requestedCwd))
        {
            throw new SshComponentException(
                SshErrorCodes.RequestInvalid,
                "cwd 必须是干净的 POSIX 绝对路径（以 / 开头，且不含 NUL/控制字符）。");
        }

        if (defaultCwd is not null && !IsValidCwd(defaultCwd))
        {
            throw new SshComponentException(
                SshErrorCodes.ConfigInvalid,
                "主机默认 cwd 非法：必须是干净的 POSIX 绝对路径。");
        }

        var cwd = requestedCwd ?? defaultCwd;
        var usedDefault = requestedCwd is null && defaultCwd is not null;

        return cwd is null
            ? new SshPosixExecutionPlan(command, null, false)
            : new SshPosixExecutionPlan($"cd -- {Quote(cwd)} && {command}", cwd, usedDefault);
    }

    /// <summary>命令必填、UTF-8 ≤ 16 KiB、拒绝 NUL（设计 §7.3）。</summary>
    public static void ValidateCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            throw new SshComponentException(SshErrorCodes.RequestInvalid, "command 必填且不能为空白。");
        }

        if (command.Contains('\0'))
        {
            throw new SshComponentException(SshErrorCodes.RequestInvalid, "command 不得包含 NUL。");
        }

        var bytes = Encoding.UTF8.GetByteCount(command);
        if (bytes > SshRequestLimits.MaxCommandUtf8Bytes)
        {
            throw new SshComponentException(
                SshErrorCodes.RequestInvalid,
                $"command 超过 {SshRequestLimits.MaxCommandUtf8Bytes} 字节上限（实际 {bytes}）。");
        }
    }
}
