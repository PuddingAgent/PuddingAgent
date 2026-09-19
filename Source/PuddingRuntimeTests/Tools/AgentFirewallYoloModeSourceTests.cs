using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingCode.Tools;
using PuddingRuntime.Services;

namespace PuddingRuntimeTests.Tools;

/// <summary>
/// 防火墙权威运行模式源统一回归测试。
/// 背景：Gate 1（ModeGate）以 _runtime.Mode 为权威源，但 Gate 4/6/7 曾直接读
/// ctx.RuntimeMode（hint）。调用方漏传 mode 时 hint 停留在 Normal，YOLO 在这些
/// gate 静默失效——高危工具被审批拦截。修复后 EvaluateAsync 开头解析一次权威
/// 模式，Gate 1/4/6/7 消费同一个值。
/// </summary>
[TestClass]
public sealed class AgentFirewallYoloModeSourceTests
{
    private static readonly ToolDescriptor HighRiskDescriptor = new()
    {
        ToolId = "file_write",
        Name = "File write",
        Description = "High-risk write tool used by firewall mode-source tests.",
        PermissionLevel = ToolPermissionLevel.Medium,
    };

    private static FirewallContext BuildContext(string toolId) => new()
    {
        WorkspaceId = "default",
        SessionId = "session",
        AgentInstanceId = "agent",
        ToolId = toolId,
        ArgumentsJson = "{}",
        // 模拟事故现场的调用方：显式传了 stale/默认的 Normal hint。
        RuntimeMode = RuntimeExecutionMode.Normal,
    };

    [TestMethod]
    public async Task Gate4_YoloAuthoritativeMode_SkipsAuthorization_EvenWhenHintSaysNormal()
    {
        var authz = new CountingAuthzService();
        var approval = new CountingApprovalService();
        var firewall = new AgentFirewall(
            runtime: new StubRuntime { Mode = RuntimeExecutionMode.Yolo },
            policySvc: new AlwaysRequiresAuthzPolicyService(),
            toolRegistry: new SingleDescriptorToolRegistry(HighRiskDescriptor),
            authzSvc: authz,
            approvalSvc: approval);

        var decision = await firewall.EvaluateAsync(
            BuildContext("file_write"), CancellationToken.None);

        Assert.IsTrue(decision.Allowed, decision.DenyReason);
        Assert.AreEqual(0, authz.CheckCalls, "YOLO 权威模式下 Gate 4 不得调用授权服务");
        Assert.AreEqual(0, approval.CheckCalls, "YOLO 权威模式下 Gate 4 不得调用审批服务");
    }

    [TestMethod]
    public async Task Gate4_NormalMode_StillInvokesAuthorization_AndDenies()
    {
        // 对照组：hint 与权威源一致（Normal）时，authz 路径必须仍然生效并拒绝，
        // 证明上面的 YOLO 放行不是 stub 环境本身放行。
        var authz = new CountingAuthzService();
        var approval = new CountingApprovalService();
        var firewall = new AgentFirewall(
            runtime: new StubRuntime { Mode = RuntimeExecutionMode.Normal },
            policySvc: new AlwaysRequiresAuthzPolicyService(),
            toolRegistry: new SingleDescriptorToolRegistry(HighRiskDescriptor),
            authzSvc: authz,
            approvalSvc: approval);

        var decision = await firewall.EvaluateAsync(
            BuildContext("file_write"), CancellationToken.None);

        Assert.IsFalse(decision.Allowed);
        Assert.AreEqual(FirewallGate.Authorization, decision.DeniedAtGate);
        Assert.AreEqual(1, authz.CheckCalls, "Normal 权威模式下 Gate 4 必须调用授权服务");
    }

    [TestMethod]
    public async Task Gate7_YoloAuthoritativeMode_SkipsResourcePolicyChecks()
    {
        var firewall = new AgentFirewall(
            runtime: new StubRuntime { Mode = RuntimeExecutionMode.Yolo });

        var decision = await firewall.EvaluateAsync(new FirewallContext
        {
            WorkspaceId = "default",
            SessionId = "session",
            AgentInstanceId = "agent",
            ToolId = "shell",
            ArgumentsJson = "{}",
            Policy = new CapabilityPolicy { AllowShellExecution = false },
            RuntimeMode = RuntimeExecutionMode.Normal,
        }, CancellationToken.None);

        Assert.IsTrue(decision.Allowed, decision.DenyReason);
    }

    [TestMethod]
    public void FromExecutionContext_NullMode_FallsBackToNormal_ExplicitModeWins()
    {
        var context = new ToolExecutionContext
        {
            WorkspaceId = "default",
            SessionId = "session",
            AgentInstanceId = "agent",
        };

        var fallback = FirewallContext.FromExecutionContext(context, mode: null);
        var explicitYolo = FirewallContext.FromExecutionContext(
            context, mode: RuntimeExecutionMode.Yolo);

        Assert.AreEqual(RuntimeExecutionMode.Normal, fallback.RuntimeMode);
        Assert.AreEqual(RuntimeExecutionMode.Yolo, explicitYolo.RuntimeMode);
    }

