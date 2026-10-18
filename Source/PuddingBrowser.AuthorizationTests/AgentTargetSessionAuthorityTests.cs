using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using PuddingBrowser.Authorization;

namespace PuddingBrowser.AuthorizationTests;

/// <summary>
/// 目标会话状态机的不变式（组件化交付规程 S2/S3）。
/// 每条断言都对应一个**必须能失败**的观察：破坏实现里的对应判断，测试即变红。
///
/// 2026-10-18 修订：按 ADR-097 D3/D4 与《一期整体实施方案》§3.2 补齐
/// 会话归属（可信控制句柄）、独立写世代、唯一票据、暂停/接管两轴与精确释放。
/// F7 的两个反例在修复前确实失败（记录见修改日志）。
/// </summary>
[TestClass]
public sealed class AgentTargetSessionAuthorityTests
{
    private static readonly DesktopPageTarget PageA = new("ctx-1", "page-a");
    private static readonly DesktopPageTarget PageB = new("ctx-1", "page-b");
    private static readonly ConnectionGeneration Gen9 = ConnectionGeneration.Require(9);

    private static readonly AgentTargetControlHandle Handle =
        AgentTargetControlHandle.For("core-session-1", "主 Agent 会话");

    private static readonly AgentTargetControlHandle OtherHandle =
        AgentTargetControlHandle.For("core-session-2", "另一个会话");

    private static AgentTargetSessionAuthority Ready()
    {
        var authority = new AgentTargetSessionAuthority();
        authority.OnBridgeReady(Gen9);
        return authority;
    }

    private static AgentTargetSessionAuthority ReadyWith(DesktopPageTarget target)
    {
        var authority = Ready();
        authority.SelectTarget(target, Handle);
        return authority;
    }

    private static AgentTargetRequest Req(
        DesktopPageTarget target,
        AgentTargetOperation operation,
        AgentTargetControlHandle? handle = null,
        bool hasUserConfirmation = false)
        => new(target, operation, handle ?? Handle, hasUserConfirmation);

    private static AgentTargetRequest ReqWithoutHandle(
        DesktopPageTarget target,
        AgentTargetOperation operation)
        => new(target, operation);

    // ── 传输生命周期：这就是历史上缺失的那一环 ───────────────────────────

    [TestMethod]
    public void AuthorityIsNotLiveBeforeTheHandshake()
    {
        var authority = new AgentTargetSessionAuthority();

        Assert.IsFalse(authority.IsBridgeReady);

        var decision = authority.Authorize(Req(PageA, AgentTargetOperation.Observe));
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
        var decision = authority.Authorize(Req(PageA, AgentTargetOperation.Observe));
        Assert.AreEqual(AgentTargetErrorCodes.TargetRequired, decision.ErrorCode);
    }

    [TestMethod]
    public void SelectingATargetBeforeTheBridgeIsReadyProducesNoTarget()
    {
        var authority = new AgentTargetSessionAuthority();

        var snapshot = authority.SelectTarget(PageA, Handle);

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
        var decision = authority.Authorize(Req(PageA, AgentTargetOperation.Observe));
        Assert.AreEqual(AgentTargetErrorCodes.TransportUnavailable, decision.ErrorCode);
    }

    [TestMethod]
    public void GenerationChangeRevokesTheTarget()
    {
        var authority = ReadyWith(PageA);

        var snapshot = authority.OnBridgeReady(ConnectionGeneration.Require(10));

        Assert.IsFalse(snapshot.HasTarget);
        var decision = authority.Authorize(Req(PageA, AgentTargetOperation.Observe));
        Assert.AreEqual(AgentTargetErrorCodes.TargetRevoked, decision.ErrorCode);
    }

    // ── 用户的选页是唯一授权来源 ────────────────────────────────────────

    [TestMethod]
    public void SelectingTheSamePageTwiceIsIdempotent()
    {
        var authority = Ready();

        var first = authority.SelectTarget(PageA, Handle);
        var second = authority.SelectTarget(PageA, Handle);

        Assert.IsTrue(second.HasTarget);
        Assert.AreEqual(first.Session!.SessionId.Value, second.Session!.SessionId.Value);
    }

