using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using PuddingBrowser.Authorization;

namespace PuddingBrowser.AuthorizationTests;

/// <summary>
/// 目标会话状态机的不变式（组件化交付规程 S2/S3）。
/// 每条断言都对应一个**必须能失败**的观察：破坏实现里的对应判断，测试即变红。
/// </summary>
[TestClass]
public sealed class AgentTargetSessionAuthorityTests
{
    private static readonly DesktopPageTarget PageA = new("ctx-1", "page-a");
    private static readonly DesktopPageTarget PageB = new("ctx-1", "page-b");
    private static readonly ConnectionGeneration Gen9 = ConnectionGeneration.Require(9);

    private static AgentTargetSessionAuthority Ready()
    {
        var authority = new AgentTargetSessionAuthority();
        authority.OnBridgeReady(Gen9);
        return authority;
    }

    private static AgentTargetSessionAuthority ReadyWith(DesktopPageTarget target)
    {
        var authority = Ready();
        authority.SelectTarget(target);
        return authority;
    }

    // ── 传输生命周期：这就是历史上缺失的那一环 ───────────────────────────

    [TestMethod]
    public void AuthorityIsNotLiveBeforeTheHandshake()
    {
        var authority = new AgentTargetSessionAuthority();

        Assert.IsFalse(authority.IsBridgeReady);

        var decision = authority.Authorize(new AgentTargetRequest(PageA, AgentTargetOperation.Observe));
        Assert.IsFalse(decision.IsAllowed);
        Assert.AreEqual(AgentTargetErrorCodes.TransportUnavailable, decision.ErrorCode);
    }

    [TestMethod]
    public void HandshakeActivatesTheAuthorityAndKeepsTheNotifiedGeneration()
    {
        var authority = new AgentTargetSessionAuthority();

        var snapshot = authority.OnBridgeReady(Gen9);

        Assert.IsTrue(snapshot.IsBridgeReady);
        Assert.AreEqual(9, snapshot.BridgeGeneration.Value);
        Assert.IsTrue(snapshot.Describe().Contains("Connected(gen=9)", StringComparison.Ordinal));

        // 桥活了但用户还没选页 ⇒ 必须是「需要选页」，不是「传输不可用」。
        var decision = authority.Authorize(new AgentTargetRequest(PageA, AgentTargetOperation.Observe));
        Assert.AreEqual(AgentTargetErrorCodes.TargetRequired, decision.ErrorCode);
    }

    [TestMethod]
    public void SelectingATargetBeforeTheBridgeIsReadyProducesNoTarget()
    {
        var authority = new AgentTargetSessionAuthority();

        var snapshot = authority.SelectTarget(PageA);

        Assert.IsFalse(snapshot.HasTarget);
        Assert.AreEqual(AgentTargetErrorCodes.TransportUnavailable, snapshot.Reason);
    }

    [TestMethod]
    public void BridgeLossRevokesTheTarget()
    {
        var authority = ReadyWith(PageA);
        Assert.IsTrue(authority.Capture().HasTarget);

        var snapshot = authority.OnBridgeLost("socket closed");

        Assert.IsFalse(snapshot.HasTarget);
        Assert.IsFalse(snapshot.IsBridgeReady);
        var decision = authority.Authorize(new AgentTargetRequest(PageA, AgentTargetOperation.Observe));
        Assert.AreEqual(AgentTargetErrorCodes.TransportUnavailable, decision.ErrorCode);
    }

    [TestMethod]
    public void GenerationChangeRevokesTheTarget()
    {
        var authority = ReadyWith(PageA);

        var snapshot = authority.OnBridgeReady(ConnectionGeneration.Require(10));

        Assert.IsFalse(snapshot.HasTarget);
        var decision = authority.Authorize(new AgentTargetRequest(PageA, AgentTargetOperation.Observe));
        Assert.AreEqual(AgentTargetErrorCodes.TargetRevoked, decision.ErrorCode);
    }

    // ── 用户的选页是唯一授权来源 ────────────────────────────────────────

    [TestMethod]
    public void SelectingTheSamePageTwiceIsIdempotent()
    {
        var authority = Ready();

        var first = authority.SelectTarget(PageA);
        var second = authority.SelectTarget(PageA);

        Assert.IsTrue(second.HasTarget);
        Assert.AreEqual(first.Session!.SessionId.Value, second.Session!.SessionId.Value);
    }

    [TestMethod]
    public void SelectingAnotherPageAtomicallyReplacesTheSession()
    {
        var authority = ReadyWith(PageA);
        var old = authority.Capture().Session!;

        var snapshot = authority.SelectTarget(PageB);

        Assert.IsTrue(snapshot.HasTarget);
        Assert.AreEqual(PageB.Key, snapshot.Session!.Target.Key);
        Assert.AreNotEqual(old.SessionId.Value, snapshot.Session.SessionId.Value);
        Assert.AreEqual(old.SessionId.Value, snapshot.LastRevokedSessionId!.Value.Value);
    }

    [TestMethod]
    public void APageTheUserDidNotSelectIsRejected()
    {
        var authority = ReadyWith(PageA);

        var decision = authority.Authorize(new AgentTargetRequest(PageB, AgentTargetOperation.Observe));

        Assert.IsFalse(decision.IsAllowed);
        Assert.AreEqual(AgentTargetErrorCodes.TargetRequired, decision.ErrorCode);
    }

    [TestMethod]
    public void RevokedTargetReportsRevokedRatherThanRequired()
    {
        var authority = ReadyWith(PageA);

        var snapshot = authority.Revoke("user cleared the target");
        Assert.IsFalse(snapshot.HasTarget);

        var decision = authority.Authorize(new AgentTargetRequest(PageA, AgentTargetOperation.Observe));
        Assert.AreEqual(AgentTargetErrorCodes.TargetRevoked, decision.ErrorCode);
    }

