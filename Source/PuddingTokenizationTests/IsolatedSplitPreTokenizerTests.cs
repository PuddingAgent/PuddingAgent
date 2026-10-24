using PuddingTokenization;

namespace PuddingTokenizationTests;

[TestClass]
public sealed class IsolatedSplitPreTokenizerTests
{
    [TestMethod]
    public void SequenceSplitsMatchesAgain_AndPreservesUnmatchedGapsAndTail()
    {
        var tokenizer = new IsolatedSplitPreTokenizer(
            [ScalarRegex.Create("[a-z]+"), ScalarRegex.Create("b")], []);
        CollectionAssert.AreEqual(new[] { (0, 1), (1, 1), (2, 1), (3, 1), (4, 2) },
            tokenizer.PreTokenize("!abc?\0").ToArray());
    }

    [TestMethod]
    public void SpecialTokensAreLongestFirst_AndBypassAllSplitStages()
    {
        var tokenizer = new IsolatedSplitPreTokenizer([ScalarRegex.Create(".")], ["<x>", "<x>long"]);
        CollectionAssert.AreEqual(new[] { (0, 1), (1, 7), (8, 1) },
            tokenizer.PreTokenize("a<x>longz").ToArray());
    }

    [TestMethod]
    public void ScalarCategoriesAndNegation_DoNotSplitSupplementaryCharacters()
    {
        var tokenizer = new IsolatedSplitPreTokenizer([ScalarRegex.Create(@"\p{N}{1,3}|[\p{L}\p{M}]+| ?[\p{P}\p{S}]+|[^\p{L}\p{P}\p{S}]")], []);
        var text = "𝟙𝟚𝟛𝟜𠮷 😀\0";
        CollectionAssert.AreEqual(new[] { (0, 6), (6, 2), (8, 2), (10, 3), (13, 1) },
            tokenizer.PreTokenize(text).ToArray());
        CollectionAssert.AreEqual(tokenizer.PreTokenize(text).ToArray(), tokenizer.PreTokenize(text.AsSpan()).ToArray());
    }

    [TestMethod]
    public void SpanEnumerationDoesNotRetainBorrowedStorage()
    {
        var tokenizer = new IsolatedSplitPreTokenizer([ScalarRegex.Create("[a-z]+")], []);
        var buffer = "abc!def".ToCharArray();
        var delayed = tokenizer.PreTokenize(buffer.AsSpan());
        Array.Fill(buffer, '!');
        CollectionAssert.AreEqual(new[] { (0, 3), (3, 1), (4, 3) }, delayed.ToArray());
    }
}
