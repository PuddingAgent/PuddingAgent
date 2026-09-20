using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Abstractions;
using PuddingCode.Classification;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingCode.Tools;
using PuddingRuntime.Classification;
using PuddingRuntime.Services;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Tools;

/// <summary>
/// S5b：完全访问授予接入审批闸门（AgentFirewall Gate 4 / AuthorizationGate）的离线回归测试。
/// <para>
/// 覆盖映射：
/// ① 无授予 ⇒ 行为与基线一致（deny at Authorization，授权与审批各被调用一次）；
/// ② 有效授予 ⇒ Gate 4 放行且落 FullAccessGateBypass 审计；
/// ③ 授予放行绝不记成分类器裁定（审批闸门未被调用、无 ClassifierInvoked / ImplicitApproved、
///    事件 ClassifierId=null 且 ReviewerModel=full-access-grant）；
/// ④ 授予到期（服务端假时钟推进）⇒ 放行消失、回到基线并落 FullAccessExpired；
/// ⑤ 撤销 ⇒ 立即失效；
/// ⑥ 跨 agent_instance 作用域隔离；
/// ⑦ 跨 workspace 作用域隔离；
/// ⑧ 授予不放宽资源边界（授予 ≠ Yolo：ResourceGate 仍拒绝）；
/// ⑨ PuddingToolExecutionService 内建防火墙端到端：授予 ⇒ 执行成功且 IsYoloMode 保持 false；
/// ⑩ PuddingToolExecutionService 内建防火墙端到端：无授予 ⇒ 403 基线。
/// 全部零网络：注入假时钟、内存审计 store 与 stub 服务，不接触任何真实模型。
/// </para>
/// </summary>
[TestClass]
public sealed class AgentFirewallFullAccessGrantTests
{
    private const string WorkspaceA = "ws-a";
    private const string WorkspaceB = "ws-b";
    private const string AgentA = "agent-a";
    private const string AgentB = "agent-b";
    private const string GrantId1 = "grant-1";
    private static readonly DateTimeOffset T0 = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    // —— ① 无授予 ⇒ 行为与基线完全一致（钉死「默认不变」） ——

    [TestMethod]
    public async Task Evaluate_WithoutGrant_BaselineDeniesAtAuthorization()
    {
        var (firewall, audit, _, _, authz, approval) = CreateFirewall();

        var decision = await firewall.EvaluateAsync(BuildContext(), CancellationToken.None);

        Assert.IsFalse(decision.Allowed, "无授予 ⇒ 必须保持基线拒绝。");
        Assert.AreEqual(FirewallGate.Authorization, decision.DeniedAtGate);
        Assert.AreEqual(1, authz.CheckCalls, "基线路径：授权服务必须被调用。");
        Assert.AreEqual(1, approval.CheckCalls, "基线路径：审批服务必须被调用。");
        Assert.IsFalse(
            (await audit.ListAsync()).Any(e => e.EventType == ToolApprovalAuditEventType.FullAccessGateBypass),
            "无授予 ⇒ 不得有授予放行审计。");
    }

    // —— ② 有效授予 ⇒ Gate 4 放行 + FullAccessGateBypass 审计 ——

    [TestMethod]
    public async Task Evaluate_WithActiveGrant_AllowsAndAuditsFullAccessGateBypass()
    {
        var (firewall, audit, grants, _, _, _) = CreateFirewall();
        await grants.GrantAsync(GrantRequest());

        var decision = await firewall.EvaluateAsync(BuildContext(), CancellationToken.None);

        Assert.IsTrue(decision.Allowed, decision.DenyReason);
        var events = await audit.ListAsync();
        var bypass = events.Single(e => e.EventType == ToolApprovalAuditEventType.FullAccessGateBypass);
        Assert.AreEqual(GrantId1, bypass.TicketId, "bypass 审计的 TicketId 必须携带 GrantId（审计溯源）。");
        Assert.AreEqual("file_write", bypass.ToolId);
        Assert.AreEqual(WorkspaceA, bypass.WorkspaceId);
        Assert.AreEqual(AgentA, bypass.AgentInstanceId);
        Assert.AreEqual(ToolApprovalDecision.Approved, bypass.Decision);
    }

    // —— ③ 授予放行绝不记成分类器裁定（审计不可伪造批准） ——

