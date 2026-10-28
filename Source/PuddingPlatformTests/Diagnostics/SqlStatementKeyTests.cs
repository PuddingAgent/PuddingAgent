using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingPlatform.Data.Diagnostics;

namespace PuddingPlatformTests.Diagnostics;

/// <summary>
/// SQL 归一化的契约。
/// </summary>
/// <remarks>
/// 这些断言存在的理由：归一化决定统计是否可用。合并过度 → 把两条不同查询算成一条，
/// 诊断被误导；合并不足 → key 爆炸，统计自己变成内存问题。两侧都是"看起来有数据、
/// 实际不可用"，所以两边都要用测试钉住。
/// </remarks>
[TestClass]
public sealed class SqlStatementKeyTests
{
    [TestMethod]
    public void NullOrWhitespace_BecomesEmptyKey()
    {
        Assert.AreEqual(SqlStatementKey.EmptyKey, SqlStatementKey.Normalize(null));
        Assert.AreEqual(SqlStatementKey.EmptyKey, SqlStatementKey.Normalize(""));
        Assert.AreEqual(SqlStatementKey.EmptyKey, SqlStatementKey.Normalize("   \r\n\t "));
    }

    [TestMethod]
    public void DifferentLiteralValues_CollapseToTheSameKey()
    {
        var a = SqlStatementKey.Normalize("SELECT * FROM t WHERE id = 1");
        var b = SqlStatementKey.Normalize("SELECT * FROM t WHERE id = 2");
        Assert.AreEqual(a, b, "只有字面量不同的同一查询必须合并，否则每行数据都会长出一个 key");
    }

    [TestMethod]
    public void DifferentParameterNames_CollapseToTheSameKey()
    {
        var a = SqlStatementKey.Normalize("SELECT * FROM t WHERE id = @p0 AND name = @p1");
        var b = SqlStatementKey.Normalize("SELECT * FROM t WHERE id = @id AND name = @nm");
        Assert.AreEqual(a, b);
    }

    [TestMethod]
    public void MixedParameterPrefixes_AllCollapse()
    {
        var a = SqlStatementKey.Normalize("SELECT $a, :b, @c");
        Assert.AreEqual("SELECT ?, ?, ?", a);
    }

    [TestMethod]
    public void DigitsInsideIdentifiers_ArePreserved()
    {
        // 这是最容易被改坏的一条：若把 table1 的 1 也替换，两条不同的查询会被合并。
        var a = SqlStatementKey.Normalize("SELECT * FROM table1 WHERE col2 = 5");
        var b = SqlStatementKey.Normalize("SELECT * FROM table2 WHERE col2 = 5");
        Assert.AreNotEqual(a, b, "标识符里的数字是名字的一部分，不能被当成字面量替换");
        StringAssert.Contains(a, "table1");
        StringAssert.Contains(b, "table2");
    }

    [TestMethod]
    public void StringLiteral_IsReplacedWholesale_IncludingInnerQuotes()
    {
        var key = SqlStatementKey.Normalize("SELECT * FROM t WHERE name = 'O''Brien' AND x = 7");
        Assert.AreEqual("SELECT * FROM t WHERE name = ? AND x = ?", key,
            "'' 是转义引号，字符串整体替换为一个 ?");
    }

    [TestMethod]
    public void ParameterMarkerInsideAStringLiteral_IsNotTreatedAsAParameter()
    {
        // 字符串必须先于参数规则处理，否则这个 @notaparam 会变成一个 ? 并吞掉后面的字符。
        var key = SqlStatementKey.Normalize("SELECT * FROM t WHERE note = 'mail @notaparam now'");
        Assert.AreEqual("SELECT * FROM t WHERE note = ?", key);
    }

    [TestMethod]
    public void DecimalLiteral_IsConsumedAsOnePlaceholder()
    {
        var key = SqlStatementKey.Normalize("SELECT 1.5 AS a");
        Assert.AreEqual("SELECT ? AS a", key, "小数必须整体替换，不能变成 ?.5");
    }

    [TestMethod]
    public void Whitespace_IsCollapsedAndTrimmed()
    {
        var a = SqlStatementKey.Normalize("SELECT   *\r\n\tFROM   t");
        var b = SqlStatementKey.Normalize("SELECT * FROM t");
        Assert.AreEqual(b, a);
    }

    [TestMethod]
    public void CaseIsPreserved_BecauseItAidsReading()
    {
        var key = SqlStatementKey.Normalize("SELECT Id FROM T");
        StringAssert.Contains(key, "SELECT");
        StringAssert.Contains(key, "Id");
    }

