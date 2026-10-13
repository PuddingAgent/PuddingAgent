using PuddingSsh.Contracts;
using PuddingSsh.Validation;

namespace PuddingSsh.Contracts;

/// <summary>
/// **冻结**的主机上下文（设计 §4/§5）：引用方（Runtime，A5）负责把 DataRoot/Agent 主体解析、主机 ACL、
/// revision 与凭据来源解析完，再交给组件。组件只接收已授权、已解析、已冻结的输入。
/// <para>
/// 组件**不会**重新读配置、不会按别名再解析另一台机器、也不接受调用方覆盖这些值。
/// </para>
/// </summary>
public sealed record SshHostContext(
    SshTargetRef Target,
    SshOperationLimits Limits,
    SshIdentityRequest Identity,
    SshTrustSnapshot Trust,
    string? DefaultCwd = null,
    DateTimeOffset? ExecutionDeadlineUtc = null,
    SshTransferLimits? Transfer = null);