    [TestMethod]
    public async Task GrantBypass_IsNeverRecordedAsClassifierVerdict()
    {
        var (firewall, audit, grants, _, _, approval) = CreateFirewall();
        await grants.GrantAsync(GrantRequest());

        var decision = await firewall.EvaluateAsync(BuildContext(), CancellationToken.None);

        Assert.IsTrue(decision.Allowed, decision.DenyReason);
        Assert.AreEqual(0, approval.CheckCalls, "授予放行 ⇒ 审批闸门（含分类器链路）不得被执行。");
        var events = await audit.ListAsync();
        Assert.IsFalse(events.Any(e => e.EventType == ToolApprovalAuditEventType.ClassifierInvoked), "授予放行不得产生分类器裁定审计。");
        Assert.IsFalse(events.Any(e => e.EventType == ToolApprovalAuditEventType.ImplicitApproved), "授予放行不得记成隐式审批。");
        var bypass = events.Single(e => e.EventType == ToolApprovalAuditEventType.FullAccessGateBypass);
        Assert.IsNull(bypass.ClassifierId, "bypass 事件不得携带分类器 id。");
        Assert.IsNull(bypass.ClassifierConfidence, "bypass 事件不得携带分类器可信度。");
        Assert.AreEqual("full-access-grant", bypass.ReviewerModel, "ReviewerModel 必须标明授予来源而非分类器。");
    }

    // —— ④ 授予到期（假时钟推进）⇒ 放行消失、回到基线 ——

    [TestMethod]
    public async Task Evaluate_AfterGrantExpired_FallsBackToBaselineDeny()
    {
        var (firewall, audit, grants, clock, _, _) = CreateFirewall();
        await grants.GrantAsync(GrantRequest());
        clock.Advance(TimeSpan.FromSeconds(301));

        var decision = await firewall.EvaluateAsync(BuildContext(), CancellationToken.None);

        Assert.IsFalse(decision.Allowed, "到期后放行必须消失。");
        Assert.AreEqual(FirewallGate.Authorization, decision.DeniedAtGate);
        var events = await audit.ListAsync();
        Assert.IsTrue(events.Any(e => e.EventType == ToolApprovalAuditEventType.FullAccessExpired), "到期被观测到时必须落 Expired 审计。");
        Assert.IsFalse(events.Any(e => e.EventType == ToolApprovalAuditEventType.FullAccessGateBypass), "到期 ⇒ 不得有任何授予放行审计。");
    }

    // —— ⑤ 撤销 ⇒ 立即失效 ——

    [TestMethod]
    public async Task Evaluate_AfterGrantRevoked_ImmediatelyFallsBackToBaseline()
    {
        var (firewall, _, grants, _, _, _) = CreateFirewall();
        var granted = await grants.GrantAsync(GrantRequest());

        var allowed = await firewall.EvaluateAsync(BuildContext(), CancellationToken.None);
        Assert.IsTrue(allowed.Allowed, "前置：授予生效期内必须放行。");

        Assert.IsTrue(await grants.RevokeAsync(granted.GrantId), "撤销必须成功。");
        var after = await firewall.EvaluateAsync(BuildContext(), CancellationToken.None);
        Assert.IsFalse(after.Allowed, "撤销后必须立即失效。");
        Assert.AreEqual(FirewallGate.Authorization, after.DeniedAtGate);
    }

    // —— ⑥ 作用域隔离：跨 agent_instance ——

    [TestMethod]
    public async Task Grant_IsIsolatedAcrossAgentInstances()
    {
        var (firewall, _, grants, _, _, _) = CreateFirewall();
        await grants.GrantAsync(GrantRequest(agentInstanceId: AgentA));

        var decision = await firewall.EvaluateAsync(BuildContext(agentInstanceId: AgentB), CancellationToken.None);

        Assert.IsFalse(decision.Allowed, "A 的授予不得影响 B（跨 agent_instance）。");
        Assert.AreEqual(FirewallGate.Authorization, decision.DeniedAtGate);
    }

    // —— ⑦ 作用域隔离：跨 workspace ——

    [TestMethod]
    public async Task Grant_IsIsolatedAcrossWorkspaces()
    {
        var (firewall, _, grants, _, _, _) = CreateFirewall();
        await grants.GrantAsync(GrantRequest(workspaceId: WorkspaceA));

        var decision = await firewall.EvaluateAsync(BuildContext(workspaceId: WorkspaceB), CancellationToken.None);

        Assert.IsFalse(decision.Allowed, "ws-a 的授予不得影响 ws-b（跨 workspace）。");
        Assert.AreEqual(FirewallGate.Authorization, decision.DeniedAtGate);
    }

