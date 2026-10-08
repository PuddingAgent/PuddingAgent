using PuddingSsh.Contracts;
using PuddingSsh.Diagnostics;

namespace PuddingSsh.Validation;

/// <summary>
/// 请求/目标校验（设计 §5/§7）：在**连接之前**拒绝畸形输入，错误码稳定。
/// 组件只校验「形状」——主机 ACL、revision 与可见性属引用方（A5）。
/// </summary>
public static class SshRequestValidator
{
    public static void ValidateTarget(SshTargetRef target)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (string.IsNullOrWhiteSpace(target.HostId)
            || string.IsNullOrWhiteSpace(target.HostRevision)
            || string.IsNullOrWhiteSpace(target.TrustRevision))
        {
            throw new SshComponentException(
                SshErrorCodes.ConfigInvalid,
                "target 缺少 host_id / host_revision / trust_revision。");
        }

        if (string.IsNullOrWhiteSpace(target.Host))
        {
            throw new SshComponentException(SshErrorCodes.ConfigInvalid, "target.host 不能为空。");
        }

        if (target.Host.Contains('\0') || char.IsControl(target.Host[0]))
        {
            throw new SshComponentException(SshErrorCodes.ConfigInvalid, "target.host 含非法字符。");
        }

        if (target.Port is < 1 or > 65535)
        {
            throw new SshComponentException(
                SshErrorCodes.ConfigInvalid,
                $"target.port 越界（1..65535，实际 {target.Port}）。");
        }

        if (string.IsNullOrWhiteSpace(target.Username))
        {
            throw new SshComponentException(SshErrorCodes.ConfigInvalid, "target.username 不能为空。");
        }
    }

    public static void ValidateTest(SshTestRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateTarget(request.Target);
    }

    /// <summary>
    /// 校验执行请求；返回实际生效的限额（只能缩小：见 <see cref="SshOperationLimits"/>）
    /// 与**审批前即可确定**的执行字符串。
    /// </summary>
    public static (SshPosixExecutionPlan Plan, TimeSpan Timeout, int OutputBudget) ValidateExecute(
        SshExecuteRequest request,
        SshOperationLimits limits,
        string? defaultCwd,
        TimeSpan? hostDeadline = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(limits);
        ValidateTarget(request.Target);

        var plan = SshPosixCommand.Compose(request.Command, request.Cwd, defaultCwd);
        var timeout = limits.ResolveOperationTimeout(request.TimeoutSeconds);
        if (hostDeadline is { } deadline && deadline < timeout)
        {
            timeout = deadline;
        }

        return (plan, timeout, limits.ResolveOutputBudget(request.MaxOutputBytes));
    }
}