    [TestMethod]
    public void SelectingAnotherPageAtomicallyReplacesTheSession()
    {
        var authority = ReadyWith(PageA);
        var old = authority.Capture().Session!;

        var snapshot = authority.SelectTarget(PageB, Handle);

        Assert.IsTrue(snapshot.HasTarget);
        Assert.AreEqual(PageB.Key, snapshot.Session!.Target.Key);
        Assert.AreNotEqual(old.SessionId.Value, snapshot.Session.SessionId.Value);
        Assert.AreEqual(old.SessionId.Value, snapshot.LastRevokedSessionId!.Value.Value);
    }

    [TestMethod]
    public void APageTheUserDidNotSelectIsRejected()
    {
        var authority = ReadyWith(PageA);

        var decision = authority.Authorize(Req(PageB, AgentTargetOperation.Observe));

        Assert.IsFalse(decision.IsAllowed);
        Assert.AreEqual(AgentTargetErrorCodes.TargetRequired, decision.ErrorCode);
    }

    [TestMethod]
    public void RevokedTargetReportsRevokedRatherThanRequired()
    {
        var authority = ReadyWith(PageA);

        var snapshot = authority.Revoke("user cleared the target");
        Assert.IsFalse(snapshot.HasTarget);

        var decision = authority.Authorize(Req(PageA, AgentTargetOperation.Observe));
        Assert.AreEqual(AgentTargetErrorCodes.TargetRevoked, decision.ErrorCode);
    }

    // ── 会话归属：宿主注入的可信控制句柄（D3 / 方案 §3.2）────────────────

    [TestMethod]
    public void MissingControlHandleIsDenied()
    {
        var authority = ReadyWith(PageA);

        var decision = authority.Authorize(ReqWithoutHandle(PageA, AgentTargetOperation.Observe));

        Assert.IsFalse(decision.IsAllowed);
        Assert.AreEqual(AgentTargetErrorCodes.ControlHandleRequired, decision.ErrorCode);
    }

    [TestMethod]
    public void SelectingTargetWithoutAControlHandleProducesNoTarget()
    {
        var authority = Ready();

        var snapshot = authority.SelectTarget(PageA, controlHandle: null);

        Assert.IsFalse(snapshot.HasTarget);
        Assert.AreEqual(AgentTargetErrorCodes.ControlHandleRequired, snapshot.Reason);
    }

    [TestMethod]
    public void ForeignOwnerCannotUseTheTarget()
    {
        var authority = ReadyWith(PageA);

        var decision = authority.Authorize(Req(PageA, AgentTargetOperation.Observe, OtherHandle));

        Assert.IsFalse(decision.IsAllowed);
        Assert.AreEqual(AgentTargetErrorCodes.OwnerMismatch, decision.ErrorCode);
    }

    [TestMethod]
    public void DisplayNameIsDiagnosticOnly()
    {
        var authority = ReadyWith(PageA);

        // 同一所有者、不同展示名 ⇒ 仍是同一会话，且可以继续使用目标。
        var sameOwnerDifferentDisplayName = AgentTargetControlHandle.For("core-session-1", "改名后的展示名");
        var snapshot = authority.SelectTarget(PageA, sameOwnerDifferentDisplayName);
        Assert.AreEqual(authority.Capture().Session!.SessionId.Value, snapshot.Session!.SessionId.Value);

        // 展示名缺失（只给所有者）同样不影响判定；句柄缺失才拒绝。
        var withoutDisplayName = AgentTargetControlHandle.For("core-session-1");
        Assert.IsTrue(authority.Authorize(Req(PageA, AgentTargetOperation.Observe, withoutDisplayName)).IsAllowed);
    }

