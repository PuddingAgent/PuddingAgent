using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingBrowser.Automation;

namespace PuddingBrowser.AutomationTests;

/// <summary>
/// 文件上传的 CDP 请求规划（§7）：三步之间的形状约定与**选择器转义**都能在进程内验证。
/// </summary>
[TestClass]
public sealed class FileUploadPlanTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [TestMethod]
    public void SelectorEscapesQuotesAndBackslashes()
    {
        // ref 里出现引号/反斜杠时必须转义，否则选择器无效或选错元素。
        Assert.AreEqual("[data-pudding-ref=\"p0123-v1-n1\"]", FileUploadPlan.SelectorForRef("p0123-v1-n1"));
        Assert.AreEqual(
            "[data-pudding-ref=\"a\\\"b\"]", FileUploadPlan.SelectorForRef("a\"b"));
        Assert.AreEqual(
            "[data-pudding-ref=\"a\\\\b\"]", FileUploadPlan.SelectorForRef("a\\b"));

        Assert.ThrowsExactly<ArgumentException>(() => FileUploadPlan.SelectorForRef("  "));
    }

    [TestMethod]
    public void TheThreeStepsCarryWhatTheNextStepNeeds()
    {
        var document = Parse(FileUploadPlan.BuildDocumentParameters());
        Assert.AreEqual(0, document.GetProperty("depth").GetInt32());

        var query = Parse(FileUploadPlan.BuildQueryParameters(rootNodeId: 7, elementRef: "ref-1"));
        Assert.AreEqual(7, query.GetProperty("nodeId").GetInt32());
        Assert.AreEqual("[data-pudding-ref=\"ref-1\"]", query.GetProperty("selector").GetString());

        var set = Parse(FileUploadPlan.BuildSetFilesParameters(
            nodeId: 42, files: ["C:\\tmp\\a.txt", "C:\\tmp\\b.txt"]));
        Assert.AreEqual(42, set.GetProperty("nodeId").GetInt32());
        Assert.AreEqual(2, set.GetProperty("files").GetArrayLength());
        Assert.AreEqual("C:\\tmp\\b.txt", set.GetProperty("files")[1].GetString());

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => FileUploadPlan.BuildQueryParameters(-1, "ref"));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => FileUploadPlan.BuildSetFilesParameters(-1, ["C:\\a"]));
    }

    [TestMethod]
    public void FileListIsValidatedBeforeTouchingTheBrowser()
    {
        Assert.IsTrue(FileUploadPlan.TryValidateFiles(["C:\\tmp\\a.txt"], out var ok), ok);
        Assert.IsNull(ok);

        Assert.IsFalse(FileUploadPlan.TryValidateFiles(null, out var empty));
        StringAssert.Contains(empty, "at least one");

        Assert.IsFalse(FileUploadPlan.TryValidateFiles([], out _));

        // 相对路径的含义取决于浏览器进程的工作目录 ⇒ 只接受绝对路径。
        Assert.IsFalse(FileUploadPlan.TryValidateFiles(["relative\\a.txt"], out var relative));
        StringAssert.Contains(relative, "absolute");

        Assert.IsFalse(FileUploadPlan.TryValidateFiles(["C:\\ok", "  "], out var blank));
        StringAssert.Contains(blank, "must not be empty");

        var tooMany = Enumerable.Range(0, FileUploadPlan.MaxFiles + 1)
            .Select(index => $"C:\\tmp\\{index}.txt").ToArray();
        Assert.IsFalse(FileUploadPlan.TryValidateFiles(tooMany, out var many));
        StringAssert.Contains(many, "at most");
    }

    [TestMethod]
    public void NodeIdsAreReadFromResponsesOrRefused()
    {
        // JsonDocument 必须在用 RootElement 期间保持存活（否则元素会随文档被回收而失效）。
        using var document = JsonDocument.Parse("""{"root":{"nodeId":5}}""");
        Assert.IsTrue(FileUploadPlan.TryReadRootNodeId(document.RootElement, out var root));
        Assert.AreEqual(5, root);

        using var empty = JsonDocument.Parse("{}");
        Assert.IsFalse(FileUploadPlan.TryReadRootNodeId(empty.RootElement, out _));

        using var query = JsonDocument.Parse("""{"nodeId":9}""");
        Assert.IsTrue(FileUploadPlan.TryReadNodeId(query.RootElement, out var node));
        Assert.AreEqual(9, node);

        // 查不到节点（nodeId=0 或缺字段）由调用方按"引用过期"处理，这里只如实读出。
        using var zero = JsonDocument.Parse("""{"nodeId":0}""");
        Assert.IsTrue(FileUploadPlan.TryReadNodeId(zero.RootElement, out var zeroId));
        Assert.AreEqual(0, zeroId);

        using var wrongType = JsonDocument.Parse("""{"nodeId":"x"}""");
        Assert.IsFalse(FileUploadPlan.TryReadNodeId(wrongType.RootElement, out _));
    }
}
