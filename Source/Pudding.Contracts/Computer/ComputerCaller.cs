namespace Pudding.Contracts.Computer;

/// <summary>
/// 可信主体：谁在请求这次桌面操作。
/// <para>
/// 全部字段来自 <c>ToolExecutionContext</c> 的**冻结**字段投影，不允许调用方自报。
/// 子代理身份取 <c>ExecutionIdentity</c> 的执行种类判定，**不能**用「ParentRunId 是否为 null」推断——
/// 那是把"有父亲"当成"是子代理"，父运行缺失时会把子代理误判成主运行。
/// </para>
/// </summary>
public sealed record ComputerPrincipal(
    string WorkspaceId,
    string ChatSessionId,
    string AgentInstanceId,
    string RunId,
    string? ParentRunId,
    bool IsSubAgent);

/// <summary>Agent 工具调用身份。仅由工具执行上下文构造，不进模型 schema。</summary>
public sealed record ComputerCaller(
    ComputerPrincipal Principal,
    string ToolCallId);

/// <summary>
/// 已认证用户控制面身份（目标选择、授权、暂停/恢复）。
/// <para>
/// **不进入 Agent 工具 schema**：工具调用必须走 tool 分支，grant.apply/revoke 必须走 control 分支，
/// 线缆上是判别联合、两者恰好一个。这样用户控制面无法伪造 Agent Run 去凑既有 Browser 身份门。
/// </para>
/// </summary>
public sealed record ComputerControlCaller(
    string UserId,
    string WorkspaceId,
    string RequestId);

/// <summary>
/// 一次调用的上下文。
/// <para>
/// <c>OperationId</c> 在 Core 发送适配器**首次接受** ToolCall 时生成并缓存；
/// 重试沿用同一个 ID（否则去重与幂等全部失效），跨 Core 实例不复用。
/// </para>
/// </summary>
public sealed record ComputerCallContext(
    string OperationId,
    ComputerCaller Caller,
    DateTimeOffset DeadlineUtc);

/// <summary>
/// 执行世代：由已认证连接与本地状态产生，**模型不填写**。
/// <para>
/// 断连后 Worker 换代，旧世代上的待定操作不得跨世代自动重发（新 Worker 不知道它们的终态，
/// 只能报 unknown）。<c>ControlEpoch</c> 用于区分暂停/恢复等控制状态的不同任期。
/// </para>
/// </summary>
public sealed record ComputerExecutionEpoch(
    string DesktopProcessInstanceId,
    ulong ConnectionGeneration,
    string WorkerInstanceId,
    long ControlEpoch);