    // —— ⑧ 授予只放宽审批闸门：资源边界不被放宽（授予 ≠ Yolo） ——

    [TestMethod]
    public async Task ActiveGrant_DoesNotRelaxResourceBoundary()
    {
        var (firewall, audit, grants, _, _, _) = CreateFirewall();
        await grants.GrantAsync(GrantRequest());

        // shell + AllowShellExecution=false：若授予被错误实现成 Yolo 级别，
        // Gate 7（ResourceGate）会被跳过并整体放行；正确行为 = Gate 4 放行、Gate 7 仍拒绝。
        var decision = await firewall.EvaluateAsync(
            BuildContext(toolId: "shell", policy: new CapabilityPolicy { AllowShellExecution = false }),
            CancellationToken.None);

        Assert.IsFalse(decision.Allowed, "授予不得放宽资源边界。");
        Assert.AreEqual(FirewallGate.Resource, decision.DeniedAtGate);
        Assert.AreEqual(
            1,
            (await audit.ListAsync()).Count(e => e.EventType == ToolApprovalAuditEventType.FullAccessGateBypass),
            "Gate 4 的授予放行审计应恰好落一条（其后被 Gate 7 拒绝）。");
    }

    // —— ⑨ 端到端：ExecutionService 内建防火墙 + 授予 ⇒ 执行成功，IsYoloMode 保持 false ——

    [TestMethod]
    public async Task ExecutionService_InternalFirewall_AllowsEndToEndWithGrant_KeepsYoloFalse()
    {
        var (execution, audit, tool) = CreateExecutionService(withGrant: true);

        var result = await execution.ExecuteAsync(
            "file_write",
            "{}",
            new ToolExecutionContext { WorkspaceId = WorkspaceA, SessionId = "s1", AgentInstanceId = AgentA },
            policy: null);

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual("stub-ok", result.Output);
        Assert.IsNotNull(tool.ObservedIsYoloMode, "工具必须被执行到。");
        Assert.IsFalse(tool.ObservedIsYoloMode.Value, "授予不得翻转 IsYoloMode（Yolo 状态未被改变，读实际传值）。");
        Assert.IsTrue(
            (await audit.ListAsync()).Any(e => e.EventType == ToolApprovalAuditEventType.FullAccessGateBypass),
            "端到端放行必须落 bypass 审计。");
    }

    // —— ⑩ 端到端：无授予 ⇒ 403 基线 ——

    [TestMethod]
    public async Task ExecutionService_InternalFirewall_WithoutGrant_Denies403Baseline()
    {
        var (execution, _, tool) = CreateExecutionService(withGrant: false);

        var result = await execution.ExecuteAsync(
            "file_write",
            "{}",
            new ToolExecutionContext { WorkspaceId = WorkspaceA, SessionId = "s1", AgentInstanceId = AgentA },
            policy: null);

        Assert.IsFalse(result.Success, "无授予 ⇒ 必须保持基线拒绝。");
        Assert.AreEqual(403, result.ExitCode, "无授予 ⇒ 与基线一致：403 授权拒绝。");
        Assert.IsNull(tool.ObservedIsYoloMode, "工具不得被执行。");
    }

    // —— 测试辅助 ——

    private static AgentFullAccessGrant GrantRequest(
        string workspaceId = WorkspaceA,
        string agentInstanceId = AgentA,
        string grantId = GrantId1)
        => new()
        {
            GrantId = grantId,
            WorkspaceId = workspaceId,
            AgentInstanceId = agentInstanceId,
            SessionId = null,
            GrantedAtUtc = T0,
            ExpiresAtUtc = T0.AddSeconds(300),
            GrantedByClassifierId = "llm-safety-v1",
            Outcome = ClassificationOutcome.AllowPermanent,
            Reason = "S5b firewall wiring test grant",
        };

    private static FirewallContext BuildContext(
        string workspaceId = WorkspaceA,
        string agentInstanceId = AgentA,
        string toolId = "file_write",
        CapabilityPolicy? policy = null)
        => new()
        {
            WorkspaceId = workspaceId,
            SessionId = "session",
            AgentInstanceId = agentInstanceId,
            ToolId = toolId,
            ArgumentsJson = "{}",
            Policy = policy,
            RuntimeMode = RuntimeExecutionMode.Normal,
        };

