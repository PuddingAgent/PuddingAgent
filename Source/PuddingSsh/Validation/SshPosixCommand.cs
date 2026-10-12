using System.Text;
using PuddingSsh.Contracts;
using PuddingSsh.Diagnostics;

namespace PuddingSsh.Validation;

/// <summary>执行封装的结果：**审批前即可确定的精确字符串**（设计 §7.3 / ADR-096 D1）。</summary>
public sealed record SshPosixExecutionPlan(string ExecutedCommand, string? EffectiveCwd, bool UsedDefaultCwd);

/// <summary>
/// POSIX 非交互执行的封装合同（设计 §7.3 + ADR-096 D1）：
/// <c>cd -- &lt;单引号编码的 cwd&gt; || exit $?</c> 独立成行，随后是**原样**命令。
/// <para>
/// 为什么不是 <c>cd ... &amp;&amp; &lt;命令&gt;</c>：那样只有命令的**第一段**受 <c>&amp;&amp;</c> 限制，
/// 复合脚本（分号、换行、<c>||</c>、pipeline、后台列表、行尾注释）在 cd 失败后仍会执行，
/// 而且后续段的退出码会覆盖 cd 的失败——那是会把文件写到账号默认目录的真实副作用。
/// 守卫独立成行后，<c>cd</c> 失败即让当前 shell 退出，后续任何一段都不会执行。
/// </para>
/// <para>
/// 分隔符固定为 <c>\n</c>（不是 <c>Environment.NewLine</c>）；不拼 <c>set -e</c>、
/// 不用 subshell 或第二个 shell 包裹原脚本（那会改变原始语义）。
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
    /// 组合最终执行字符串。请求未给 cwd 时用主机默认 cwd；两者都没有则不加守卫
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
            : new SshPosixExecutionPlan($"cd -- {Quote(cwd)} || exit $?\n{command}", cwd, usedDefault);
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
