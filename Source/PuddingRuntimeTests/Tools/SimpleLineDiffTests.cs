using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Tools;

/// <summary>
/// file_patch 回显 diff 的回归（2026-10-27）。
///
/// 缺陷：<see cref="SimpleLineDiff"/> 原来是「贪心前瞻」，把空白行 / 花括号当成定位锚点。
/// 只要插入点旁边存在这类重复行，它就会判定成「删除了一大段」，于是工具回显的 diff
/// 与实际改动完全不同 —— 调用方看到的是一屏错乱的行。
///
/// 这里的核心断言是：**纯插入不得产生任何 '-' 行**。旧实现在第 1 个用例上会失败。
/// </summary>
[TestClass]
public sealed class SimpleLineDiffTests
{
    [TestMethod]
    public void InsertingBlankLineBetweenContentLines_ProducesOnlyInsertions()
    {
        // 最小复现：旧文 ["a","b","","c"] → 新文在 a 之后插入一个空白行。
        // 旧贪心实现会把 "b" 判成删除（因为旧文里的空白行离得更近）。
        var original = "a\nb\n\nc\n";
        var current = "a\n\nb\n\nc\n";

        var diff = SimpleLineDiff.Render(original, current);

        Assert.IsFalse(
            diff.Split('\n').Any(line => line.StartsWith("- ", StringComparison.Ordinal)),
            $"纯插入不得出现删除行，实际 diff：\n{diff}");
        Assert.IsTrue(diff.Contains("+ "), $"应至少出现一行插入，实际 diff：\n{diff}");
    }

    [TestMethod]
    public void InsertingCommentBlockBesideBraceAnchors_DoesNotReportUnrelatedDeletions()
    {
        // 贴近真实场景：在 "}" 之后插入文档注释 + 空行 + 成员。
        var original =
            "class A\n{\n    int X { get; init; }\n}\n\n/// summary\nclass B\n{\n}\n";
        var current =
            "class A\n{\n    int X { get; init; }\n\n    /// doc\n    int Y { get; init; }\n}\n\n/// summary\nclass B\n{\n}\n";

        var diff = SimpleLineDiff.Render(original, current);

        Assert.IsFalse(
            diff.Split('\n').Any(line => line.StartsWith("- ", StringComparison.Ordinal)),
            $"插入注释块不应删除任何既有行，实际 diff：\n{diff}");
    }

    [TestMethod]
    public void PureDeletion_ProducesOnlyDeletions()
    {
        var diff = Normalize(SimpleLineDiff.Render("keep\ndrop\n", "keep\n"));

        Assert.AreEqual("- drop", diff, $"实际 diff：\n{diff}");
    }

    [TestMethod]
    public void SingleLineReplacement_ProducesOnePair()
    {
        var diff = Normalize(SimpleLineDiff.Render("alpha\nbeta\ngamma\n", "alpha\nBETA\ngamma\n"));

        Assert.AreEqual("- beta\n+ BETA", diff, $"实际 diff：\n{diff}");
    }

    /// <summary>
    /// Render 用 StringBuilder.AppendLine，在 Windows 上是 CRLF。断言前归一化，
    /// 否则会因行尾差异误报（内容本身是对的）。
    /// </summary>
    private static string Normalize(string diff) =>
        diff.Replace("\r\n", "\n").Trim();

    [TestMethod]
    public void IdenticalContent_ReportsNoVisibleChanges()
    {
        var diff = SimpleLineDiff.Render("same\nlines\n", "same\nlines\n");

        Assert.AreEqual("(no visible line changes)", diff.Trim());
    }

    [TestMethod]
    public void MoreThanTenChangeGroups_IsTruncatedWithMarker()
    {
        // 截断按【分组】计数，不是按行：必须让各组之间有未变的行分隔。
        var originalLines = new List<string>();
        var currentLines = new List<string>();
        for (var i = 0; i < 15; i++)
        {
            originalLines.Add($"old{i}");
            currentLines.Add($"new{i}");
            originalLines.Add($"stable{i}");
            currentLines.Add($"stable{i}");
        }

        var diff = SimpleLineDiff.Render(
            string.Join("\n", originalLines) + "\n",
            string.Join("\n", currentLines) + "\n");

        Assert.IsTrue(diff.Contains("... (more changes)"), $"超过分组上限应标注截断，实际 diff：\n{diff}");
    }
}