    private static (AgentFirewall Firewall, InMemoryToolApprovalAuditStore Audit, AgentFullAccessGrantService Grants, FakeClock Clock, CountingAuthzService Authz, CountingApprovalService Approval) CreateFirewall()
    {
        var audit = new InMemoryToolApprovalAuditStore();
        var clock = new FakeClock();
        var grants = new AgentFullAccessGrantService(audit, clock);
        var authz = new CountingAuthzService();
        var approval = new CountingApprovalService();
        var firewall = new AgentFirewall(
            runtime: new StubRuntime { Mode = RuntimeExecutionMode.Normal },
            policySvc: new AlwaysRequiresAuthzPolicyService(),
            toolRegistry: new StubToolRegistry(new StubTool()),
            authzSvc: authz,
            approvalSvc: approval,
            fullAccessGrants: grants,
            approvalAuditStore: audit);
        return (firewall, audit, grants, clock, authz, approval);
    }

    private static (PuddingToolExecutionService Execution, InMemoryToolApprovalAuditStore Audit, StubTool Tool) CreateExecutionService(bool withGrant)
    {
        var audit = new InMemoryToolApprovalAuditStore();
        var clock = new FakeClock();
        var grants = new AgentFullAccessGrantService(audit, clock);
        if (withGrant)
        {
            grants.GrantAsync(GrantRequest()).GetAwaiter().GetResult();
        }

        var tool = new StubTool();
        var execution = new PuddingToolExecutionService(
            new StubToolRegistry(tool),
            new SandboxExecutor(NullLogger<SandboxExecutor>.Instance),
            NullLogger<PuddingToolExecutionService>.Instance,
            permissionPolicy: new AlwaysRequiresAuthzPolicyService(),
            authorizationService: new CountingAuthzService(),
            approvalService: new CountingApprovalService(),
            runtimeControl: new StubRuntime { Mode = RuntimeExecutionMode.Normal },
            accessLevels: null,
            fullAccessGrants: grants,
            approvalAuditStore: audit);
        return (execution, audit, tool);
    }

    /// <summary>假时钟：时间只经注入的 TimeProvider 获取（服务端计时语义的可测性）。</summary>
    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset _now = T0;

        public void Advance(TimeSpan delta) => _now += delta;

        public override DateTimeOffset GetUtcNow() => _now;
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

        public RuntimeControlActionResult StopSession(string sessionId, string reason)
            => throw new NotSupportedException();

        public RuntimeControlActionResult StopAll(string reason)
            => throw new NotSupportedException();

        public RuntimeControlActionResult ResetSessionFault(string sessionId)
            => throw new NotSupportedException();

        public RuntimeControlActionResult SetMode(RuntimeExecutionMode mode, string reason)
            => throw new NotSupportedException();

        // SessionGate 容忍 null 快照（status?.Session?.State），返回 null 表示无会话状态。
        public RuntimeStatusSnapshot GetStatus(string? sessionId = null) => null!;
    }

    private sealed class StubTool : IPuddingTool
    {
        public bool? ObservedIsYoloMode { get; private set; }

        public ToolDescriptor Descriptor { get; } = new()
        {
            ToolId = "file_write",
            Name = "File write",
            Description = "High-risk write tool used by full-access grant wiring tests.",
            PermissionLevel = ToolPermissionLevel.Medium,
        };

        public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionRequest request, CancellationToken ct = default)
        {
            ObservedIsYoloMode = request.Context.IsYoloMode;
            return Task.FromResult(ToolExecutionResult.Ok("stub-ok"));
        }
    }

    private sealed class StubToolRegistry : IPuddingToolRegistry
    {
        private readonly StubTool _tool;
        private readonly ToolDescriptor _shellDescriptor = new()
        {
            ToolId = "shell",
            Name = "Shell",
            Description = "Shell tool used by full-access grant wiring tests.",
            PermissionLevel = ToolPermissionLevel.Medium,
        };

        public StubToolRegistry(StubTool tool) => _tool = tool;

        public IPuddingTool? GetTool(string toolId) => toolId == _tool.Descriptor.ToolId ? _tool : null;

        public ToolDescriptor? GetDescriptor(string toolId)
            => toolId == _tool.Descriptor.ToolId ? _tool.Descriptor
            : toolId == _shellDescriptor.ToolId ? _shellDescriptor
            : null;

        public IReadOnlyList<ToolDescriptor> ListDescriptors() => [_tool.Descriptor, _shellDescriptor];
        public IReadOnlyList<ToolDescriptor> ListAvailable(CapabilityPolicy? policy) => [_tool.Descriptor, _shellDescriptor];
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
