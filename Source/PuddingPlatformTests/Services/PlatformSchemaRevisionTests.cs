using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

/// <summary>
/// 稳态 schema 阶梯跳过的判定。这些用例固定住"绝不因为读不到标记而假设结构是新的"这条 fail-open 规则。
/// </summary>
[TestClass]
public sealed class PlatformSchemaRevisionTests
{
    [TestMethod]
    public void FreshDatabaseRequiresTheLadder()
        => Assert.IsTrue(PlatformSchemaRevision.LadderRequired(0, 0));

    [TestMethod]
    public void MatchingRevisionWithEverySentinelTableSkipsTheLadder()
        => Assert.IsFalse(PlatformSchemaRevision.LadderRequired(
            PlatformSchemaRevision.Current, PlatformSchemaRevision.SentinelTables.Length));

    [TestMethod]
    public void MatchingRevisionButAMissingSentinelTableRunsTheLadderAgain()
        => Assert.IsTrue(PlatformSchemaRevision.LadderRequired(
            PlatformSchemaRevision.Current, PlatformSchemaRevision.SentinelTables.Length - 1));

    [DataTestMethod]
    [DataRow(PlatformSchemaRevision.Current - 1)]
    [DataRow(PlatformSchemaRevision.Current + 1)]
    [DataRow(-1L)]
    public void AnyOtherRevisionRunsTheLadder(long revision)
        => Assert.IsTrue(PlatformSchemaRevision.LadderRequired(revision, PlatformSchemaRevision.SentinelTables.Length));

    [TestMethod]
    public void FailedProbeRunsTheLadder()
        => Assert.IsTrue(PlatformSchemaRevision.LadderRequired(PlatformSchemaRevision.Current, -1));

    [TestMethod]
    public void SentinelTablesAreDistinctAndNamed()
    {
        Assert.AreEqual(4, PlatformSchemaRevision.SentinelTables.Length);
        Assert.AreEqual(
            PlatformSchemaRevision.SentinelTables.Length,
            PlatformSchemaRevision.SentinelTables.Distinct(StringComparer.Ordinal).Count());
        foreach (var name in PlatformSchemaRevision.SentinelTables)
            Assert.IsFalse(string.IsNullOrWhiteSpace(name), "哨兵表名不能为空");
    }

    [DataTestMethod]
    [DataRow("full", true)]
    [DataRow("FULL", true)]
    [DataRow("", false)]
    [DataRow("true", false)]
    [DataRow(null, false)]
    public void OnlyTheExplicitFullSettingForcesTheLadder(string? setting, bool expected)
        => Assert.AreEqual(expected, PlatformSchemaRevision.ForceFullLadder(setting));
}
