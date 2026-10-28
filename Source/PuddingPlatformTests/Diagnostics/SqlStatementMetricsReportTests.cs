using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingPlatform.Data.Diagnostics;

namespace PuddingPlatformTests.Diagnostics;

/// <summary>
/// 报告渲染的契约。报告是"人肉诊断的唯一入口"，它自己出错会直接误导结论。
/// </summary>
[TestClass]
public sealed class SqlStatementMetricsReportTests
{
    private static SqlStatementMetrics MetricsWith(params (string Sql, long Ticks, int? Rows, int Times)[] items)
    {
        var metrics = new SqlStatementMetrics();
        foreach (var item in items)
        {
            for (var i = 0; i < item.Times; i++)
                metrics.Record(item.Sql, item.Ticks, item.Rows);
        }
        return metrics;
    }

    [TestMethod]
    public void Header_CarriesWindowAndCounts()
    {
        var metrics = MetricsWith(("SELECT 1 FROM t", 10, 1, 2));
        var text = SqlStatementMetricsReport.Format(metrics, top: 5, windowLabel: "60s");

        StringAssert.Contains(text, "[SqlMetrics]");
        StringAssert.Contains(text, "window=60s");
        StringAssert.Contains(text, "tracked=1");
        StringAssert.Contains(text, "overflowed=False");
    }

    [TestMethod]
    public void Rows_UnknownIsSpelledOut_NotRenderedAsZero()
    {
        var metrics = new SqlStatementMetrics();
        metrics.Record("SELECT * FROM t", 100, null);   // 查询类：行数未知

        var text = SqlStatementMetricsReport.Format(metrics, top: 5, windowLabel: "1s");
        StringAssert.Contains(text, "rows=unknown",
            "行数未知必须写成 unknown；写成 0 会让读报告的人以为这条查询没返回数据");
        Assert.IsFalse(text.Contains("rows=0"), "未知不得渲染成 0");
    }

    [TestMethod]
    public void TicksAreRenderedAsMilliseconds()
    {
        // 1 tick = 100ns，所以 10_000 ticks = 1ms，100_000 ticks = 10ms
        var metrics = new SqlStatementMetrics();
        metrics.Record("SELECT * FROM t", 100_000, 0);

        var text = SqlStatementMetricsReport.Format(metrics, top: 5, windowLabel: "1s");
        StringAssert.Contains(text, "max=10ms", "tick 必须换算成毫秒，否则读不出量级");
    }

    [TestMethod]
    public void TopLimit_IsRespected_AndOrderIsByCallCount()
    {
        var metrics = MetricsWith(
            ("SELECT * FROM cold", 1, 0, 1),
            ("SELECT * FROM warm", 1, 0, 3),
            ("SELECT * FROM hot", 1, 0, 5));

        var text = SqlStatementMetricsReport.Format(metrics, top: 2, windowLabel: "1s");
        StringAssert.Contains(text, "FROM hot");
        StringAssert.Contains(text, "FROM warm");
        Assert.IsFalse(text.Contains("FROM cold"), "top=2 时最冷的必须被省略");

        Assert.IsTrue(
            text.IndexOf("FROM hot", StringComparison.Ordinal) < text.IndexOf("FROM warm", StringComparison.Ordinal),
            "报告必须有确定顺序：调用次数多的在前");
    }

    [TestMethod]
    public void LongSql_IsTruncatedWithOriginalLength()
    {
        var longSql = "SELECT " + new string('x', 400) + " FROM t";
        var metrics = new SqlStatementMetrics();
        metrics.Record(longSql, 1, 0);

        var text = SqlStatementMetricsReport.Format(metrics, top: 1, windowLabel: "1s");
        Assert.IsTrue(text.Contains("…(len="), "超长 SQL 必须标注被截断与原始长度，否则无法判断是不是同一条");
        Assert.IsFalse(text.Contains(new string('x', SqlStatementMetricsReport.MaxSqlDisplayChars + 1)),
            "截断必须真的生效，否则单行日志会爆");
    }

    [TestMethod]
    public void EmptyMetrics_SaysSoInsteadOfEmittingNothing()
    {
        var metrics = new SqlStatementMetrics();
        var text = SqlStatementMetricsReport.Format(metrics, top: 5, windowLabel: "1s");

        StringAssert.Contains(text, "<no statements recorded>",
            "空报告必须明确写出来；空白输出会被误读为产出失败");
    }

    [TestMethod]
    public void EachStatementIsOneLine_SoLogSplittingKeepsItWhole()
    {
        var metrics = MetricsWith(("SELECT * FROM a", 1, 0, 1), ("SELECT * FROM b", 1, 0, 1));
        var text = SqlStatementMetricsReport.Format(metrics, top: 5, windowLabel: "1s");

        var lines = text.Split(Environment.NewLine);
        Assert.AreEqual(3, lines.Length, "表头 + 两条语句 = 3 行；任何条目跨行都会被日志切成碎片");
        foreach (var line in lines)
            Assert.IsFalse(line.Contains('\n'));
    }

    [TestMethod]
    public void NonPositiveTop_IsRejected()
    {
        var metrics = new SqlStatementMetrics();
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => SqlStatementMetricsReport.Format(metrics, 0, "1s"));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => SqlStatementMetricsReport.Format(metrics, -1, "1s"));
    }
}
