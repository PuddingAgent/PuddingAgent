using Pudding.Contracts.Desktop;

namespace Pudding.ContractsTests;

/// <summary>
/// 行级差异（设计方案 §145）：削公共前后缀后给出**精确**的新增/删除行；
/// 预算不足如实标注截断；**永远不发明没出现过的行**。
/// </summary>
public sealed class BrowserTextDiffTests
{
    private static IReadOnlyList<string> Lines(params string[] lines) => lines;

    [Fact]
    public void IdenticalText_HasNoDiff()
    {
        var diff = BrowserTextDiff.Compute(Lines("a", "b", "c"), Lines("a", "b", "c"));

        Assert.True(diff.IsEmpty);
        Assert.Equal(0, diff.AddedCount);
        Assert.Equal(0, diff.RemovedCount);
        Assert.False(diff.Truncated);
    }

    [Fact]
    public void AppendedLines_AreReportedExactly()
    {
        var diff = BrowserTextDiff.Compute(Lines("a", "b"), Lines("a", "b", "c", "d"));

        Assert.Equal(["c", "d"], diff.Added);
        Assert.Empty(diff.Removed);
        Assert.Equal(2, diff.AddedCount);
        Assert.Equal(0, diff.RemovedCount);
    }

    [Fact]
    public void RemovedAndInsertedLines_AreReportedPerSide()
    {
        var diff = BrowserTextDiff.Compute(Lines("a", "b", "c", "d"), Lines("a", "x", "y", "d"));

        // 公共前后缀被削掉，中间段精确落在两侧。
        Assert.Equal(["x", "y"], diff.Added);
        Assert.Equal(["b", "c"], diff.Removed);
        Assert.Equal(2, diff.AddedCount);
        Assert.Equal(2, diff.RemovedCount);
    }

    [Fact]
    public void EmptyBaseline_MeansEverythingIsNew()
    {
        var diff = BrowserTextDiff.Compute([], Lines("a", "b"));

        Assert.Equal(["a", "b"], diff.Added);
        Assert.Empty(diff.Removed);
    }

    [Fact]
    public void RepeatedLines_DoNotConfuseThePrefixSuffixTrim()
    {
        // 全同行的文本：公共前后缀会吃掉整段，差异必须为空（不能用"出现次数"猜）。
        var diff = BrowserTextDiff.Compute(Lines("x", "x", "x"), Lines("x", "x", "x"));
        Assert.True(diff.IsEmpty);

        // 首尾相同、中间不同：只有中间那段算差异。
        var middle = BrowserTextDiff.Compute(Lines("x", "a", "x"), Lines("x", "b", "x"));
        Assert.Equal(["b"], middle.Added);
        Assert.Equal(["a"], middle.Removed);
    }

    [Fact]
    public void OverBudgetDiff_IsFlaggedInsteadOfSilentlyTrimmed()
    {
        var baseline = Enumerable.Range(0, 50).Select(index => $"old-{index}").ToArray();
        var current = Enumerable.Range(0, 50).Select(index => $"new-{index}").ToArray();

        var diff = BrowserTextDiff.Compute(baseline, current, maxLines: 10);

        Assert.True(diff.Truncated);
        Assert.Equal(10, diff.Added.Count);
        Assert.Equal(10, diff.Removed.Count);
        // 计数是**真实**的（不是被截断后的长度）：调用方据此知道差异有多大。
        Assert.Equal(50, diff.AddedCount);
        Assert.Equal(50, diff.RemovedCount);
    }

    [Fact]
    public void DiffNeverInventsLines()
    {
        var baseline = Lines("alpha", "beta");
        var current = Lines("alpha", "gamma", "delta");
        var diff = BrowserTextDiff.Compute(baseline, current);

        foreach (var line in diff.Added)
        {
            Assert.Contains(line, current);
        }

        foreach (var line in diff.Removed)
        {
            Assert.Contains(line, baseline);
        }
    }

    [Fact]
    public void SplitLines_NormalizesLineEndingsAndEmptyText()
    {
        Assert.Empty(BrowserTextDiff.SplitLines(null));
        Assert.Empty(BrowserTextDiff.SplitLines(string.Empty));
        Assert.Equal(["a", "b"], BrowserTextDiff.SplitLines("a\r\nb"));
        Assert.Equal(["a", "b"], BrowserTextDiff.SplitLines("a\rb"));
        Assert.Equal(["a", "b"], BrowserTextDiff.SplitLines("a\nb"));
    }

    [Fact]
    public void Compute_RejectsNonsenseBudgets()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BrowserTextDiff.Compute([], [], maxLines: 0));
        Assert.Throws<ArgumentNullException>(() => BrowserTextDiff.Compute(null!, []));
    }
}
