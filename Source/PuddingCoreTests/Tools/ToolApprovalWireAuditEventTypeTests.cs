using PuddingCode.Tools;

namespace PuddingCoreTests.Tools;

/// <summary>
/// 审计事件类型 wire 名称的**完备性守卫**。
/// <para>
/// 事故背景（2026-09-21）：管理 API 曾自带一份只有 13 条分支的私有映射，而
/// <see cref="ToolApprovalAuditEventType"/> 有 26 个成员 ⇒ 其余 13 个事件落到
/// <c>ToString().ToLowerInvariant()</c> 兜底，产生 <c>classifierinvoked</c> /
/// <c>fullaccessgatebypass</c> 这类**丢下划线**的名字，与 <c>ticket_submitted</c> 形成两套命名；
/// 且管理 API 的 <c>eventType</c> 过滤正是按该名字精确比对 ⇒ 那些事件**筛选不出来**。
/// </para>
/// <para>
/// 本测试的作用：① 钉住**每个**枚举成员的 wire 名（新增成员却漏补映射 ⇒ 本测试直接失败）；
/// ② 钉住命名风格统一（全小写 snake_case，不出现大写字母）。
/// </para>
/// </summary>
[TestClass]
public sealed class ToolApprovalWireAuditEventTypeTests
{
    /// <summary>每个枚举成员的期望 wire 名（与 <see cref="ToolApprovalWire.ToWire(ToolApprovalAuditEventType)"/> 一一对应）。</summary>
    private static readonly Dictionary<ToolApprovalAuditEventType, string> Expected = new()
    {
        [ToolApprovalAuditEventType.TicketSubmitted] = "ticket_submitted",
        [ToolApprovalAuditEventType.TicketApproved] = "ticket_approved",
        [ToolApprovalAuditEventType.TicketDenied] = "ticket_denied",
        [ToolApprovalAuditEventType.TicketNeedHuman] = "ticket_need_human",
        [ToolApprovalAuditEventType.TicketMatched] = "ticket_matched",
        [ToolApprovalAuditEventType.TicketConsumed] = "ticket_consumed",
        [ToolApprovalAuditEventType.TicketMismatch] = "ticket_mismatch",
        [ToolApprovalAuditEventType.ImplicitApproved] = "implicit_approved",
        [ToolApprovalAuditEventType.ImplicitDenied] = "implicit_denied",
        [ToolApprovalAuditEventType.AllowlistHit] = "allowlist_hit",
        [ToolApprovalAuditEventType.AllowlistRuleCreated] = "allowlist_rule_created",
        [ToolApprovalAuditEventType.AllowlistRuleUpdated] = "allowlist_rule_updated",
        [ToolApprovalAuditEventType.AllowlistRuleDisabled] = "allowlist_rule_disabled",
        [ToolApprovalAuditEventType.DefinitionDriftDetected] = "definition_drift_detected",
        [ToolApprovalAuditEventType.TicketDeferredDependency] = "ticket_deferred_dependency",
        [ToolApprovalAuditEventType.ClassifierInvoked] = "classifier_invoked",
        [ToolApprovalAuditEventType.ClassifierUnavailable] = "classifier_unavailable",
        [ToolApprovalAuditEventType.DenylistRuleCreated] = "denylist_rule_created",
        [ToolApprovalAuditEventType.DenylistRuleDisabled] = "denylist_rule_disabled",
        [ToolApprovalAuditEventType.FullAccessRequested] = "full_access_requested",
        [ToolApprovalAuditEventType.FullAccessGranted] = "full_access_granted",
        [ToolApprovalAuditEventType.FullAccessDenied] = "full_access_denied",
        [ToolApprovalAuditEventType.FullAccessExpired] = "full_access_expired",
        [ToolApprovalAuditEventType.FullAccessRevoked] = "full_access_revoked",
        [ToolApprovalAuditEventType.RuleConflictDetected] = "rule_conflict_detected",
        [ToolApprovalAuditEventType.FullAccessGateBypass] = "full_access_gate_bypass",
    };

    [TestMethod]
    public void EveryAuditEventType_HasAnExplicitWireName()
    {
        var members = Enum.GetValues<ToolApprovalAuditEventType>();

        var missing = members.Where(member => !Expected.ContainsKey(member)).ToArray();
        Assert.IsEmpty(
            missing,
            $"新增枚举成员必须在 {nameof(ToolApprovalWire)} 与本测试的期望表中补上显式 wire 名（N01：成员只允许追加在末尾）。缺失：{string.Join(", ", missing.Select(m => m.ToString()))}");

        var unlisted = Expected.Keys.Where(key => !members.Contains(key)).ToArray();
        Assert.IsEmpty(
            unlisted,
            $"期望表里有已不存在的枚举成员（枚举成员不得删除）：{string.Join(", ", unlisted.Select(m => m.ToString()))}");
    }

    [TestMethod]
    public void EveryWireName_IsStableLowerSnakeCase()
    {
        foreach (var (member, expected) in Expected)
        {
            var actual = ToolApprovalWire.ToWire(member);
            Assert.AreEqual(expected, actual, $"事件 {member} 的 wire 名必须稳定（改了就是破坏性契约变更）。");

            // 命名风格统一：不得出现大写；不得靠"丢下划线"的兜底名（兜底名与成员名一致即视为漏补映射）。
            Assert.AreEqual(
                expected.ToLowerInvariant(),
                expected,
                $"事件 {member} 的 wire 名必须全小写（snake_case）。");
            Assert.AreEqual(
                actual,
                actual.ToLowerInvariant(),
                $"事件 {member} 的 wire 名不得含大写字母。");
            Assert.AreNotEqual(
                member.ToString().ToLowerInvariant(),
                actual,
                $"事件 {member} 落到了兜底名（缺显式映射）；兜底只用于「不静默说错」，不得作为正式命名。");
        }
    }
}
