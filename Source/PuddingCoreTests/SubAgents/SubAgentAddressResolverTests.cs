using PuddingCode.SubAgents;

namespace PuddingCoreTests.SubAgents;

[TestClass]
public sealed class SubAgentAddressResolverTests
{
    [TestMethod]
    public void Resolve_NonAliasAddress_IsReturnedUnchanged()
    {
        var r = SubAgentAddressResolver.Resolve("agent:assistant", "parent-1");

        Assert.IsFalse(r.IsParentAlias);
        Assert.AreEqual("agent:assistant", r.Address);
        Assert.IsNull(r.Error);
        Assert.IsTrue(r.CanSend);
    }

    [TestMethod]
    public void Resolve_ParentAlias_IsResolvedToParentAgent()
    {
        var r = SubAgentAddressResolver.Resolve("agent:parent", "default.global.6a8");

        Assert.IsTrue(r.IsParentAlias);
        Assert.AreEqual("agent:default.global.6a8", r.Address);
        Assert.IsNull(r.Error);
        Assert.IsTrue(r.CanSend);
    }

    [TestMethod]
    public void Resolve_ParentAlias_IsCaseInsensitiveAndTrimmed()
    {
        var r = SubAgentAddressResolver.Resolve("  AGENT:Parent  ", "p-9");

        Assert.IsTrue(r.IsParentAlias);
        Assert.AreEqual("agent:p-9", r.Address);
    }

    [TestMethod]
    public void Resolve_ParentAliasWithoutParentAddress_FailsInsteadOfGuessing()
    {
        var r = SubAgentAddressResolver.Resolve("agent:parent", "   ");

        Assert.IsTrue(r.IsParentAlias);
        Assert.IsNull(r.Address);
        Assert.IsFalse(r.CanSend);
        Assert.IsFalse(string.IsNullOrWhiteSpace(r.Error));
    }

    [TestMethod]
    public void Resolve_NearMissValues_AreNotTreatedAsParentAlias()
    {
        // 只有完整别名才是别名：裸 parent、带后缀、带别的前缀都必须原样透传，
        // 否则一个恰好叫 parent 的 agent 会被错误改写。
        foreach (var candidate in new[] { "parent", "agent:parentx", "user:parent", "agent:parent/x" })
        {
            var r = SubAgentAddressResolver.Resolve(candidate, "p-1");

            Assert.IsFalse(r.IsParentAlias, $"'{candidate}' must not be treated as the parent alias");
            Assert.AreEqual(candidate, r.Address);
            Assert.IsNull(r.Error);
        }
    }

    [TestMethod]
    public void Resolve_EmptyInput_IsNotAliasAndNotSendable()
    {
        foreach (var candidate in new[] { null, "", "   " })
        {
            var r = SubAgentAddressResolver.Resolve(candidate, "p-1");

            Assert.IsFalse(r.IsParentAlias);
            Assert.IsNull(r.Address);
            Assert.IsFalse(r.CanSend);
            Assert.IsNull(r.Error);
        }
    }
}
