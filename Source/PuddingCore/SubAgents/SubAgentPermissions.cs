namespace PuddingCode.SubAgents;

/// <summary>
/// 子代理权限边界定义 — 控制子代理对文件系统、工具和网络的访问范围。
/// 默认禁止写入系统配置和数据目录。
/// 关联 ADR：Docs/07架构/21子代理工作空间与运行归档ADR.md
/// </summary>
public sealed record SubAgentPermissions
{
    public SubAgentFilesystemPermissions Filesystem { get; init; } = new();
    public SubAgentToolPermissions Tools { get; init; } = new();
    public SubAgentNetworkPermissions Network { get; init; } = new();
}

/// <summary>
/// 文件系统权限 — read/write 为允许的 glob 模式，deny 为明确禁止的模式（优先级最高）。
/// 默认：只读 workspace/** 和 shared/context/**，可写 workspace/**，禁止 ../** 和系统目录。
/// </summary>
public sealed record SubAgentFilesystemPermissions
{
    public List<string> Read { get; init; } = new() { "workspace/**", "shared/context/**" };
    public List<string> Write { get; init; } = new() { "workspace/**" };
    public List<string> Deny { get; init; } = new() { "../**", "data/config/**", "data/databases/**" };
}

/// <summary>
/// 工具权限 — allow 为允许的工具 ID 列表，deny 为明确禁止的工具 ID 列表（优先级高于 allow）。
/// </summary>
public sealed record SubAgentToolPermissions
{
    public List<string> Allow { get; init; } = new();
    public List<string> Deny { get; init; } = new();
}

/// <summary>
/// 网络权限 — 控制子代理是否可以发起外部网络请求。默认禁止。
/// </summary>
public sealed record SubAgentNetworkPermissions
{
    public bool Allow { get; init; } = false;
}