    [TestMethod]
    public void OwnerChangeReplacesTheSessionAndInvalidatesOldPermits()
    {
        var authority = ReadyWith(PageA);
        var oldSession = authority.Capture().Session!;
        var oldPermit = authority.Authorize(Req(PageA, AgentTargetOperation.Mutate)).Permit!;

        // 另一个 Core 会话接管这一页：旧会话与旧票据一并作废。
        var snapshot = authority.SelectTarget(PageA, OtherHandle);

        Assert.IsTrue(snapshot.HasTarget);
        Assert.AreNotEqual(oldSession.SessionId.Value, snapshot.Session!.SessionId.Value);
        Assert.AreEqual(OtherHandle.Owner, snapshot.Session.Owner);
        Assert.AreEqual(oldSession.SessionId.Value, snapshot.LastRevokedSessionId!.Value.Value);

        var decision = authority.Revalidate(oldPermit);
        Assert.IsFalse(decision.IsAllowed);
        Assert.AreEqual(AgentTargetErrorCodes.TargetRevoked, decision.ErrorCode);
    }

    [TestMethod]
    public void NotifyOwnerUnavailableRevokesTheTarget()
    {
        var authority = ReadyWith(PageA);

        // 无关所有者失效 ⇒ 不影响当前目标。
        var untouched = authority.NotifyOwnerUnavailable(OtherHandle.Owner);
        Assert.IsTrue(untouched.HasTarget);

        // 所有者失效 ⇒ 收回控制权；恢复聊天不自动继承旧控制权。
        var revoked = authority.NotifyOwnerUnavailable(Handle.Owner, "core session ended");
        Assert.IsFalse(revoked.HasTarget);

        var decision = authority.Authorize(Req(PageA, AgentTargetOperation.Observe));
        Assert.AreEqual(AgentTargetErrorCodes.TargetRevoked, decision.ErrorCode);
    }

    [TestMethod]
    public void NotifyPageClosedRevokesOnlyTheMatchingTarget()
    {
        var authority = ReadyWith(PageA);

        var other = authority.NotifyPageClosed(PageB);
        Assert.IsTrue(other.HasTarget);

        var closed = authority.NotifyPageClosed(PageA);
        Assert.IsFalse(closed.HasTarget);
        Assert.AreEqual(AgentTargetErrorCodes.TargetRevoked, authority.Authorize(Req(PageA, AgentTargetOperation.Observe)).ErrorCode);
    }

    // ── 接管/暂停是运行控制，不是权限审批 ──────────────────────────────

    [TestMethod]
    public void TakeoverBlocksSideEffectsButKeepsObservation()
    {
        var authority = ReadyWith(PageA);

        authority.BeginUserControl("user is typing");

        var observe = authority.Authorize(Req(PageA, AgentTargetOperation.Observe));
        Assert.IsTrue(observe.IsAllowed, "接管不撤销阅读（69 规格 §1.1）。");

        var interact = authority.Authorize(Req(PageA, AgentTargetOperation.Interact));
        Assert.IsFalse(interact.IsAllowed);
        Assert.AreEqual(AgentTargetErrorCodes.Busy, interact.ErrorCode);

        // 接管期间**保留目标**：交还控制权后无需重新选页。
        authority.EndUserControl();
        Assert.IsTrue(authority.Capture().HasTarget);
        Assert.IsTrue(authority.Authorize(Req(PageA, AgentTargetOperation.Interact)).IsAllowed);
    }

    [TestMethod]
    public void PauseBlocksWritesButKeepsObservation()
    {
        var authority = ReadyWith(PageA);

        authority.BeginPause("user pressed pause");

        Assert.IsTrue(authority.Authorize(Req(PageA, AgentTargetOperation.Observe)).IsAllowed);
        var write = authority.Authorize(Req(PageA, AgentTargetOperation.Mutate));
        Assert.IsFalse(write.IsAllowed);
        Assert.AreEqual(AgentTargetErrorCodes.Busy, write.ErrorCode);

        authority.EndPause();
        Assert.IsTrue(authority.Authorize(Req(PageA, AgentTargetOperation.Mutate)).IsAllowed);
    }

