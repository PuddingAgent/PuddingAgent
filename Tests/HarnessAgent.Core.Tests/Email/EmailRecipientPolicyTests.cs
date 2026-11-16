using HarnessAgent.Core.Connectors.Email;

namespace HarnessAgent.Core.Tests.Email;

/// <summary>
/// 收件人白名单的用例。
///
/// <para>背景：邮箱渠道的收件人是一个字符串参数，可被提示词注入直接改写；
/// 而飞书的收件人是会话绑定的。所以邮箱侧必须默认拒绝，
/// 只有显式登记的地址才放行。</para>
/// </summary>
[TestClass]
public sealed class EmailRecipientPolicyTests
{
    private static EmailRecipientPolicy Policy(params string[] allowed) => new(allowed);

    [TestMethod]
    public void ListedRecipientIsAllowed()
    {
        var decision = Policy("1519481987@qq.com").Evaluate("1519481987@qq.com");

        Assert.IsTrue(decision.Allowed);
        Assert.AreEqual("1519481987@qq.com", decision.NormalizedAddress);
    }

    [TestMethod]
    public void UnlistedRecipientIsDenied()
    {
        var decision = Policy("1519481987@qq.com").Evaluate("attacker@example.com");

        Assert.IsFalse(decision.Allowed);
        StringAssert.Contains(decision.Reason, "allow list");
    }

    [TestMethod]
    public void MatchingIsCaseInsensitiveAndTrimsWhitespace()
    {
        var decision = Policy("1519481987@qq.com").Evaluate("  1519481987@QQ.com  ");

        Assert.IsTrue(decision.Allowed);
        Assert.AreEqual("1519481987@qq.com", decision.NormalizedAddress);
    }

    [DataTestMethod]
    [DataRow("victim@qq.com\r\nBcc: attacker@example.com")]
    [DataRow("victim@qq.com\nBcc: attacker@example.com")]
    public void LineBreaksAreDeniedBecauseTheyInjectHeaders(string address)
    {
        var decision = Policy("victim@qq.com").Evaluate(address);

        Assert.IsFalse(decision.Allowed, "换行必须拒绝，否则可以注入新的邮件头");
        // 只断言“被拒”不够：其他规则（单 @、裸地址）也会拒它，那样就分不清是哪条在拦。
        // 断言原因才能证明换行分支本身在承重。
        StringAssert.Contains(decision.Reason, "line break");
    }

    [TestMethod]
    public void DisplayNameFormIsDenied()
    {
        var decision = Policy("victim@qq.com").Evaluate("Victim <victim@qq.com>");

        Assert.IsFalse(decision.Allowed);
    }

    [TestMethod]
    public void MultipleRecipientsAreDenied()
    {
        var decision = Policy("a@qq.com", "b@qq.com").Evaluate("a@qq.com,b@qq.com");

        Assert.IsFalse(decision.Allowed);
    }

    [DataTestMethod]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("no-at-sign")]
    [DataRow("@qq.com")]
    [DataRow("user@")]
    [DataRow("a@b@c")]
    public void MalformedAddressesAreDenied(string address)
    {
        var decision = Policy("1519481987@qq.com").Evaluate(address);

        Assert.IsFalse(decision.Allowed);
    }

    [TestMethod]
    public void ResolveWithoutOverrideUsesTheDefaultRecipient()
    {
        var policy = EmailRecipientPolicy.Resolve(null);

        Assert.IsTrue(policy.Evaluate("1519481987@qq.com").Allowed);
        Assert.IsFalse(policy.Evaluate("attacker@example.com").Allowed);
    }

    [TestMethod]
    public void ResolveWithOverrideReplacesInsteadOfAppending()
    {
        var policy = EmailRecipientPolicy.Resolve("only@example.com");

        Assert.IsTrue(policy.Evaluate("only@example.com").Allowed);
        Assert.IsFalse(
            policy.Evaluate("1519481987@qq.com").Allowed,
            "覆盖必须是替换：默认地址不应继续放行");
    }

    [TestMethod]
    public void ResolveWithOnlySeparatorsFallsBackToDefault()
    {
        var policy = EmailRecipientPolicy.Resolve(" ; , ");

        Assert.IsTrue(policy.Evaluate("1519481987@qq.com").Allowed);
    }

    [TestMethod]
    public void DuplicateRecipientsAreCollapsed()
    {
        var policy = Policy("A@qq.com", "a@QQ.com", "  a@qq.com ");

        Assert.AreEqual(1, policy.AllowedRecipients.Count);
        Assert.AreEqual("a@qq.com", policy.AllowedRecipients[0]);
    }
}