    [TestMethod]
    public void StructuredPunctuationSurvives()
    {
        var key = SqlStatementKey.Normalize("SELECT a, b FROM t WHERE c IN (1, 2, 3)");
        Assert.AreEqual("SELECT a, b FROM t WHERE c IN (?, ?, ?)", key);
    }
}

/// <summary>
/// 语句统计的契约：有界、线程安全、排序稳定。
/// </summary>
[TestClass]
public sealed class SqlStatementMetricsTests
{
    [TestMethod]
    public void SameShape_AccumulatesIntoSingleEntry()
    {
        var metrics = new SqlStatementMetrics();
        metrics.Record("SELECT 1 FROM t", 10, 1);
        metrics.Record("SELECT 2 FROM t", 30, 1);

        var snap = metrics.Snapshot();
        Assert.AreEqual(1, snap.Count, "归一化后应只有一个条目");
        Assert.AreEqual(2, snap[0].Calls);
        Assert.AreEqual(40, snap[0].TotalTicks);
        Assert.AreEqual(30, snap[0].MaxTicks, "最大值必须是单次最大，不是累计");
        Assert.AreEqual(20, snap[0].AverageTicks);
    }

    [TestMethod]
    public void DifferentShapes_StaySeparate()
    {
        var metrics = new SqlStatementMetrics();
        metrics.Record("SELECT * FROM a", 1, 0);
        metrics.Record("SELECT * FROM b", 1, 0);
        Assert.AreEqual(2, metrics.Snapshot().Count);
    }

    [TestMethod]
    public void EmptySql_IsIgnored()
    {
        var metrics = new SqlStatementMetrics();
        metrics.Record(null, 5, 1);
        metrics.Record("   ", 5, 1);
        Assert.AreEqual(0, metrics.Snapshot().Count);
    }

    [TestMethod]
    public void BoundIsEnforced_AndOverflowKeepsCountingWithoutGrowing()
    {
        var metrics = new SqlStatementMetrics(maxTrackedStatements: 3);

        // 造 10 种不同形状，只有前 3 种能被单独跟踪，其余全部进 overflow。
        for (var i = 0; i < 10; i++)
            metrics.Record("SELECT * FROM t" + (char)('a' + i), 1, 0);

        Assert.AreEqual(4, metrics.TrackedStatementCount,
            "上限 3 个真实形状 + 1 个 overflow 项；再多的形状不得让字典继续增长");
        Assert.IsTrue(metrics.Overflowed);

        SqlStatementSnapshot? overflow = null;
        foreach (var s in metrics.Snapshot())
        {
            if (s.Key == SqlStatementMetrics.OverflowKey) { overflow = s; break; }
        }
        Assert.IsNotNull(overflow);
        Assert.AreEqual(7, overflow!.Calls, "溢出的 7 次必须被计数，不能静默丢失");
    }

    [TestMethod]
    public void NonPositiveBound_IsRejectedAsCallerError()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new SqlStatementMetrics(0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new SqlStatementMetrics(-1));
    }

    [TestMethod]
    public void Snapshot_IsOrderedByCallsDescending()
    {
        var metrics = new SqlStatementMetrics();
        metrics.Record("SELECT * FROM rare", 1, 0);
        for (var i = 0; i < 5; i++)
            metrics.Record("SELECT * FROM hot", 1, 0);

        var snap = metrics.Snapshot();
        Assert.AreEqual("SELECT * FROM hot", snap[0].Key);
        Assert.AreEqual("SELECT * FROM rare", snap[1].Key);
    }

    [TestMethod]
    public void NegativeElapsed_IsClampedToZero()
    {
        var metrics = new SqlStatementMetrics();
        metrics.Record("SELECT * FROM t", -100, 0);
        Assert.AreEqual(0, metrics.Snapshot()[0].TotalTicks);
    }

    [TestMethod]
    public void ConcurrentRecording_LosesNoIncrements()
    {
        var metrics = new SqlStatementMetrics();
        const int threads = 8;
        const int perThread = 2000;

        Parallel.For(0, threads, _ =>
        {
            for (var i = 0; i < perThread; i++)
                metrics.Record("SELECT * FROM t WHERE id = @p", 1, 0);
        });

        var snap = metrics.Snapshot();
        Assert.AreEqual(1, snap.Count);
        Assert.AreEqual(threads * perThread, snap[0].Calls);
    }
}