    [TestMethod]
    public void PauseAndTakeoverAreIndependentAxes()
    {
        var authority = ReadyWith(PageA);

        authority.BeginPause("paused");
        authority.BeginUserControl("user took over");

        // 退出其中一个轴不得清除另一个轴。
        authority.EndUserControl();
        Assert.IsTrue(authority.Capture().IsPaused);
        Assert.IsFalse(authority.Capture().IsUserControlling);
        Assert.AreEqual(
            AgentTargetErrorCodes.Busy,
            authority.Authorize(Req(PageA, AgentTargetOperation.Mutate)).ErrorCode);

        authority.EndPause();
        Assert.IsFalse(authority.Capture().IsWriteBlocked);
        Assert.IsTrue(authority.Authorize(Req(PageA, AgentTargetOperation.Mutate)).IsAllowed);
    }

    // ── 高风险确认与写串行化 ────────────────────────────────────────────

    [TestMethod]
    public void HighRiskOperationsNeedAOneTimeConfirmation()
    {
        var authority = ReadyWith(PageA);

        var without = authority.Authorize(Req(PageA, AgentTargetOperation.HighRiskAction));
        Assert.IsFalse(without.IsAllowed);
        Assert.AreEqual(AgentTargetErrorCodes.UserConfirmationRequired, without.ErrorCode);

        var with = authority.Authorize(Req(PageA, AgentTargetOperation.HighRiskAction, hasUserConfirmation: true));
        Assert.IsTrue(with.IsAllowed);
    }

    [TestMethod]
    public void OnlyOneMutatingOperationMayBeInFlight()
    {
        var authority = ReadyWith(PageA);

        var first = authority.Authorize(Req(PageA, AgentTargetOperation.Mutate));
        Assert.IsTrue(first.IsAllowed);

        var second = authority.Authorize(Req(PageA, AgentTargetOperation.Mutate));
        Assert.IsFalse(second.IsAllowed);
        Assert.AreEqual(AgentTargetErrorCodes.Busy, second.ErrorCode);

        authority.CompleteOperation(first.Permit!);
        Assert.IsTrue(authority.Authorize(Req(PageA, AgentTargetOperation.Mutate)).IsAllowed);
    }

    [TestMethod]
    public void ObservationDoesNotOccupyTheWriteSlot()
    {
        var authority = ReadyWith(PageA);

        var write = authority.Authorize(Req(PageA, AgentTargetOperation.Mutate));
        Assert.IsTrue(write.IsAllowed);

        // 只读不占用写槽，也不会因写票在途而被拒。
        Assert.IsTrue(authority.Authorize(Req(PageA, AgentTargetOperation.Observe)).IsAllowed);
        Assert.IsTrue(authority.Authorize(Req(PageA, AgentTargetOperation.Observe)).IsAllowed);

        authority.CompleteOperation(write.Permit!);
    }

    // ── 竞态：判定通过到触碰页面之间发生的事必须被拦下 ───────────────────

    [TestMethod]
    public void AStalePermitCannotSurviveATargetSwitch()
    {
        var authority = ReadyWith(PageA);
        var permit = authority.Authorize(Req(PageA, AgentTargetOperation.Mutate)).Permit!;

        authority.SelectTarget(PageB, Handle);

        var decision = authority.Revalidate(permit);
        Assert.IsFalse(decision.IsAllowed);
        Assert.AreEqual(AgentTargetErrorCodes.TargetRevoked, decision.ErrorCode);
    }

    [TestMethod]
    public void AQueuedMutationCannotSurviveATakeover()
    {
        var authority = ReadyWith(PageA);
        var permit = authority.Authorize(Req(PageA, AgentTargetOperation.Mutate)).Permit!;

        authority.BeginUserControl("user grabbed the mouse");

        var decision = authority.Revalidate(permit);
        Assert.IsFalse(decision.IsAllowed);
        Assert.AreEqual(AgentTargetErrorCodes.Busy, decision.ErrorCode);
    }

    [TestMethod]
    public void AnObservationPermitSurvivesATakeover()
    {
        var authority = ReadyWith(PageA);
        var permit = authority.Authorize(Req(PageA, AgentTargetOperation.Observe)).Permit!;

        authority.BeginUserControl("user grabbed the mouse");

        Assert.IsTrue(authority.Revalidate(permit).IsAllowed);
    }

