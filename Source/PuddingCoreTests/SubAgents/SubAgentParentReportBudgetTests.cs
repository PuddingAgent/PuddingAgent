using PuddingCode.SubAgents;

namespace PuddingCoreTests.SubAgents;

/// <summary>
/// 子代理 → 父代理<b>主动</b>回信的额度账本。
/// 关注点：模型自己调 <c>send_message(to: agent:parent)</c> 时，不能让一个长跑子代理把父会话塞满；
/// 同时被拒的那次不得扣额度，否则日志会给出「没收到却说超额」的错误解释。
/// </summary>
[TestClass]
public sealed class SubAgentParentReportBudgetTests
{
    private static readonly DateTimeOffset T0 =
        new(2026, 11, 15, 13, 0, 0, TimeSpan.FromHours(8));

    [TestMethod]
    public void FirstReply_IsAllowed()
    {
        var budget = new SubAgentParentReportBudget();

        var plan = budget.TryAcquire(T0);

        Assert.AreEqual(SubAgentParentReportDecision.Allowed, plan.Decision);
        Assert.IsTrue(plan.Allowed);
        Assert.AreEqual(SubAgentParentReportBudget.DefaultMaxMessages, plan.Remaining);
    }

    [TestMethod]
    public void ReplyWithinMinInterval_IsTooSoon()
    {
        var budget = new SubAgentParentReportBudget(
            maxMessages: 5, minInterval: TimeSpan.FromSeconds(20));
        Assert.IsTrue(budget.Commit(T0));

        var plan = budget.TryAcquire(T0.AddSeconds(19));

        Assert.AreEqual(SubAgentParentReportDecision.TooSoon, plan.Decision);
        Assert.IsFalse(plan.Allowed);
    }

    [TestMethod]
    public void ReplyExactlyAtMinInterval_IsAllowed()
    {
        // 边界：恰好等于间隔应放行，否则「最小 20 秒」实际变成 21 秒。
        var budget = new SubAgentParentReportBudget(
            maxMessages: 5, minInterval: TimeSpan.FromSeconds(20));
        Assert.IsTrue(budget.Commit(T0));

        Assert.AreEqual(
            SubAgentParentReportDecision.Allowed,
            budget.TryAcquire(T0.AddSeconds(20)).Decision);
    }

    [TestMethod]
    public void RejectedCalls_DoNotConsumeQuota()
    {
        // 关键不变量：TryAcquire 是纯判定。被拒多次之后额度必须原样不动，
        // 否则「试一下」这个动作本身就会把额度磨光。
        var budget = new SubAgentParentReportBudget(
            maxMessages: 3, minInterval: TimeSpan.FromSeconds(20));
        Assert.IsTrue(budget.Commit(T0));

        for (var i = 0; i < 5; i++)
        {
            var plan = budget.TryAcquire(T0.AddSeconds(5));
            Assert.AreEqual(SubAgentParentReportDecision.TooSoon, plan.Decision);
        }

        Assert.AreEqual(1, budget.SentCount, "被拒的判定不得扣额度");
        Assert.AreEqual(2, budget.Remaining);
    }

    [TestMethod]
    public void QuotaExhausted_IsRejected()
    {
        var budget = new SubAgentParentReportBudget(
            maxMessages: 1, minInterval: TimeSpan.Zero);
        Assert.IsTrue(budget.Commit(T0));
        Assert.AreEqual(0, budget.Remaining);

        var plan = budget.TryAcquire(T0.AddHours(1));

        Assert.AreEqual(SubAgentParentReportDecision.ExceededQuota, plan.Decision);
        Assert.AreEqual(0, plan.Remaining);
        StringAssert.Contains(plan.Reason, "1/1");
    }

    [TestMethod]
    public void ZeroMaxMessages_IsClampedToOne()
    {
        // 配成 0 不应退化成「永远发不出」——那样子代理连一条结论都报不回来。
        var budget = new SubAgentParentReportBudget(maxMessages: 0, minInterval: TimeSpan.Zero);

        Assert.AreEqual(SubAgentParentReportDecision.Allowed, budget.TryAcquire(T0).Decision);
        Assert.IsTrue(budget.Commit(T0));
        Assert.AreEqual(0, budget.Remaining);
        Assert.AreEqual(
            SubAgentParentReportDecision.ExceededQuota,
            budget.TryAcquire(T0.AddSeconds(1)).Decision);
    }

    [TestMethod]
    public void ZeroInterval_AllowsBackToBackButQuotaStillApplies()
    {
        var budget = new SubAgentParentReportBudget(
            maxMessages: 2, minInterval: TimeSpan.Zero);

        Assert.IsTrue(budget.Commit(T0));
        Assert.AreEqual(
            SubAgentParentReportDecision.Allowed,
            budget.TryAcquire(T0).Decision, "不限间隔时应允许连发");
        Assert.IsTrue(budget.Commit(T0));
        Assert.AreEqual(
            SubAgentParentReportDecision.ExceededQuota,
            budget.TryAcquire(T0).Decision, "不限间隔不等于不限条数");
    }

    [TestMethod]
    public void ClockGoingBackwards_WithZeroInterval_DoesNotBlock()
    {
        // 时钟回拨说明不了「太频繁」。不限间隔时不该因一次校时把后续回信永久拒掉。
        var budget = new SubAgentParentReportBudget(
            maxMessages: 5, minInterval: TimeSpan.Zero);
        Assert.IsTrue(budget.Commit(T0));

        var plan = budget.TryAcquire(T0.AddSeconds(-30));

        Assert.AreEqual(SubAgentParentReportDecision.Allowed, plan.Decision);
    }

    [TestMethod]
    public void CommitBeyondQuota_ReturnsFalseAndKeepsState()
    {
        var budget = new SubAgentParentReportBudget(
            maxMessages: 1, minInterval: TimeSpan.Zero);
        Assert.IsTrue(budget.Commit(T0));

        Assert.IsFalse(budget.Commit(T0.AddSeconds(1)), "超额的提交必须失败，调用方据此当作未送达");
        Assert.AreEqual(1, budget.SentCount);
    }

    [TestMethod]
    public void NegativeMinInterval_IsClampedToZero()
    {
        var budget = new SubAgentParentReportBudget(
            maxMessages: 3, minInterval: TimeSpan.FromSeconds(-5));
        Assert.IsTrue(budget.Commit(T0));

        Assert.AreEqual(
            SubAgentParentReportDecision.Allowed,
            budget.TryAcquire(T0).Decision, "负间隔按 0 处理，仍受额度约束");
    }

    [TestMethod]
    public void RecordThenAcquire_ProducesAtMostQuotaAllowedReplies()
    {
        // 端到端串一遍：允许 → 送达 → 再判定，最终送达条数不得超过额度。
        var budget = new SubAgentParentReportBudget(
            maxMessages: 3, minInterval: TimeSpan.Zero);
        var delivered = 0;

        for (var i = 0; i < 10; i++)
        {
            var now = T0.AddMinutes(i);
            if (!budget.TryAcquire(now).Allowed)
                continue;
            if (budget.Commit(now))
                delivered++;
        }

        Assert.AreEqual(3, delivered, "额度是硬上限");
        Assert.AreEqual(3, budget.SentCount);
        Assert.AreEqual(0, budget.Remaining);
    }
}
