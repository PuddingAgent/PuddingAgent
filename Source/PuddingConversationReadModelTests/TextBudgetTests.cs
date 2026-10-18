namespace PuddingConversationReadModelTests;

/// <summary>
/// UTF-8 有界截断规则（Plan §5.4；<see cref="TextBudget"/> 为组件内部合同，测试通过 InternalsVisibleTo 访问）。
/// </summary>
[TestClass]
public sealed class TextBudgetTests
{
    [TestMethod]
    public void Utf8Length_CountsEncodedBytes()
    {
        Assert.AreEqual(0, TextBudget.Utf8Length(string.Empty));
        Assert.AreEqual(3, TextBudget.Utf8Length("abc"));
        Assert.AreEqual(6, TextBudget.Utf8Length("你好"));
        Assert.AreEqual(4, TextBudget.Utf8Length("😀"));
        Assert.AreEqual(8, TextBudget.Utf8Length("😀😀"));
    }

    [TestMethod]
    public void TakeUtf8Prefix_ReturnsWholeTextWhenWithinBudget()
    {
        Assert.AreEqual("你好", TextBudget.TakeUtf8Prefix("你好", 6));
        Assert.AreEqual("😀", TextBudget.TakeUtf8Prefix("😀", 4));
        Assert.AreEqual("abc", TextBudget.TakeUtf8Prefix("abc", 100));
    }

    [TestMethod]
    public void TakeUtf8Prefix_TruncatesAsciiExactly()
        => Assert.AreEqual("abc", TextBudget.TakeUtf8Prefix("abcdef", 3));

    [TestMethod]
    public void TakeUtf8Prefix_DoesNotSplitChineseCodePoints()
    {
        var text = new string('文', 10); // 30 bytes
        var prefix = TextBudget.TakeUtf8Prefix(text, 20);

        Assert.AreEqual(new string('文', 6), prefix);
        Assert.AreEqual(18, TextBudget.Utf8Length(prefix));
    }

    [TestMethod]
    public void TakeUtf8Prefix_DoesNotSplitEmojiSurrogatePairs()
    {
        var text = TestData.Repeat("😀", 5); // 20 bytes

        Assert.AreEqual(TestData.Repeat("😀", 4), TextBudget.TakeUtf8Prefix(text, 18));
        Assert.AreEqual(16, TextBudget.Utf8Length(TextBudget.TakeUtf8Prefix(text, 18)));

        var prefix = TextBudget.TakeUtf8Prefix(text, 17);
        Assert.AreEqual(TestData.Repeat("😀", 4), prefix);
        TestData.AssertNoReplacementCharacters(prefix);
    }

    [TestMethod]
    public void TakeUtf8Prefix_ExactBoundaryKeepsWholeCodePoint()
    {
        Assert.AreEqual(TestData.Repeat("😀", 4), TextBudget.TakeUtf8Prefix(TestData.Repeat("😀", 4), 16));
        Assert.AreEqual(new string('文', 3), TextBudget.TakeUtf8Prefix(new string('文', 4), 9));
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    [DataRow(-100)]
    public void TakeUtf8Prefix_NonPositiveBudget_ReturnsEmpty(int maxBytes)
        => Assert.AreEqual(string.Empty, TextBudget.TakeUtf8Prefix("abc", maxBytes));

    [TestMethod]
    public void TakeUtf8Prefix_EmptyString_ReturnsEmpty()
        => Assert.AreEqual(string.Empty, TextBudget.TakeUtf8Prefix(string.Empty, 10));

    [TestMethod]
    public void TakeUtf8Tail_ReturnsWholeTextWhenWithinBudget()
    {
        Assert.AreEqual("你好", TextBudget.TakeUtf8Tail("你好", 6));
        Assert.AreEqual("😀", TextBudget.TakeUtf8Tail("😀", 4));
    }

    [TestMethod]
    public void TakeUtf8Tail_TruncatesChineseOnBoundary()
    {
        var text = new string('文', 10); // 30 bytes
        var tail = TextBudget.TakeUtf8Tail(text, 10);

        Assert.AreEqual(new string('文', 3), tail);
        Assert.AreEqual(9, TextBudget.Utf8Length(tail));
    }

    [TestMethod]
    public void TakeUtf8Tail_DoesNotSplitEmojiSurrogatePairs()
    {
        var text = TestData.Repeat("😀", 5) + "x"; // 21 bytes

        var tail = TextBudget.TakeUtf8Tail(text, 10);

        Assert.AreEqual(TestData.Repeat("😀", 2) + "x", tail);
        Assert.AreEqual(9, TextBudget.Utf8Length(tail));
        TestData.AssertNoReplacementCharacters(tail);
    }

    [TestMethod]
    public void TakeUtf8Tail_ExactBoundaryKeepsWholeCodePoint()
        => Assert.AreEqual(TestData.Repeat("😀", 4), TextBudget.TakeUtf8Tail(TestData.Repeat("😀", 4), 16));

    [TestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    [DataRow(-100)]
    public void TakeUtf8Tail_NonPositiveBudget_ReturnsEmpty(int maxBytes)
        => Assert.AreEqual(string.Empty, TextBudget.TakeUtf8Tail("abc", maxBytes));

    [TestMethod]
    public void TakeUtf8Tail_EmptyString_ReturnsEmpty()
        => Assert.AreEqual(string.Empty, TextBudget.TakeUtf8Tail(string.Empty, 10));

    [TestMethod]
    public void TakeUtf8Tail_ReturnsUtf8ValidSuffix()
    {
        var text = "abc" + TestData.Repeat("😀", 100);

        var tail = TextBudget.TakeUtf8Tail(text, 33);

        Assert.IsLessThanOrEqualTo(33, TextBudget.Utf8Length(tail));
        Assert.IsGreaterThanOrEqualTo(29, TextBudget.Utf8Length(tail));
        TestData.AssertNoReplacementCharacters(tail);
        StringAssert.EndsWith(tail, "😀");
    }
}