    [TestMethod]
    public void AStalePermitCannotSurviveAGenerationChange()
    {
        var authority = ReadyWith(PageA);
        var permit = authority.Authorize(Req(PageA, AgentTargetOperation.Mutate)).Permit!;

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
        var decision = authority.Authorize(Req(PageA, AgentTargetOperation.Observe));
        Assert.AreEqual(AgentTargetErrorCodes.Closed, decision.ErrorCode);
    }

    [TestMethod]
    public void EveryDenialMapsToAStableCode()
    {
        var authority = new AgentTargetSessionAuthority();

        var decision = authority.Authorize(Req(PageA, AgentTargetOperation.Mutate));

        Assert.IsNotNull(decision.ErrorCode);
        Assert.AreEqual(AgentTargetErrorCodes.TransportUnavailable, decision.ErrorCode);
        Assert.IsNull(decision.Permit);
    }

    [TestMethod]
    public void EveryDenialValueMapsToADistinctStableCode()
    {
        var codes = Enum.GetValues<AgentTargetDenial>()
            .Select(denial => AgentTargetDecision.Deny(denial, "test").ErrorCode)
            .ToArray();

        CollectionAssert.AllItemsAreNotNull(codes);
        CollectionAssert.AllItemsAreUnique(codes);
    }

    // ── F7 反例（先复现失败，修复后通过）：简化不能删掉执行不变量 ─────────

    /// <summary>
    /// 反例 1（审阅报告 F7-1）：接管只翻转布尔值、不推进写世代 ⇒ 交还控制权后，
    /// 接管前入队的写票据仍能通过 <c>Revalidate</c> 复活。
    /// </summary>
    [TestMethod]
    public void TakeoverRestoreDoesNotReviveAQueuedMutation()
    {
        var authority = ReadyWith(PageA);
        var permit = authority.Authorize(Req(PageA, AgentTargetOperation.Mutate)).Permit!;

        authority.BeginUserControl("user grabbed the mouse");
        authority.EndUserControl("user handed control back");

        var decision = authority.Revalidate(permit);
        Assert.IsFalse(decision.IsAllowed,
            "恢复控制权不得复活接管前入队的写票据（ADR-097 D4 / 方案 §3.2）。");
        Assert.AreEqual(AgentTargetErrorCodes.PermitInvalid, decision.ErrorCode);
    }

    [TestMethod]
    public void PauseRestoreDoesNotReviveAQueuedMutation()
    {
        var authority = ReadyWith(PageA);
        var permit = authority.Authorize(Req(PageA, AgentTargetOperation.Mutate)).Permit!;

        authority.BeginPause("paused");
        authority.EndPause("resumed");

        var decision = authority.Revalidate(permit);
        Assert.IsFalse(decision.IsAllowed);
        Assert.AreEqual(AgentTargetErrorCodes.PermitInvalid, decision.ErrorCode);
    }

    /// <summary>
    /// 反例 2（审阅报告 F7-2）：票据没有唯一标识、完成只比较会话/控制世代 ⇒
    /// A 的迟到完成会清掉后来 B 的占用。
    /// </summary>
    [TestMethod]
    public void ADuplicateCompletionCannotReleaseAnotherOperationsOwnership()
    {
        var authority = ReadyWith(PageA);

        var first = authority.Authorize(Req(PageA, AgentTargetOperation.Mutate));
        Assert.IsTrue(first.IsAllowed);
        authority.CompleteOperation(first.Permit!);

        var second = authority.Authorize(Req(PageA, AgentTargetOperation.Mutate));
        Assert.IsTrue(second.IsAllowed, "A 已释放，B 应能取得唯一的写占用。");

        // A 的迟到/重复完成：不得释放 B 的占用。
        authority.CompleteOperation(first.Permit!);

        var third = authority.Authorize(Req(PageA, AgentTargetOperation.Mutate));
        Assert.IsFalse(third.IsAllowed, "A 的重复完成释放了 B 的占用（F7-2）。");
        Assert.AreEqual(AgentTargetErrorCodes.Busy, third.ErrorCode);

        // B 自己的完成才释放占用。
        authority.CompleteOperation(second.Permit!);
        Assert.IsTrue(authority.Authorize(Req(PageA, AgentTargetOperation.Mutate)).IsAllowed);
    }

