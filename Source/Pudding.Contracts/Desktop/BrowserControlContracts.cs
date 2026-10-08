using System.Globalization;

namespace Pudding.Contracts.Desktop;

/// <summary>
/// 控制世代号。接管/暂停、恢复、关闭、连接世代改变都会推进它。
///
/// 为什么需要它（方案 §3.1）：写请求要在「入队前」与「实际触碰页面前」两次验证。
/// 只比较布尔标志会漏掉「验证通过 → 期间被接管 → 又恢复」这种 ABA 竞态；
/// 世代号是单调的，所以「恢复」不会让接管前捕获的租约重新变有效。
/// </summary>
public readonly record struct BrowserControlEpoch(long Value)
{
    /// <summary>尚未初始化（<see cref="BrowserControlSnapshot"/> 不使用该值）。</summary>
    public static readonly BrowserControlEpoch None = new(0);

    /// <summary>进程内唯一 authority 的起始世代。</summary>
    public static readonly BrowserControlEpoch Initial = new(1);

    public bool IsKnown => Value > 0;

    public BrowserControlEpoch Next() => new(checked(Value + 1));

    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// 页面授权世代号。授权撤销、切目标、关闭、连接世代改变推进它；<b>接管不推进</b>
/// （方案 §3.1：接管不自动撤销阅读授权）。
/// </summary>
public readonly record struct BrowserPageGrantEpoch(long Value)
{
    public static readonly BrowserPageGrantEpoch None = new(0);

    public static readonly BrowserPageGrantEpoch Initial = new(1);

    public bool IsKnown => Value > 0;

    public BrowserPageGrantEpoch Next() => new(checked(Value + 1));

    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>执行租约 ID（系统产生）。</summary>
public sealed record BrowserAutomationLeaseId
{
    public const int MaxLength = 128;

    public BrowserAutomationLeaseId(string value) =>
        Value = ContractText.RequireIdentifier(value, MaxLength, "leaseId");

    public string Value { get; }

    public static BrowserAutomationLeaseId NewId() =>
        new(Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));

    public override string ToString() => Value;
}

/// <summary>
/// 单个写操作的执行租约：由 authority 在「入队准入」时签发，在「触碰页面前」复检，
/// 动作完成后释放。租约记录签发时的控制/授权世代 ⇒ 期间发生接管或撤权时，
/// 复检必然失败（方案 §3.1）。
///
/// 父子代理不得并发持有同页写租约；同一个页面目标同时只有一个租约。
/// </summary>
public sealed record BrowserAutomationLease
{
    public BrowserAutomationLease(
        BrowserAutomationLeaseId leaseId,
        DesktopPageTarget target,
        BrowserCallerIdentity holder,
        OperationId operationId,
        BrowserControlEpoch controlEpoch,
        BrowserPageGrantEpoch grantEpoch,
        DateTimeOffset acquiredAtUtc,
        DateTimeOffset? expiresAtUtc = null)
    {
        LeaseId = leaseId ?? throw new ArgumentNullException(nameof(leaseId));
        Target = target ?? throw new ArgumentNullException(nameof(target));
        Holder = holder ?? throw new ArgumentNullException(nameof(holder));
        OperationId = operationId ?? throw new ArgumentNullException(nameof(operationId));

        if (acquiredAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Lease acquisition time must be UTC.", nameof(acquiredAtUtc));
        }

        if (expiresAtUtc is { } expiry)
        {
            if (expiry.Offset != TimeSpan.Zero)
            {
                throw new ArgumentException("Lease expiry must be UTC.", nameof(expiresAtUtc));
            }

            if (expiry <= acquiredAtUtc)
            {
                throw new ArgumentException("Lease expiry must be after acquisition.", nameof(expiresAtUtc));
            }
        }

        ControlEpoch = controlEpoch;
        GrantEpoch = grantEpoch;
        AcquiredAtUtc = acquiredAtUtc;
        ExpiresAtUtc = expiresAtUtc;
    }

    public BrowserAutomationLeaseId LeaseId { get; }

    public DesktopPageTarget Target { get; }

    public BrowserCallerIdentity Holder { get; }

    /// <summary>持有该租约的操作；同一 operationId 重传不得换租约语义（方案 §4.3）。</summary>
    public OperationId OperationId { get; }

    /// <summary>签发时的控制世代；复检时必须与当前一致。</summary>
    public BrowserControlEpoch ControlEpoch { get; }

    /// <summary>签发时的授权世代；复检时必须与当前一致。</summary>
    public BrowserPageGrantEpoch GrantEpoch { get; }

    public DateTimeOffset AcquiredAtUtc { get; }

    public DateTimeOffset? ExpiresAtUtc { get; }

    /// <summary>租约是否允许<span>该操作</span>继续触碰页面：持有者、操作、页面都必须一致。</summary>
    public bool Allows(BrowserAutomationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return string.Equals(OperationId.Value, request.OperationId.Value, StringComparison.Ordinal)
            && string.Equals(Holder.CallerId, request.Caller.CallerId, StringComparison.Ordinal)
            && request.Target is { } target
            && string.Equals(Target.Key, target.Key, StringComparison.Ordinal);
    }

    public bool IsExpiredAt(DateTimeOffset utcNow) =>
        ExpiresAtUtc is { } expiry && utcNow >= expiry;

    public override string ToString() =>
        $"lease {LeaseId.Value} {Target.Key} op={OperationId.Value} holder={Holder.CallerId}"
        + $" @ctl{ControlEpoch.Value}/grant{GrantEpoch.Value}";
}

/// <summary>
/// 权威控制状态的一次只读快照。Bridge dispatcher、DesktopService、工作区控制器与 Shell
/// <b>都读这一份</b>（方案 §3.1：现有三份可写 takeover/paused 字段收敛为投影）。
/// </summary>
public sealed record BrowserControlSnapshot
{
    public static readonly BrowserControlSnapshot Initial = new(
        BrowserControlEpoch.Initial,
        BrowserPageGrantEpoch.Initial);

    public BrowserControlSnapshot(
        BrowserControlEpoch controlEpoch,
        BrowserPageGrantEpoch grantEpoch,
        bool isPaused = false,
        bool isUserTakeover = false,
        bool isClosed = false,
        BrowserAutomationLease? activeLease = null,
        string? reason = null)
    {
        ControlEpoch = controlEpoch;
        GrantEpoch = grantEpoch;
        IsPaused = isPaused;
        IsUserTakeover = isUserTakeover;
        IsClosed = isClosed;
        ActiveLease = activeLease;
        Reason = reason is null ? null : ContractText.NormalizeDisplayText(reason, 256);
    }

    public BrowserControlEpoch ControlEpoch { get; }

    public BrowserPageGrantEpoch GrantEpoch { get; }

    public bool IsPaused { get; }

    public bool IsUserTakeover { get; }

    public bool IsClosed { get; }

    /// <summary>当前写租约；无写操作在途时为 null。</summary>
    public BrowserAutomationLease? ActiveLease { get; }

    /// <summary>最近的接管/暂停原因（诊断用，已剔除控制字符并截断）。</summary>
    public string? Reason { get; }

    /// <summary>
    /// 是否还允许<span>开始新的副作用步骤</span>。接管/暂停确认后必须为 <c>false</c>
    /// （方案 §3.1：「接管完成确认后不允许新步骤开始」）。
    /// </summary>
    public bool IsAcceptingNewSteps => !IsClosed && !IsPaused && !IsUserTakeover;

    public override string ToString() =>
        $"ctl={ControlEpoch.Value} grant={GrantEpoch.Value}"
        + (IsClosed ? " closed" : string.Empty)
        + (IsUserTakeover ? " takeover" : string.Empty)
        + (IsPaused ? " paused" : string.Empty)
        + (ActiveLease is { } lease ? $" lease={lease.LeaseId.Value}" : string.Empty);
}

/// <summary>
/// 经窄端口暴露的浏览器自动化操作（方案 §3.2 表格的左侧一列）。
/// 它决定「需要哪种页面授权」与「接管期间是否被拒绝」，而不是由能力名推断。
/// </summary>
public enum BrowserAutomationOperation
{
    /// <summary>context/tab 元数据清单（浏览器作用域，无页面目标）。</summary>
    ContextsList,

    /// <summary>只读页面状态。</summary>
    PageState,

    /// <summary>快照（DOM/可达性/正文）。</summary>
    Snapshot,

    /// <summary>定位元素。</summary>
    Locate,

    /// <summary>等待条件。</summary>
    WaitFor,

    /// <summary>截图。</summary>
    Screenshot,

    /// <summary>导航。</summary>
    Navigate,

    /// <summary>元素交互（click/fill/type/press/check/select/hover/scroll）。</summary>
    Interact,

    /// <summary>坐标输入（pointer click/move/drag）。</summary>
    CoordinateInput,

    /// <summary>页面脚本（任意脚本按可产生副作用能力准入）。</summary>
    Script,

    /// <summary>创建 context。</summary>
    ContextCreate,

    /// <summary>关闭 context。</summary>
    ContextClose,

    /// <summary>新建 tab。</summary>
    TabNew,

    /// <summary>关闭 tab。</summary>
    TabClose,
}

/// <summary>
/// 该操作需要哪一类授权（方案 §3.2）：
/// 页面操作要 <b>page grant</b>；建/关 context 与新建标签属于「允许本任务新建标签」的
/// <b>任务级 management 授权</b>（此时还没有页面可以授权）；元数据清单只需可信身份。
/// </summary>
public enum BrowserGrantKind
{
    /// <summary>只需可信 Agent 身份（浏览器作用域元数据）。</summary>
    None,

    /// <summary>需要绑定到具体页面的授权记录。</summary>
    Page,

    /// <summary>需要任务级管理授权。</summary>
    Management,
}

/// <summary>
/// 操作 → 授权类别 / 授权范围 / 租约需求 / 接管可见性的<b>唯一真源</b>（方案 §3.2 表格）。
/// 放在契约层是为了让「两传输同形」有同一份判据，而不是各自再推断一遍。
/// </summary>
public static class BrowserOperationPolicy
{
    /// <summary>所需授权类别。</summary>
    public static BrowserGrantKind RequiredGrantKind(BrowserAutomationOperation operation) => operation switch
    {
        BrowserAutomationOperation.ContextsList => BrowserGrantKind.None,
        BrowserAutomationOperation.ContextCreate
            or BrowserAutomationOperation.ContextClose
            or BrowserAutomationOperation.TabNew => BrowserGrantKind.Management,
        BrowserAutomationOperation.PageState
            or BrowserAutomationOperation.Snapshot
            or BrowserAutomationOperation.Locate
            or BrowserAutomationOperation.WaitFor
            or BrowserAutomationOperation.Screenshot
            or BrowserAutomationOperation.Navigate
            or BrowserAutomationOperation.Interact
            or BrowserAutomationOperation.CoordinateInput
            or BrowserAutomationOperation.Script
            or BrowserAutomationOperation.TabClose => BrowserGrantKind.Page,
        // 刻意不写 `_ =>`：新增操作必须被显式分类，不得静默落进某个默认类别。
        _ => throw new ArgumentOutOfRangeException(
            nameof(operation), operation, "Automation operation is not registered."),
    };

    /// <summary>所需授权范围；无授权的操作返回 <see cref="BrowserPageGrantScope.None"/>。</summary>
    public static BrowserPageGrantScope RequiredScope(BrowserAutomationOperation operation) => operation switch
    {
        BrowserAutomationOperation.ContextsList => BrowserPageGrantScope.None,
        BrowserAutomationOperation.PageState => BrowserPageGrantScope.Read,
        BrowserAutomationOperation.Snapshot => BrowserPageGrantScope.Read,
        BrowserAutomationOperation.Locate => BrowserPageGrantScope.Read,
        BrowserAutomationOperation.WaitFor => BrowserPageGrantScope.Read,
        BrowserAutomationOperation.Screenshot => BrowserPageGrantScope.Read,
        BrowserAutomationOperation.Navigate => BrowserPageGrantScope.Write,
        BrowserAutomationOperation.Interact => BrowserPageGrantScope.Write,
        BrowserAutomationOperation.CoordinateInput => BrowserPageGrantScope.Write,
        BrowserAutomationOperation.Script => BrowserPageGrantScope.Write,
        BrowserAutomationOperation.ContextCreate => BrowserPageGrantScope.Manage,
        BrowserAutomationOperation.ContextClose => BrowserPageGrantScope.Manage,
        BrowserAutomationOperation.TabNew => BrowserPageGrantScope.Manage,
        // 关闭已有页不仅要 management 授权，还要**该页自己的** read 授权（方案 §3.2）。
        BrowserAutomationOperation.TabClose => BrowserPageGrantScope.Manage | BrowserPageGrantScope.Read,
        _ => throw new ArgumentOutOfRangeException(
            nameof(operation), operation, "Automation operation is not registered."),
    };

    /// <summary>是否需要显式页面目标（context/page）。</summary>
    public static bool RequiresPageTarget(BrowserAutomationOperation operation) =>
        RequiredGrantKind(operation) == BrowserGrantKind.Page;

    /// <summary>是否需要页面授权（无页面目标的操作只校验可信身份与任务级授权）。</summary>
    public static bool RequiresPageGrant(BrowserAutomationOperation operation) =>
        RequiredGrantKind(operation) == BrowserGrantKind.Page;

    /// <summary>是否会产生副作用（写或管理）。</summary>
    public static bool IsSideEffecting(BrowserAutomationOperation operation)
    {
        var scope = RequiredScope(operation);
        return scope != BrowserPageGrantScope.None && scope != BrowserPageGrantScope.Read;
    }

    /// <summary>是否只读（接管期间仍允许的观察）。</summary>
    public static bool IsReadOnly(BrowserAutomationOperation operation) => !IsSideEffecting(operation);

    /// <summary>接管/暂停期间是否被拒绝：副作用操作拒绝新动作，只读观察照常。</summary>
    public static bool IsBlockedWhileUserControlling(BrowserAutomationOperation operation) =>
        IsSideEffecting(operation);

    /// <summary>是否需要当前控制租约（只有页面写操作需要串行化）。</summary>
    public static bool RequiresControlLease(BrowserAutomationOperation operation) =>
        RequiredScope(operation) == BrowserPageGrantScope.Write;

    /// <summary>全部已登记操作（快照断言用）。</summary>
    public static IReadOnlyList<BrowserAutomationOperation> All { get; } =
        Enum.GetValues<BrowserAutomationOperation>();
}

/// <summary>
/// 一次准入/复检请求。身份与世代来自 Core 的可信执行上下文，不由模型填写。
/// </summary>
public sealed record BrowserAutomationRequest
{
    public BrowserAutomationRequest(
        BrowserAutomationOperation operation,
        BrowserCallerIdentity caller,
        OperationId operationId,
        ConnectionGeneration connectionGeneration,
        DesktopPageTarget? target = null,
        string? frameId = null)
    {
        if (!Enum.IsDefined(operation))
        {
            throw new ArgumentOutOfRangeException(nameof(operation), operation, "Automation operation is not registered.");
        }

        Operation = operation;
        Caller = caller ?? throw new ArgumentNullException(nameof(caller));
        OperationId = operationId ?? throw new ArgumentNullException(nameof(operationId));
        ConnectionGeneration = connectionGeneration;
        Target = target;
        FrameId = frameId is null
            ? null
            : ContractText.RequireIdentifier(frameId, BrowserPageGrant.MaxFrameIdLength, nameof(frameId));

        if (BrowserOperationPolicy.RequiresPageTarget(operation) && target is null)
        {
            throw new ArgumentException(
                $"Operation '{operation}' requires an explicit page target.", nameof(target));
        }
    }

    public BrowserAutomationOperation Operation { get; }

    public BrowserCallerIdentity Caller { get; }

    public OperationId OperationId { get; }

    public ConnectionGeneration ConnectionGeneration { get; }

    public DesktopPageTarget? Target { get; }

    /// <summary>frame 身份绑定；为 null 表示顶层文档。</summary>
    public string? FrameId { get; }

    public BrowserPageGrantScope RequiredScope => BrowserOperationPolicy.RequiredScope(Operation);

    public bool RequiresControlLease => BrowserOperationPolicy.RequiresControlLease(Operation);

    public override string ToString() =>
        $"{Operation} {(Target is { } target ? target.Key : "-")} op={OperationId.Value} by {Caller}";
}

/// <summary>准入/复检结论。拒绝时携带领域错误与结构化原因码。</summary>
public sealed record BrowserAuthorizationDecision
{
    private BrowserAuthorizationDecision(
        bool isAllowed,
        BrowserAuthorizationDenial denial,
        DesktopCapabilityError? error,
        BrowserControlSnapshot control,
        BrowserAutomationLease? lease)
    {
        IsAllowed = isAllowed;
        Denial = denial;
        Error = error;
        Control = control;
        Lease = lease;
    }

    public bool IsAllowed { get; }

    public bool IsDenied => !IsAllowed;

    public BrowserAuthorizationDenial Denial { get; }

    /// <summary>拒绝时的领域错误；允许时为 null。</summary>
    public DesktopCapabilityError? Error { get; }

    /// <summary>判定时观察到的控制快照（用于审计与 UI 投影）。</summary>
    public BrowserControlSnapshot Control { get; }

    /// <summary>写操作的执行租约；只读或拒绝时为 null。</summary>
    public BrowserAutomationLease? Lease { get; }

    public static BrowserAuthorizationDecision Allow(
        BrowserControlSnapshot control,
        BrowserAutomationLease? lease = null) =>
        new(true, BrowserAuthorizationDenial.None, null, control, lease);

    public static BrowserAuthorizationDecision Deny(
        BrowserAuthorizationDenial denial,
        DesktopCapabilityError error,
        BrowserControlSnapshot control) =>
        new(false, denial, error ?? throw new ArgumentNullException(nameof(error)), control, null);

    public override string ToString() =>
        IsAllowed
            ? $"allowed{(Lease is { } lease ? $" lease={lease.LeaseId.Value}" : string.Empty)}"
            : $"denied {BrowserAuthorizationDenialWire.NameOf(Denial)}: {Error?.Message}";
}

/// <summary>
/// 窄端口：控制权与页面授权的<b>唯一事实源</b>读取面。
/// Bridge dispatcher、DesktopService、工具映射与工作区控制器都通过它判定，不再各自维护标志。
/// </summary>
public interface IBrowserAutomationAuthority
{
    /// <summary>当前权威状态快照（只读投影）。</summary>
    BrowserControlSnapshot Capture();

    /// <summary>当前有效的授权记录（UI 需分别统计目标数与授权数）。</summary>
    IReadOnlyList<BrowserPageGrant> ActiveGrants { get; }

    /// <summary>当前有效的任务级管理授权。</summary>
    IReadOnlyList<BrowserManagementGrant> ActiveManagementGrants { get; }

    /// <summary>
    /// <b>第一次验证</b>：入队前。只读操作做完整校验；写操作额外签发执行租约。
    /// 拒绝时驱动调用数必须为 0（方案 G1/G2）。
    /// </summary>
    BrowserAuthorizationDecision Admit(BrowserAutomationRequest request);

    /// <summary>
    /// <b>第二次验证</b>：实际触碰页面前（与单个不可分输入步骤在同一调度边界内）。
    /// 除重新校验授权外，还要求租约记录的世代仍等于当前世代——
    /// 因此「验证通过 → 期间被接管」必然在这里被拦下。
    /// </summary>
    BrowserAuthorizationDecision Revalidate(BrowserAutomationLease lease, BrowserAutomationRequest request);
}

/// <summary>
/// 窄端口：控制权与授权的<b>写入面</b>，由 Desktop Shell（接管开关）、页面生命周期端口
/// 与连接世代变化驱动。Agent 侧只读 <see cref="IBrowserAutomationAuthority"/>，不得调用本端口。
/// </summary>
public interface IBrowserAutomationControl
{
    /// <summary>暂停/恢复工具运行时。推进控制世代并撤销在途租约。</summary>
    BrowserControlSnapshot SetPaused(bool paused, string? reason = null);

    /// <summary>
    /// 用户接管开关。推进控制世代、撤销在途租约，<b>不</b>撤销阅读授权
    /// （方案 §3.1：接管不自动撤销阅读授权）。
    /// </summary>
    BrowserControlSnapshot SetUserTakeover(bool takenOver, string? reason = null);

    /// <summary>
    /// 显式恢复控制。推进控制世代 ⇒ 接管前的队列与租约不会复活（方案 G1）。
    /// </summary>
    BrowserControlSnapshot Resume(string? reason = null);

    /// <summary>关闭浏览器表面：推进控制与授权世代，清空租约与全部 grant。</summary>
    BrowserControlSnapshot Close(string? reason = null);

    /// <summary>
    /// 连接世代变化（握手/断连）。世代改变时推进控制与授权世代，终止未执行队列的许可。
    /// </summary>
    BrowserControlSnapshot NotifyConnectionGeneration(ConnectionGeneration generation, string? reason = null);

    /// <summary>释放写租约（动作完成、取消或失败）。</summary>
    BrowserControlSnapshot ReleaseLease(BrowserAutomationLeaseId leaseId, string? reason = null);

    /// <summary>登记一条授权记录；返回登记后的授权世代。</summary>
    BrowserPageGrantEpoch IssueGrant(BrowserPageGrant grant);

    /// <summary>登记一条任务级管理授权；返回登记后的授权世代。</summary>
    BrowserPageGrantEpoch IssueManagementGrant(BrowserManagementGrant grant);

    /// <summary>撤销指定授权；推进授权世代。</summary>
    BrowserPageGrantEpoch RevokeGrant(BrowserPageGrantId grantId);

    /// <summary>撤销指定任务级管理授权；推进授权世代。</summary>
    BrowserPageGrantEpoch RevokeManagementGrant(BrowserManagementGrantId grantId);

    /// <summary>
    /// 撤销某页面的执行权（Write/Manage），保留只读授权；推进授权世代。
    /// 切换「Agent 目标」时使用（方案 §3.2）。
    /// </summary>
    BrowserPageGrantEpoch RevokeExecutionScope(DesktopPageTarget target);

    /// <summary>撤销全部授权；推进授权世代。</summary>
    BrowserPageGrantEpoch RevokeAllGrants();
}
