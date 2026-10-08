using System.Globalization;

namespace Pudding.Contracts.Desktop;

/// <summary>
/// 页面授权范围（位标志）。
///
/// 设计要点（方案 §3.2）：<c>context trust</c> 只说明「表面类别」，<b>grant 才说明</b>
/// 「哪个调用者能阅读/操作哪个页面」。因此「在 AgentAuthorized 上下文里」绝不等于
/// 「这个页面已获授权」——<c>IsAgentTarget</c> 之类的标记必须与 grant 一起进准入判据。
/// </summary>
[Flags]
public enum BrowserPageGrantScope
{
    /// <summary>无范围：不授予任何能力（默认值，fail closed）。</summary>
    None = 0,

    /// <summary>只读观察：元数据、快照、定位、等待、截图。</summary>
    Read = 1 << 0,

    /// <summary>副作用操作：导航、交互、坐标输入、脚本。</summary>
    Write = 1 << 1,

    /// <summary>页面生命周期管理：建/关 context 与 tab。</summary>
    Manage = 1 << 2,
}

/// <summary>调用者类别。子代理只能取得父任务授权的<b>子集</b>，不能自行扩大。</summary>
public enum BrowserCallerKind
{
    /// <summary>用户在 Desktop UI 上的直接操作（不受自动化闸门约束）。</summary>
    User,

    /// <summary>Core 侧可信 Agent 任务。</summary>
    Agent,

    /// <summary>由父任务派生的子代理；必须带父任务身份。</summary>
    SubAgent,
}

/// <summary>
/// 可信调用身份。由 Core 的可信执行上下文产生，<b>不接受模型填写</b>
/// （方案 §3.1：不让模型填 epoch 或 owner 来获得权限）。
/// </summary>
public sealed record BrowserCallerIdentity
{
    public const int MaxLength = 128;

    public BrowserCallerIdentity(
        BrowserCallerKind kind,
        string callerId,
        string? taskId = null,
        string? parentTaskId = null)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Caller kind is not registered.");
        }

        CallerId = ContractText.RequireIdentifier(callerId, MaxLength, nameof(callerId));
        TaskId = taskId is null ? null : ContractText.RequireIdentifier(taskId, MaxLength, nameof(taskId));
        ParentTaskId = parentTaskId is null
            ? null
            : ContractText.RequireIdentifier(parentTaskId, MaxLength, nameof(parentTaskId));

        if (kind == BrowserCallerKind.SubAgent)
        {
            if (TaskId is null)
            {
                throw new ArgumentException("A sub-agent caller must carry its own task identity.", nameof(taskId));
            }

            if (ParentTaskId is null)
            {
                throw new ArgumentException(
                    "A sub-agent caller must carry the parent task identity; grants are issued against it.",
                    nameof(parentTaskId));
            }
        }

        if (ParentTaskId is not null && string.Equals(ParentTaskId, TaskId, StringComparison.Ordinal))
        {
            throw new ArgumentException("A caller cannot be its own parent task.", nameof(parentTaskId));
        }

        Kind = kind;
    }

    public BrowserCallerKind Kind { get; }

    public string CallerId { get; }

    /// <summary>任务身份；用户直接操作时为 null。</summary>
    public string? TaskId { get; }

    /// <summary>派生该子代理的父任务身份；仅 <see cref="BrowserCallerKind.SubAgent"/> 非空。</summary>
    public string? ParentTaskId { get; }

    public bool IsSubAgent => Kind == BrowserCallerKind.SubAgent;

    /// <summary>是否可信 Agent 身份（Agent 或 SubAgent）。<see cref="BrowserCallerKind.User"/> 不走自动化闸门。</summary>
    public bool IsTrustedAutomationCaller => Kind is BrowserCallerKind.Agent or BrowserCallerKind.SubAgent;

    /// <summary>
    /// 该身份是否落在授权绑定的任务范围内：同任务，或同一个父任务下的子任务。
    /// 这是「子代理不得扩大父授权」的判据基础。
    /// </summary>
    public bool IsSameTaskScope(BrowserCallerIdentity other)
    {
        ArgumentNullException.ThrowIfNull(other);

        if (string.Equals(CallerId, other.CallerId, StringComparison.Ordinal))
        {
            return true;
        }

        var mine = ParentTaskId ?? TaskId;
        var theirs = other.ParentTaskId ?? other.TaskId;
        return mine is not null && theirs is not null && string.Equals(mine, theirs, StringComparison.Ordinal);
    }

    public override string ToString() =>
        TaskId is null ? $"{Kind}:{CallerId}" : $"{Kind}:{CallerId}/task={TaskId}";
}