    /// <summary>反例 3：释放后的票据不得再通过复检（Revalidate 未核对票据仍在途）。</summary>
    [TestMethod]
    public void AReleasedPermitCannotBeRevalidated()
    {
        var authority = ReadyWith(PageA);
        var permit = authority.Authorize(Req(PageA, AgentTargetOperation.Mutate)).Permit!;

        authority.CompleteOperation(permit);

        var decision = authority.Revalidate(permit);
        Assert.IsFalse(decision.IsAllowed, "已释放的票据不得再用（方案 §3.2）。");
        Assert.AreEqual(AgentTargetErrorCodes.PermitInvalid, decision.ErrorCode);
    }

    [TestMethod]
    public void TakeoverCancelsTheInFlightWrite()
    {
        var authority = ReadyWith(PageA);
        var permit = authority.Authorize(Req(PageA, AgentTargetOperation.Mutate)).Permit!;

        Assert.IsFalse(permit.Cancellation.IsCancellationRequested);

        authority.BeginUserControl("user grabbed the mouse");

        Assert.IsTrue(permit.Cancellation.IsCancellationRequested,
            "进入阻断状态必须立即取消在途写（ADR-097 D4「取消信号」）。");
    }

    [TestMethod]
    public void CancelOperationReleasesTheSlotAndInvalidatesThePermit()
    {
        var authority = ReadyWith(PageA);
        var permit = authority.Authorize(Req(PageA, AgentTargetOperation.Mutate)).Permit!;

        authority.CancelOperation(permit, "task cancelled");

        Assert.IsTrue(permit.Cancellation.IsCancellationRequested);
        Assert.AreEqual(
            AgentTargetErrorCodes.PermitInvalid,
            authority.Revalidate(permit).ErrorCode);
        Assert.IsTrue(authority.Authorize(Req(PageA, AgentTargetOperation.Mutate)).IsAllowed,
            "取消后写槽必须释放，后续操作可用。");
    }

    [TestMethod]
    public void PermitsCarryUniqueIdsAndAdmissionEpochs()
    {
        var authority = ReadyWith(PageA);

        var first = authority.Authorize(Req(PageA, AgentTargetOperation.Mutate)).Permit!;
        authority.CompleteOperation(first);
        var second = authority.Authorize(Req(PageA, AgentTargetOperation.Mutate)).Permit!;

        Assert.AreNotEqual(first.PermitId.Value, second.PermitId.Value);
        Assert.AreEqual(authority.ControlEpoch.Value, second.ControlEpoch.Value);
        Assert.AreEqual(authority.WriteEpoch.Value, second.WriteEpoch.Value);

        // 观察票据不占用写槽，但也带唯一标识（便于回执关联）。
        var observe = authority.Authorize(Req(PageA, AgentTargetOperation.Observe)).Permit!;
        Assert.AreNotEqual(second.PermitId.Value, observe.PermitId.Value);
        Assert.IsFalse(observe.RequiresExclusiveOwnership);

        authority.CompleteOperation(second);
    }

    [TestMethod]
    public void WriteEpochAdvancesOnBlockingEntryButNotOnExitOrRepeat()
    {
        var authority = ReadyWith(PageA);
        var before = authority.WriteEpoch.Value;

        authority.BeginPause();
        var paused = authority.WriteEpoch.Value;
        Assert.AreNotEqual(before, paused, "进入阻断状态必须推进写世代。");

        authority.BeginPause();
        Assert.AreEqual(paused, authority.WriteEpoch.Value, "重复进入是幂等的。");

        authority.EndPause();
        Assert.AreEqual(paused, authority.WriteEpoch.Value, "退出阻断状态不回退也不推进写世代。");

        authority.BeginUserControl();
        Assert.AreNotEqual(paused, authority.WriteEpoch.Value, "另一轴进入同样推进写世代。");
    }
}
