using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using PuddingBrowser.Automation;

namespace PuddingBrowser.AutomationTests;

/// <summary>
/// B0 测试夹具：构造权威状态机、授权记录与请求的共享工厂。
/// 全部为纯内存对象 —— 测试进程不加载宿主、驱动或 UI。
/// </summary>
internal static class B0Fixture
{
    public static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    public static readonly DesktopInstanceId DesktopId = new("desktop-test");

    public static readonly DesktopProcessInstanceId ProcessId = new("process-test");

    /// <summary>默认连接世代（模拟已握手成功）。</summary>
    public static readonly ConnectionGeneration Generation = ConnectionGeneration.Require(7);

    public static BrowserPageGrantBinding Binding(ConnectionGeneration? generation = null) =>
        new(DesktopId, ProcessId, generation ?? Generation);

    public static BrowserCallerIdentity Agent(string callerId = "agent-1", string? taskId = "task-1") =>
        new(BrowserCallerKind.Agent, callerId, taskId);

    public static BrowserCallerIdentity SubAgent(
        string callerId = "sub-1",
        string taskId = "task-sub-1",
        string parentTaskId = "task-1") =>
        new(BrowserCallerKind.SubAgent, callerId, taskId, parentTaskId);

    public static BrowserCallerIdentity User(string callerId = "user-1") =>
        new(BrowserCallerKind.User, callerId);

    public static DesktopPageTarget Page(string pageId = "page-1", string contextId = "ctx-1") =>
        new(contextId, pageId);

    /// <summary>建已握手的权威状态机；未握手时一切自动化都被 <c>not_connected</c> 拒绝。</summary>
    public static BrowserAutomationAuthority Connected(TimeProvider? timeProvider = null)
    {
        var authority = Disconnected(timeProvider);
        authority.NotifyConnectionGeneration(Generation);
        return authority;
    }

    /// <summary>未握手的权威实例（测 <c>not_connected</c>）。</summary>
    public static BrowserAutomationAuthority Disconnected(TimeProvider? timeProvider = null) =>
        new(DesktopId, ProcessId, timeProvider ?? new TestTimeProvider(Now));

    public static BrowserPageGrant Grant(
        BrowserCallerIdentity grantee,
        BrowserPageGrantScope scope = BrowserPageGrantScope.Read | BrowserPageGrantScope.Write,
        DesktopPageTarget? target = null,
        string? frameId = null,
        BrowserPageGrantBinding? binding = null,
        TimeSpan? lifetime = null,
        BrowserPageGrantEpoch? epoch = null) =>
        new(
            BrowserPageGrantId.NewId(),
            binding ?? Binding(),
            target ?? Page(),
            grantee,
            scope,
            epoch ?? BrowserPageGrantEpoch.Initial,
            Now,
            frameId,
            lifetime is { } span ? Now + span : null);

    public static BrowserManagementGrant ManagementGrant(
        BrowserCallerIdentity grantee,
        BrowserPageGrantBinding? binding = null,
        TimeSpan? lifetime = null) =>
        new(
            BrowserManagementGrantId.NewId(),
            binding ?? Binding(),
            grantee,
            Now,
            lifetime is { } span ? Now + span : null);

    public static BrowserAutomationRequest Request(
        BrowserAutomationOperation operation,
        BrowserCallerIdentity? caller = null,
        DesktopPageTarget? target = null,
        string? frameId = null,
        OperationId? operationId = null,
        ConnectionGeneration? generation = null) =>
        new(
            operation,
            caller ?? Agent(),
            operationId ?? OperationId.NewId(),
            generation ?? Generation,
            target ?? (BrowserOperationPolicy.RequiresPageTarget(operation) ? Page() : null),
            frameId);

    public static BrowserActionAssertion Assertion(
        BrowserAssertionKind kind,
        bool matched,
        string? actual = null,
        bool sensitive = false) =>
        new(kind, matched, actual, sensitive);

    public static BrowserActionEvidence Evidence(
        BrowserAutomationOperation operation,
        BrowserActionExecution execution,
        IReadOnlyList<BrowserActionAssertion>? assertions = null,
        bool requiresAssertions = false,
        bool isBlocked = false,
        bool unableToVerify = false,
        DesktopCapabilityError? error = null,
        DesktopPageTarget? target = null,
        OperationId? operationId = null) =>
        new(
            operation,
            operationId ?? OperationId.NewId(),
            execution,
            assertions,
            requiresAssertions,
            isBlocked,
            unableToVerify,
            target ?? Page(),
            new BrowserObservationStamp(new DesktopPageVersion(7), Now),
            new BrowserObservationStamp(new DesktopPageVersion(7), Now),
            BrowserActionEffectSummary.None,
            error);

    public static BrowserActionReceipt Receipt(
        BrowserAutomationOperation operation = BrowserAutomationOperation.Interact,
        BrowserActionExecution execution = BrowserActionExecution.Completed,
        BrowserActionVerification verification = BrowserActionVerification.Passed,
        bool mayHaveSideEffects = false,
        bool retryable = false,
        bool isBlocked = false,
        OperationId? operationId = null,
        DesktopCapabilityError? error = null) =>
        new(
            operationId ?? OperationId.NewId(),
            operation,
            execution,
            verification,
            Page(),
            null,
            null,
            null,
            BrowserActionEffectSummary.None,
            mayHaveSideEffects,
            retryable,
            isBlocked,
            error);
}

/// <summary>可推进的测试时钟（不引入额外包：只覆写 <see cref="TimeProvider.GetUtcNow"/>）。</summary>
internal sealed class TestTimeProvider : TimeProvider
{
    private DateTimeOffset _now;

    public TestTimeProvider(DateTimeOffset now) => _now = now;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan delta) => _now = _now + delta;
}