/// <summary>
/// 授权记录的身份绑定（方案 §3.2）：Desktop 实例 + 启动进程实例 + Core 连接世代。
/// 换实例/换世代后旧 grant 一律失效，避免「跨 Desktop 目标」与「旧世代命令」复用许可。
/// </summary>
public sealed record BrowserPageGrantBinding
{
    public BrowserPageGrantBinding(
        DesktopInstanceId desktopId,
        DesktopProcessInstanceId processInstanceId,
        ConnectionGeneration connectionGeneration)
    {
        DesktopId = desktopId ?? throw new ArgumentNullException(nameof(desktopId));
        ProcessInstanceId = processInstanceId ?? throw new ArgumentNullException(nameof(processInstanceId));
        ConnectionGeneration = connectionGeneration;
    }

    public DesktopInstanceId DesktopId { get; }

    public DesktopProcessInstanceId ProcessInstanceId { get; }

    /// <summary><see cref="ConnectionGeneration.None"/> 表示尚未握手成功 ⇒ 该绑定不可用。</summary>
    public ConnectionGeneration ConnectionGeneration { get; }

    public bool IsLive => ConnectionGeneration.IsLive;

    public bool Matches(BrowserPageGrantBinding other)
    {
        ArgumentNullException.ThrowIfNull(other);

        return string.Equals(DesktopId.Value, other.DesktopId.Value, StringComparison.Ordinal)
            && string.Equals(ProcessInstanceId.Value, other.ProcessInstanceId.Value, StringComparison.Ordinal)
            && ConnectionGeneration.Value == other.ConnectionGeneration.Value;
    }

    public override string ToString() =>
        $"{DesktopId.Value}/{ProcessInstanceId.Value}@gen{ConnectionGeneration.Value}";
}

/// <summary>授权记录 ID（系统产生，不接受用户文本）。</summary>
public sealed record BrowserPageGrantId
{
    public const int MaxLength = 128;

    public BrowserPageGrantId(string value) =>
        Value = ContractText.RequireIdentifier(value, MaxLength, "grantId");

    public string Value { get; }

    public static bool IsValid(string? value) => ContractText.IsIdentifier(value, MaxLength);

    public static BrowserPageGrantId NewId() =>
        new(Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));

    public override string ToString() => Value;
}

/// <summary>
/// 一条页面授权记录：绑定 = 实例/世代 + context/page（+ 可选 frame）+ 可信调用身份，
/// 范围 = read/write/manage，可撤销（撤销推进 <see cref="BrowserPageGrantEpoch"/>）。
///
/// 不可变：撤销不是改这条记录的字段，而是把它从注册表移除并推进 epoch
/// （方案 §3.2「授予记录……可撤销 epoch」）。
/// </summary>
public sealed record BrowserPageGrant
{
    public const int MaxFrameIdLength = 128;

    public BrowserPageGrant(
        BrowserPageGrantId grantId,
        BrowserPageGrantBinding binding,
        DesktopPageTarget target,
        BrowserCallerIdentity grantee,
        BrowserPageGrantScope scope,
        BrowserPageGrantEpoch epoch,
        DateTimeOffset issuedAtUtc,
        string? frameId = null,
        DateTimeOffset? expiresAtUtc = null)
    {
        GrantId = grantId ?? throw new ArgumentNullException(nameof(grantId));
        Binding = binding ?? throw new ArgumentNullException(nameof(binding));
        Target = target ?? throw new ArgumentNullException(nameof(target));
        Grantee = grantee ?? throw new ArgumentNullException(nameof(grantee));

        if (scope == BrowserPageGrantScope.None)
        {
            throw new ArgumentException("A grant with no scope must not be issued.", nameof(scope));
        }

        if ((scope & ~BrowserPageGrantScope.Read & ~BrowserPageGrantScope.Write & ~BrowserPageGrantScope.Manage) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(scope), scope, "Grant scope contains unregistered bits.");
        }

        if (issuedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Grant issuance time must be UTC.", nameof(issuedAtUtc));
        }

        if (expiresAtUtc is { } expiry)
        {
            if (expiry.Offset != TimeSpan.Zero)
            {
                throw new ArgumentException("Grant expiry must be UTC.", nameof(expiresAtUtc));
            }

            if (expiry <= issuedAtUtc)
            {
                throw new ArgumentException("Grant expiry must be after issuance.", nameof(expiresAtUtc));
            }
        }

