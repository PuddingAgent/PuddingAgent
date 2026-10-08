using Pudding.DesktopService;

namespace DesktopServiceTests;

/// <summary>
/// 从 TRX 读真实测试计数（交付清单 checks 的事实来源）。
/// 这里钉住的是**保守**：读不出来就失败，不猜 0、不返回"通过了"。
/// </summary>
public sealed class TrxTestResultParserTests
{
    private const string SampleTrx = """
        <?xml version="1.0" encoding="utf-8"?>
        <TestRun id="0" name="run" xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
          <Results>
            <UnitTestResult testId="1" outcome="Passed" testName="a" />
            <UnitTestResult testId="2" outcome="Passed" testName="b" />
            <UnitTestResult testId="3" outcome="Failed" testName="c" />
          </Results>
          <ResultSummary outcome="Failed">
            <Counters total="3" executed="3" passed="2" failed="1" error="0" timeout="0" aborted="0" inconclusive="0" />
          </ResultSummary>
        </TestRun>
        """;

    [Fact]
    public void ReadsPassedAndFailedCountersFromATrx()
    {
        Assert.True(
            TrxTestResultParser.TryParse("AutomationTests", SampleTrx, out var check, out var error),
            error);

        Assert.Equal("AutomationTests", check.Name);
        Assert.Equal(2, check.Passed);
        Assert.Equal(1, check.Failed);
    }

    [Fact]
    public void MissingOrMalformedResultsAreRefusedInsteadOfGuessed()
    {
        Assert.False(TrxTestResultParser.TryParse("x", null, out _, out var missing));
        Assert.Contains("missing", missing);

        Assert.False(TrxTestResultParser.TryParse("x", "not xml <<<", out _, out var invalid));
        Assert.Contains("valid XML", invalid);

        // 没有 Counters ⇒ 说明"其实什么都没跑"，不能当成功。
        Assert.False(TrxTestResultParser.TryParse(
            "x",
            """<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results /></TestRun>""",
            out _,
            out var noCounters));
        Assert.Contains("Counters", noCounters);

        // 计数字段缺失或不是数字 ⇒ 拒绝。
        Assert.False(TrxTestResultParser.TryParse(
            "x",
            """<TestRun><ResultSummary><Counters passed="1" /></ResultSummary></TestRun>""",
            out _,
            out var partial));
        Assert.Contains("passed/failed", partial);

        Assert.False(TrxTestResultParser.TryParse("", SampleTrx, out _, out var noName));
        Assert.Contains("suite name", noName);
    }

    [Fact]
    public void ZeroCountsAreReportedAsTheyAre()
    {
        // 0/0 是"跑了但一个用例都没有"——如实报告，让清单校验去拒绝（解析器不替它下结论）。
        Assert.True(TrxTestResultParser.TryParse(
            "empty",
            """<TestRun><ResultSummary><Counters passed="0" failed="0" /></ResultSummary></TestRun>""",
            out var check,
            out _));

        Assert.Equal(0, check.Passed);
        Assert.Equal(0, check.Failed);
    }

    [Fact]
    public void AllOrNothingAcrossSuites()
    {
        var ok = new (string, string?)[]
        {
            ("A", """<TestRun><Counters passed="10" failed="0" /></TestRun>"""),
            ("B", """<TestRun><Counters passed="5" failed="0" /></TestRun>"""),
        };

        Assert.True(TrxTestResultParser.TryParseAll(ok, out var checks, out var errors), string.Join("; ", errors));
        Assert.Equal(2, checks.Count);
        Assert.Equal(15, checks.Sum(check => check.Passed));

        // 其中一个读不出来 ⇒ 整体失败（不拿"读不全的检查"去交付）。
        var broken = new (string, string?)[]
        {
            ("A", """<TestRun><Counters passed="10" failed="0" /></TestRun>"""),
            ("B", "garbage"),
        };

        Assert.False(TrxTestResultParser.TryParseAll(broken, out var none, out var problems));
        Assert.Empty(none);
        Assert.Single(problems);

        // 一个都没给 ⇒ 同样拒绝（空检查列表不是交付）。
        Assert.False(TrxTestResultParser.TryParseAll([], out _, out var empty));
        Assert.Contains("no TRX results", empty[0]);

        Assert.Throws<ArgumentNullException>(() => TrxTestResultParser.TryParseAll(null!, out _, out _));
    }

    [Fact]
    public void ParsedChecksFeedTheDeliveryBuilder()
    {
        // 端到端小步：TRX → checks → 清单（并确认有失败项时清单生成器会拒绝）。
        Assert.True(TrxTestResultParser.TryParseAll(
            [
                ("AutomationTests", """<TestRun><Counters passed="276" failed="0" /></TestRun>"""),
                ("HostTests", """<TestRun><Counters passed="258" failed="0" /></TestRun>"""),
            ],
            out var checks,
            out _));

        Assert.True(DeliveryManifestBuilder.TryBuild(
            new DeliveryBuildFacts(
                "build-2026-10-08",
                "fda9bc9",
                ["PuddingDesktop.exe"],
                checks,
                ["- 真机未验证：CDP 行为需外部 smoke 复核。"]),
            out var manifest,
            out var problems),
            string.Join("; ", problems));

        Assert.Equal(534, manifest!.Checks.Sum(check => check.Passed));

        Assert.False(DeliveryManifestBuilder.TryBuild(
            new DeliveryBuildFacts(
                "build-2026-10-08",
                "fda9bc9",
                ["PuddingDesktop.exe"],
                [new DeliveryCheck("HostTests", 257, 1)],
                []),
            out _,
            out var refused));
        Assert.Contains(refused, problem => problem.Contains("failing checks", StringComparison.Ordinal));
    }
}
