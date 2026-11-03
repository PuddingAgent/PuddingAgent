using Microsoft.VisualStudio.TestTools.UnitTesting;
using Pudding.Contracts.Computer;
using PuddingComputer.UIAutomation;

namespace PuddingComputer.UIAutomationTests;

/// <summary>
/// 匹配规则（唯一真源）与写入器的判定分支。
/// 这些用例不碰真实窗口，只钉住「规则本身」和「不调用 COM 就不能成功」这两件事。
/// </summary>
[TestClass]
public sealed class UiaValueWriterTests
{
    [TestMethod]
    public void MatchesFields_EmptySelector_MatchesNothing()
    {
        // 空选择器匹配一切是最危险的默认值：调用方漏填字段就会静默选中第一个元素。
        var selector = new ElementSelector();

        Assert.IsFalse(UiaNodeMatching.MatchesFields(selector, "any", "any", "Edit", "Edit"));
    }

    [TestMethod]
    public void MatchesFields_NameIsCaseSensitive()
    {
        var selector = new ElementSelector { Name = "Search" };

        Assert.IsTrue(UiaNodeMatching.MatchesFields(selector, null, "Search", null, null));
        Assert.IsFalse(UiaNodeMatching.MatchesFields(selector, null, "search", null, null));
    }

    [TestMethod]
    public void MatchesFields_AutomationIdIsCaseSensitive()
    {
        var selector = new ElementSelector { AutomationId = "SearchBox" };

        Assert.IsTrue(UiaNodeMatching.MatchesFields(selector, "SearchBox", null, null, null));
        Assert.IsFalse(UiaNodeMatching.MatchesFields(selector, "searchbox", null, null, null));
    }

    [TestMethod]
    public void MatchesFields_ControlTypeAndClassNameIgnoreCase()
    {
        var selector = new ElementSelector { ControlType = "Edit", ClassName = "ProseMirror" };

        Assert.IsTrue(UiaNodeMatching.MatchesFields(selector, null, null, "edit", "prosemirror"));
        Assert.IsTrue(UiaNodeMatching.MatchesFields(selector, null, null, "Edit", "ProseMirror"));
    }

    [TestMethod]
    public void MatchesFields_AllGivenFieldsMustMatch()
    {
        var selector = new ElementSelector { Name = "Search", ControlType = "Edit" };

        Assert.IsTrue(UiaNodeMatching.MatchesFields(selector, null, "Search", "Edit", null));
        Assert.IsFalse(UiaNodeMatching.MatchesFields(selector, null, "Search", "Button", null));
        Assert.IsFalse(UiaNodeMatching.MatchesFields(selector, null, "Other", "Edit", null));
    }

    [TestMethod]
    public void MatchesFields_NullFieldsOnSelectorAreIgnored()
    {
        var selector = new ElementSelector { Name = "Search" };

        Assert.IsTrue(UiaNodeMatching.MatchesFields(selector, automationId: null, name: "Search", controlType: null, className: null));
    }

    [TestMethod]
    public void WriteValue_EmptySelector_ReturnsNotFoundWithoutTouchingUia()
    {
        using var writer = new UiaValueWriter();

        var result = writer.WriteValue(new IntPtr(0x1234), new ElementSelector(), "text");

        Assert.AreEqual(UiaWriteStatus.NotFound, result.Status);
        Assert.IsFalse(result.IsVerified);
        Assert.IsNull(result.ReadBack);
    }

    [TestMethod]
    public void WriteValue_ZeroHandle_ReturnsFailed()
    {
        using var writer = new UiaValueWriter();
        var selector = new ElementSelector { Name = "Search" };

        var result = writer.WriteValue(IntPtr.Zero, selector, "text");

        Assert.AreEqual(UiaWriteStatus.Failed, result.Status);
        Assert.IsFalse(result.IsVerified);
    }

    [TestMethod]
    public void Invoke_EmptySelector_ReturnsNotFoundWithoutTouchingUia()
    {
        using var writer = new UiaValueWriter();

        var result = writer.Invoke(new IntPtr(0x1234), new ElementSelector());

        Assert.AreEqual(UiaInvokeStatus.NotFound, result.Status);
        Assert.IsFalse(result.Invoked);
    }

    [TestMethod]
    public void Invoke_ZeroHandle_ReturnsFailed()
    {
        using var writer = new UiaValueWriter();
        var selector = new ElementSelector { Name = "Search" };

        var result = writer.Invoke(IntPtr.Zero, selector);

        Assert.AreEqual(UiaInvokeStatus.Failed, result.Status);
        Assert.IsFalse(result.Invoked);
    }
}