        Scope = scope;
        Epoch = epoch;
        IssuedAtUtc = issuedAtUtc;
        FrameId = frameId is null
            ? null
            : ContractText.RequireIdentifier(frameId, MaxFrameIdLength, nameof(frameId));
        ExpiresAtUtc = expiresAtUtc;
    }

    public BrowserPageGrantId GrantId { get; }

    public BrowserPageGrantBinding Binding { get; }

    public DesktopPageTarget Target { get; }

    public BrowserCallerIdentity Grantee { get; }

    public BrowserPageGrantScope Scope { get; }

    /// <summary>签发时的授权世代；撤销/切目标会推进 authority 的当前世代，使旧 grant 可被识别为陈旧。</summary>
    public BrowserPageGrantEpoch Epoch { get; }

    public DateTimeOffset IssuedAtUtc { get; }

    /// <summary>frame 身份绑定；为 null 表示顶层文档。跨 frame 复用同一 ref 必须拒绝（方案 §5）。</summary>
    public string? FrameId { get; }

    public DateTimeOffset? ExpiresAtUtc { get; }

    /// <summary>授权范围是否覆盖所需范围（按位包含）。</summary>
    public bool Covers(BrowserPageGrantScope required) =>
        (Scope & required) == required;

    /// <summary>是否仍在有效期内；<c>false</c> 表示已过期，必须重新授权。</summary>
    public bool IsExpiredAt(DateTimeOffset utcNow) =>
        ExpiresAtUtc is { } expiry && utcNow >= expiry;

    /// <summary>同一页面目标（context/page，不比 frame）。</summary>
    public bool TargetsSamePage(DesktopPageTarget other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return string.Equals(Target.Key, other.Key, StringComparison.Ordinal);
    }

    public override string ToString() =>
        $"grant {GrantId.Value} {Target.Key}{(FrameId is null ? string.Empty : $"[{FrameId}]")}"
        + $" {Scope} → {Grantee} @epoch{Epoch.Value}";
}

/// <summary>任务级管理授权 ID（系统产生）。</summary>
public sealed record BrowserManagementGrantId
{
    public const int MaxLength = 128;

    public BrowserManagementGrantId(string value) =>
        Value = ContractText.RequireIdentifier(value, MaxLength, "managementGrantId");

    public string Value { get; }

    public static BrowserManagementGrantId NewId() =>
        new(Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));

    public override string ToString() => Value;
}

/// <summary>
/// 任务级管理授权：「允许本任务新建标签 / 建关 context」（方案 §3.2）。
///
/// 为什么单独一类：这些操作发生时<b>还没有可授权的页面</b>（新建标签更是要创建页面），
/// 因此不能用 page grant 表达。它绑定实例/世代 + 可信任务身份，且子代理只能拿到
/// 父任务同一范围内的子集。
/// </summary>
public sealed record BrowserManagementGrant
{
    public BrowserManagementGrant(
        BrowserManagementGrantId grantId,
        BrowserPageGrantBinding binding,
        BrowserCallerIdentity grantee,
        DateTimeOffset issuedAtUtc,
        DateTimeOffset? expiresAtUtc = null)
    {
        GrantId = grantId ?? throw new ArgumentNullException(nameof(grantId));
        Binding = binding ?? throw new ArgumentNullException(nameof(binding));
        Grantee = grantee ?? throw new ArgumentNullException(nameof(grantee));

        if (issuedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Grant issuance time must be UTC.", nameof(issuedAtUtc));
        }

        if (expiresAtUtc is { } expiry)
        {
            if (expiry.Offset != TimeSpan.Zero)
            {
                throw new ArgumentException("Grant expiry must be UTC.", nameof(expiresAtUtc));
            }

            if (expiry <= issuedAtUtc)
            {
                throw new ArgumentException("Grant expiry must be after issuance.", nameof(expiresAtUtc));
            }
        }

        IssuedAtUtc = issuedAtUtc;
        ExpiresAtUtc = expiresAtUtc;
    }

    public BrowserManagementGrantId GrantId { get; }

    public BrowserPageGrantBinding Binding { get; }

    public BrowserCallerIdentity Grantee { get; }

    public DateTimeOffset IssuedAtUtc { get; }

    public DateTimeOffset? ExpiresAtUtc { get; }

    public bool IsExpiredAt(DateTimeOffset utcNow) =>
        ExpiresAtUtc is { } expiry && utcNow >= expiry;

    public override string ToString() =>
        $"management grant {GrantId.Value} → {Grantee}"
        + (ExpiresAtUtc is { } expiry ? $" until {expiry:O}" : string.Empty);
}