    private sealed class StubRuntime : IRuntimeControlService
    {
        public RuntimeExecutionMode Mode { get; init; }

        public CancellationToken GetSessionCancellationToken(string sessionId) => CancellationToken.None;

        public RuntimeControlDecision CanAcceptUserMessage(string? sessionId)
            => throw new NotSupportedException();

        public RuntimeControlDecision CanStartAgent(string sessionId)
            => throw new NotSupportedException();

        public RuntimeControlDecision CanInvokeTool(string sessionId, string toolName)
            => throw new NotSupportedException();

        public void MarkSessionRunning(string sessionId) => throw new NotSupportedException();

        public void MarkSessionWaitingForTool(string sessionId) => throw new NotSupportedException();

        public void MarkSessionCompleted(string sessionId) => throw new NotSupportedException();

        public void MarkSessionStopped(string sessionId) => throw new NotSupportedException();

        public void MarkProgress(string sessionId) => throw new NotSupportedException();

        public RuntimeFuseResult RecordError(
            string sessionId, RuntimeErrorKind kind, string component, string message)
            => throw new NotSupportedException();

        public RuntimeControlActionResult ResetSessionFault(string sessionId)
            => throw new NotSupportedException();

        public RuntimeControlActionResult StopSession(string sessionId, string reason)
            => throw new NotSupportedException();

        public RuntimeControlActionResult StopAll(string reason)
            => throw new NotSupportedException();

        public RuntimeControlActionResult SetMode(RuntimeExecutionMode mode, string reason)
            => throw new NotSupportedException();

        // SessionGate 容忍 null 快照（status?.Session?.State），返回 null 表示无会话状态。
        public RuntimeStatusSnapshot GetStatus(string? sessionId = null) => null!;
    }

    private sealed class SingleDescriptorToolRegistry : IPuddingToolRegistry
    {
        private readonly ToolDescriptor _descriptor;

        public SingleDescriptorToolRegistry(ToolDescriptor descriptor) => _descriptor = descriptor;

        public IPuddingTool? GetTool(string toolId) => null;
        public ToolDescriptor? GetDescriptor(string toolId)
            => toolId == _descriptor.ToolId ? _descriptor : null;
        public IReadOnlyList<ToolDescriptor> ListDescriptors() => [_descriptor];
        public IReadOnlyList<ToolDescriptor> ListAvailable(CapabilityPolicy? policy) => [_descriptor];
    }

    private sealed class AlwaysRequiresAuthzPolicyService : IToolPermissionPolicyService
    {
        public ToolPermissionDecision Classify(ToolDescriptor descriptor)
            => throw new NotSupportedException();

        public bool RequiresRuntimeAuthorization(ToolDescriptor descriptor) => true;

        public bool CanExposeToAgent(ToolDescriptor descriptor, CapabilityPolicy? policy) => true;

        public CapabilityPolicy BuildCapabilityPolicy(
            IEnumerable<ToolDescriptor> descriptors,
            IEnumerable<string> selectedToolNames,
            bool isTaskRole)
            => throw new NotSupportedException();
    }

    private sealed class CountingAuthzService : IToolAuthorizationService
    {
        public int CheckCalls { get; private set; }

        public Task<ToolAuthorizationCommandResult> ApplyCommandAsync(
            ToolAuthorizationCommand command,
            ToolAuthorizationContext context,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<ToolAuthorizationCheckResult> CheckAsync(
            ToolAuthorizationContext context,
            ToolDescriptor descriptor,
            CancellationToken ct = default)
        {
            CheckCalls++;
            return Task.FromResult(new ToolAuthorizationCheckResult
            {
                IsAuthorized = false,
                Message = "stub denies",
            });
        }

        public string BuildRequiredMessage(string toolId, ToolDescriptor? descriptor = null)
            => "stub";
    }

    private sealed class CountingApprovalService : IToolApprovalService
    {
        public int CheckCalls { get; private set; }

        public Task<ToolApprovalTicketResult> SubmitAsync(
            ToolApprovalTicketRequest request,
            ToolApprovalIdentity identity,
            ToolDescriptor descriptor,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<ToolApprovalCheckResult> CheckAsync(
            ToolApprovalExecutionRequest request,
            ToolDescriptor descriptor,
            CancellationToken ct = default)
        {
            CheckCalls++;
            return Task.FromResult(new ToolApprovalCheckResult
            {
                IsApproved = false,
                Message = "stub denies",
            });
        }
    }
}