    // ── 接管是运行控制，不是权限审批 ────────────────────────────────────

    [TestMethod]
    public void TakeoverBlocksSideEffectsButKeepsObservation()
    {
        var authority = ReadyWith(PageA);

        authority.BeginUserControl("user is typing");

        var observe = authority.Authorize(new AgentTargetRequest(PageA, AgentTargetOperation.Observe));
        Assert.IsTrue(observe.IsAllowed, "接管不撤销阅读（69 规格 §1.1）。");

        var interact = authority.Authorize(new AgentTargetRequest(PageA, AgentTargetOperation.Interact));
        Assert.IsFalse(interact.IsAllowed);
        Assert.AreEqual(AgentTargetErrorCodes.Busy, interact.ErrorCode);

        // 接管期间**保留目标**：交还控制权后无需重新选页。
        authority.EndUserControl();
        Assert.IsTrue(authority.Capture().HasTarget);
        Assert.IsTrue(authority.Authorize(new AgentTargetRequest(PageA, AgentTargetOperation.Interact)).IsAllowed);
    }

    // ── 高风险确认与写串行化 ────────────────────────────────────────────

    [TestMethod]
    public void HighRiskOperationsNeedAOneTimeConfirmation()
    {
        var authority = ReadyWith(PageA);

        var without = authority.Authorize(new AgentTargetRequest(PageA, AgentTargetOperation.HighRiskAction));
        Assert.IsFalse(without.IsAllowed);
        Assert.AreEqual(AgentTargetErrorCodes.UserConfirmationRequired, without.ErrorCode);

        var with = authority.Authorize(new AgentTargetRequest(PageA, AgentTargetOperation.HighRiskAction, hasUserConfirmation: true));
        Assert.IsTrue(with.IsAllowed);
    }

    [TestMethod]
    public void OnlyOneMutatingOperationMayBeInFlight()
    {
        var authority = ReadyWith(PageA);

        var first = authority.Authorize(new AgentTargetRequest(PageA, AgentTargetOperation.Mutate));
        Assert.IsTrue(first.IsAllowed);

        var second = authority.Authorize(new AgentTargetRequest(PageA, AgentTargetOperation.Mutate));
        Assert.IsFalse(second.IsAllowed);
        Assert.AreEqual(AgentTargetErrorCodes.Busy, second.ErrorCode);

        authority.CompleteOperation(first.Permit!);
        Assert.IsTrue(authority.Authorize(new AgentTargetRequest(PageA, AgentTargetOperation.Mutate)).IsAllowed);
    }

    // ── 竞态：判定通过到触碰页面之间发生的事必须被拦下 ───────────────────

    [TestMethod]
    public void AStalePermitCannotSurviveATargetSwitch()
    {
        var authority = ReadyWith(PageA);
        var permit = authority.Authorize(new AgentTargetRequest(PageA, AgentTargetOperation.Mutate)).Permit!;

        authority.SelectTarget(PageB);

        var decision = authority.Revalidate(permit);
        Assert.IsFalse(decision.IsAllowed);
        Assert.AreEqual(AgentTargetErrorCodes.TargetRevoked, decision.ErrorCode);
    }

    [TestMethod]
    public void AQueuedMutationCannotSurviveATakeover()
    {
        var authority = ReadyWith(PageA);
        var permit = authority.Authorize(new AgentTargetRequest(PageA, AgentTargetOperation.Mutate)).Permit!;

        authority.BeginUserControl("user grabbed the mouse");

        var decision = authority.Revalidate(permit);
        Assert.IsFalse(decision.IsAllowed);
        Assert.AreEqual(AgentTargetErrorCodes.Busy, decision.ErrorCode);
    }

    [TestMethod]
    public void AnObservationPermitSurvivesATakeover()
    {
        var authority = ReadyWith(PageA);
        var permit = authority.Authorize(new AgentTargetRequest(PageA, AgentTargetOperation.Observe)).Permit!;

        authority.BeginUserControl("user grabbed the mouse");

        Assert.IsTrue(authority.Revalidate(permit).IsAllowed);
    }

    [TestMethod]
    public void AStalePermitCannotSurviveAGenerationChange()
    {
        var authority = ReadyWith(PageA);
        var permit = authority.Authorize(new AgentTargetRequest(PageA, AgentTargetOperation.Mutate)).Permit!;

        authority.OnBridgeReady(ConnectionGeneration.Require(11));

        var decision = authority.Revalidate(permit);
        Assert.IsFalse(decision.IsAllowed);
        Assert.AreEqual(AgentTargetErrorCodes.TransportUnavailable, decision.ErrorCode);
    }

    [TestMethod]
    public void CloseBlocksEverything()
    {
        var authority = ReadyWith(PageA);

        authority.Close("desktop shutting down");

        Assert.IsFalse(authority.Capture().HasTarget);
        var decision = authority.Authorize(new AgentTargetRequest(PageA, AgentTargetOperation.Observe));
        Assert.AreEqual(AgentTargetErrorCodes.Closed, decision.ErrorCode);
    }

    [TestMethod]
    public void EveryDenialMapsToAStableCode()
    {
        var authority = new AgentTargetSessionAuthority();

        var decision = authority.Authorize(new AgentTargetRequest(PageA, AgentTargetOperation.Mutate));

        Assert.IsNotNull(decision.ErrorCode);
        Assert.AreEqual(AgentTargetErrorCodes.TransportUnavailable, decision.ErrorCode);
        Assert.IsNull(decision.Permit);
    }
}