/// <summary>
/// 准入拒绝原因码（真源在本文件）。线名用于结构化回执与审计；
/// 与已冻结的 <see cref="DesktopCapabilityErrorCode"/> 表分开，避免在 B0（未接宿主）阶段
/// 改动线缆错误目录——线缆可见的原因码属于接入切片。
/// </summary>
public enum BrowserAuthorizationDenial
{
    /// <summary>未拒绝。</summary>
    None,

    /// <summary>尚未握手/连接已失效；恢复连接需重新获取许可与状态。</summary>
    NotConnected,

    /// <summary>浏览器表面已关闭。</summary>
    Closed,

    /// <summary>工具运行时被暂停。</summary>
    Paused,

    /// <summary>用户接管中；副作用操作一律拒绝。</summary>
    UserTakeover,

    /// <summary>该页面没有任何 grant。</summary>
    NoGrant,

    /// <summary>缺少任务级管理授权（建/关 context、新建标签）。</summary>
    NoManagementGrant,

    /// <summary>有 grant 但范围不足（例如只有 read 却要 write）。</summary>
    ScopeInsufficient,

    /// <summary>grant 指向另一个页面。</summary>
    TargetMismatch,

    /// <summary>frame 身份不匹配。</summary>
    FrameMismatch,

    /// <summary>grant 已被撤销。</summary>
    GrantRevoked,

    /// <summary>grant 已过期。</summary>
    GrantExpired,

    /// <summary>实例/进程/连接世代绑定不匹配。</summary>
    BindingMismatch,

    /// <summary>调用方观察到的世代已过期（期间发生了接管/撤权/断连）。</summary>
    EpochStale,

    /// <summary>执行租约已失效（被接管撤销或已被别的持有者拿走）。</summary>
    LeaseLost,

    /// <summary>同一页面目标已有在途写租约（写操作必须按页串行化）。</summary>
    PageBusy,

    /// <summary>grant 的授予对象与调用者不在同一任务范围。</summary>
    CallerMismatch,

    /// <summary>调用者不是可信自动化身份。</summary>
    UntrustedCaller,

    /// <summary>该操作需要显式页面目标但没有提供。</summary>
    MissingTarget,

    /// <summary>操作未登记。</summary>
    UnknownOperation,
}

/// <summary>拒绝原因码 ↔ 线名（fail closed：未登记值取红，不猜测）。</summary>
public static class BrowserAuthorizationDenialWire
{
    public static string NameOf(BrowserAuthorizationDenial denial) => denial switch
    {
        BrowserAuthorizationDenial.None => "none",
        BrowserAuthorizationDenial.NotConnected => "not_connected",
        BrowserAuthorizationDenial.Closed => "closed",
        BrowserAuthorizationDenial.Paused => "paused",
        BrowserAuthorizationDenial.UserTakeover => "user_takeover",
        BrowserAuthorizationDenial.NoGrant => "no_grant",
        BrowserAuthorizationDenial.NoManagementGrant => "no_management_grant",
        BrowserAuthorizationDenial.ScopeInsufficient => "scope_insufficient",
        BrowserAuthorizationDenial.TargetMismatch => "target_mismatch",
        BrowserAuthorizationDenial.FrameMismatch => "frame_mismatch",
        BrowserAuthorizationDenial.GrantRevoked => "grant_revoked",
        BrowserAuthorizationDenial.GrantExpired => "grant_expired",
        BrowserAuthorizationDenial.BindingMismatch => "binding_mismatch",
        BrowserAuthorizationDenial.EpochStale => "epoch_stale",
        BrowserAuthorizationDenial.LeaseLost => "lease_lost",
        BrowserAuthorizationDenial.PageBusy => "page_busy",
        BrowserAuthorizationDenial.CallerMismatch => "caller_mismatch",
        BrowserAuthorizationDenial.UntrustedCaller => "untrusted_caller",
        BrowserAuthorizationDenial.MissingTarget => "missing_target",
        BrowserAuthorizationDenial.UnknownOperation => "unknown_operation",
        _ => throw new ArgumentOutOfRangeException(
            nameof(denial), denial, "Authorization denial is not registered."),
    };

    /// <summary>严格解析：不知道的线名不猜测、不回退。</summary>
    public static bool TryParse(string? name, out BrowserAuthorizationDenial denial)
    {
        foreach (var candidate in Enum.GetValues<BrowserAuthorizationDenial>())
        {
            if (string.Equals(NameOf(candidate), name, StringComparison.Ordinal))
            {
                denial = candidate;
                return true;
            }
        }

        denial = default;
        return false;
    }

    /// <summary>全部已登记原因码（快照断言用，顺序即声明顺序）。</summary>
    public static IReadOnlyList<BrowserAuthorizationDenial> All { get; } =
        Enum.GetValues<BrowserAuthorizationDenial>();
}
