using PuddingContextPolicy;

namespace PuddingContextPolicyTests;

[TestClass]
public sealed class PrefixCompactionPolicyTests
{
    [DataTestMethod]
    [DataRow(59, false, false, false)]
    [DataRow(60, true, false, false)]
    [DataRow(75, true, true, false)]
    [DataRow(90, true, true, true)]
    [DataRow(101, true, true, true)]
    public void TokenAndBytePressureIndependentlyDriveStages(int used, bool prepare, bool commit, bool wait)
    {
        foreach (var d in new[] { PrefixCompactionPolicy.Decide(used, 100, 1, 100), PrefixCompactionPolicy.Decide(1, 100, used, 100) })
        {
            Assert.AreEqual(prepare, d.Prepare);
            Assert.AreEqual(commit, d.CommitReady);
            Assert.AreEqual(wait, d.WaitForCandidate);
        }
    }

    [TestMethod]
    public void CacheEpochCooldownNeverSuppressesCapacityProtection()
    {
        Assert.IsFalse(PrefixCompactionPolicy.Decide(80, 100, 1, 100, roundsSinceMaintenance: 1).Prepare);
        Assert.IsTrue(PrefixCompactionPolicy.Decide(90, 100, 1, 100, roundsSinceMaintenance: 1).WaitForCandidate);
        Assert.IsTrue(PrefixCompactionPolicy.Decide(70, 100, 1, 100, growthTokens: 30, roundsSinceMaintenance: 1).WaitForCandidate);
    }

    private static readonly FrozenCandidateDescriptor Frozen = new("fp", 2, "old", "route", "tools", "policy", ["a"], ["b"], 1);
    private static CurrentContextDescriptor Current(params string[] ids) => new(2, "new", "route", "tools", "policy", ids);

    [TestMethod]
    public void AppendedUserAndToolMessagesArePreservedButAnyFrozenMutationIsRejected()
    {
        string[] before = ["system", "a", "b"];
        Assert.IsTrue(PrefixCompactionPolicy.ValidateAppendOnly(Frozen, before, Current("system", "a", "b", "new-user", "tool")).IsApplicable);
        foreach (var after in new[] { new[] { "changed-system", "a", "b" }, new[] { "system", "a" }, new[] { "system", "x", "a", "b" }, new[] { "system", "b", "a" } })
            Assert.IsFalse(PrefixCompactionPolicy.ValidateAppendOnly(Frozen, before, Current(after)).IsApplicable);
        Assert.IsFalse(PrefixCompactionPolicy.ValidateAppendOnly(Frozen, before, Current(before) with { Generation = 3 }).IsApplicable);
        Assert.IsFalse(PrefixCompactionPolicy.ValidateAppendOnly(Frozen, before, Current(before) with { ToolSpecVersion = "changed" }).IsApplicable);
        Assert.IsFalse(PrefixCompactionPolicy.ValidateAppendOnly(Frozen, before, Current(before) with { RouteVersion = "changed" }).IsApplicable);
        Assert.IsFalse(PrefixCompactionPolicy.ValidateAppendOnly(Frozen, before, Current(before) with { PolicyVersion = "changed" }).IsApplicable);
    }
}
