namespace PuddingSsh.Contracts;

/// <summary>
/// 已登记主机的**冻结目标引用**（设计 §5）。字段语义与 <c>ssh_hosts</c> / <c>ssh_execute</c> 的
/// <c>target</c> 一一对应：
/// <list type="bullet">
///   <item><see cref="HostRevision"/>：执行相关配置规范化后的 SHA256（地址/端口/账号/目录作用域/identity 文件名/有效信任集合/shell/cwd/ACL/限额）。</item>
///   <item><see cref="TrustRevision"/>：匹配目标的有效主机公钥/撤销集合摘要。</item>
/// </list>
/// 二者都不是覆盖值：调用方传来的 target 必须与权威配置一致，否则由引用方在连接前判断
/// <c>ssh.target_changed</c>（组件只接收已冻结的快照）。
/// </summary>
public sealed record SshTargetRef(
    string HostId,
    string HostRevision,
    string Host,
    int Port,
    string Username,
    string TrustRevision);

/// <summary><c>ssh_hosts</c> 返回的可见主机描述（设计 §7.1）；不含私钥路径/内容与其他主体的 ACL。</summary>
public sealed record SshHostDescriptor(
    SshTargetRef Target,
    string DisplayName,
    string ShellKind,
    string? DefaultCwd,
    string IdentityScope,
    SshOperationLimits Limits,
    bool IsAvailable)
{
    /// <summary>不可用原因（稳定错误码）；可用时为 <see langword="null"/>。</summary>
    public string? UnavailableReasonCode { get; init; }
}

/// <summary>主机可见性/可用性的稳定取值（设计 §6.1）。作用域由管理配置决定，Agent 不能按次切换。</summary>
public static class SshIdentityScopes
{
    public const string CurrentUser = "current_user";
    public const string AgentPrivate = "agent_private";
}

/// <summary>首期只交付 POSIX 非交互命令；Windows OpenSSH 的引号/编码合同另行交付（设计 §1.2 第 5 条）。</summary>
public static class SshShellKinds
{
    public const string Posix = "posix";
}
