namespace PuddingSsh.Contracts;

/// <summary><c>ssh_test</c> 请求（设计 §7.2）：执行 DNS/TCP/握手与认证，不执行 shell、不写远端文件。</summary>
public sealed record SshTestRequest(SshTargetRef Target)
{
    /// <summary>建连 deadline；<see langword="null"/> 表示使用主机配置的 <c>connectTimeoutSeconds</c>。</summary>
    public TimeSpan? ConnectTimeout { get; init; }
}

/// <summary>
/// <c>ssh_execute</c> 请求（设计 §7.3）。组件不提供 env / stdin / shellKind 覆盖 / 自动 sudo / PTY：
/// <see cref="Cwd"/> 省略时用主机默认值，指定时必须是 POSIX 绝对路径；限额只能缩小。
/// </summary>
public sealed record SshExecuteRequest(
    SshTargetRef Target,
    string Command)
{
    /// <summary>POSIX 绝对路径；<see langword="null"/> 表示使用主机 <c>defaultCwd</c>。</summary>
    public string? Cwd { get; init; }

    /// <summary>请求超时（秒）；实际生效值见 <see cref="SshOperationLimits.ResolveOperationTimeout"/>。</summary>
    public int? TimeoutSeconds { get; init; }

    /// <summary>请求捕获预算（字节）；实际生效值只能缩小。</summary>
    public int? MaxOutputBytes { get; init; }

    /// <summary>目的说明；**不是授权证据**（设计 §8）。</summary>
    public string? Reason { get; init; }
}

/// <summary>命令文本上限 16 KiB（UTF-8），且拒绝 NUL（设计 §7.3）。</summary>
public static class SshRequestLimits
{
    public const int MaxCommandUtf8Bytes = 16 * 1024;
}
