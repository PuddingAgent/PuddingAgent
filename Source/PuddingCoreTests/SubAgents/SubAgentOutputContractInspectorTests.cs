using PuddingCode.SubAgents;

namespace PuddingCoreTests.SubAgents;

[TestClass]
public sealed class SubAgentOutputContractInspectorTests
{
    [TestMethod]
    public void Inspect_EmptyContract_IsNotApplicable()
    {
        var r = SubAgentOutputContractInspector.Inspect("anything", "   ");

        Assert.IsFalse(r.Applicable);
        Assert.IsTrue(r.Satisfied);
        Assert.AreEqual(0, r.MissingKeys.Count);
    }

    [TestMethod]
    public void Inspect_AllLabelsPresent_IsSatisfied()
    {
        const string reply = """
            SUMMARY: done
            CHANGES: none
            EVIDENCE: none
            RISKS: none
            BLOCKERS: none
            """;

        var r = SubAgentOutputContractInspector.Inspect(reply, SubAgentOutputContractInspector.DefaultContract);

        Assert.IsTrue(r.Applicable);
        Assert.IsTrue(r.Satisfied, "missing=" + string.Join(",", r.MissingKeys));
    }

    [TestMethod]
    public void Inspect_MarkdownAndNumberedPrefixes_AreStillLabels()
    {
        const string reply = """
            - **SUMMARY**: done
            2. RISKS: none
            > EVIDENCE: none
            """;

        var r = SubAgentOutputContractInspector.Inspect(reply, "SUMMARY, EVIDENCE, RISKS");

        Assert.IsTrue(r.Satisfied, "missing=" + string.Join(",", r.MissingKeys));
    }

    [TestMethod]
    public void Inspect_JsonWrappedReply_IsSatisfied()
    {
        const string reply = "{\n  \"status\": \"DONE\",\n  \"message\": \"SUMMARY: ok\\n\\nBLOCKERS: none\"\n}";

        var r = SubAgentOutputContractInspector.Inspect(reply, "SUMMARY, BLOCKERS");

        Assert.IsTrue(r.Satisfied, "missing=" + string.Join(",", r.MissingKeys));
    }

    [TestMethod]
    public void Inspect_OneWordReply_AgainstDefaultContract_IsNotSatisfied()
    {
        // 实测过的真实情形：任务书要求“只回一行 DONE”，回包就是 DONE。
        var r = SubAgentOutputContractInspector.Inspect("DONE", SubAgentOutputContractInspector.DefaultContract);

        Assert.IsTrue(r.Applicable);
        Assert.IsFalse(r.Satisfied);
        Assert.AreEqual(5, r.MissingKeys.Count);
    }

    [TestMethod]
    public void Inspect_MissingOneKey_ReportsExactlyThatKey()
    {
        const string reply = "SUMMARY: done\nCHANGES: none\nEVIDENCE: none\nBLOCKERS: none";

        var r = SubAgentOutputContractInspector.Inspect(reply, SubAgentOutputContractInspector.DefaultContract);

        Assert.IsFalse(r.Satisfied);
        CollectionAssert.AreEqual(new[] { "RISKS" }, r.MissingKeys.ToArray());
    }

    [TestMethod]
    public void Inspect_KeyMentionedMidSentence_IsNotCounted()
    {
        // 承认缺失的回包不得被判成齐全。
        const string reply = "SUMMARY: done\nI did not write the RISKS section because there were none.";

        var r = SubAgentOutputContractInspector.Inspect(reply, "SUMMARY, RISKS");

        Assert.IsFalse(r.Satisfied);
        CollectionAssert.AreEqual(new[] { "RISKS" }, r.MissingKeys.ToArray());
    }

    [TestMethod]
    public void Inspect_NullReply_ReportsEveryKeyMissing()
    {
        var r = SubAgentOutputContractInspector.Inspect(null, "SUMMARY, CHANGES");

        Assert.IsFalse(r.Satisfied);
        CollectionAssert.AreEqual(new[] { "SUMMARY", "CHANGES" }, r.MissingKeys.ToArray());
    }

    [TestMethod]
    public void Inspect_LowerCaseLabels_AreMatched()
    {
        var r = SubAgentOutputContractInspector.Inspect("summary: ok", "SUMMARY");

        Assert.IsTrue(r.Satisfied);
    }

    [TestMethod]
    public void ParseKeys_DropsEmptyAndDuplicates_PreservesOrder()
    {
        var keys = SubAgentOutputContractInspector.ParseKeys(" SUMMARY ,, changes ,SUMMARY , EVIDENCE ");

        CollectionAssert.AreEqual(new[] { "SUMMARY", "changes", "EVIDENCE" }, keys.ToArray());
    }
}
