using System.Text.Json;
using Microsoft.ML.Tokenizers;
using PuddingTokenization;

namespace PuddingTokenizationTests;

[TestClass]
public sealed class UnicodeRegressionTests
{
    private static BpeTokenizer Load()
    {
        var path = Environment.GetEnvironmentVariable("PUDDING_DEEPSEEK_TOKENIZER_JSON");
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            Assert.Inconclusive("PUDDING_DEEPSEEK_TOKENIZER_JSON is required for exact vocabulary regression tests.");
        using var stream = File.OpenRead(path);
        return DeepSeekTokenizerJsonLoader.Load(stream).Tokenizer;
    }

    [TestMethod]
    public void CountAndEncode_MatchIndependentUnicodeVectors()
    {
        var tokenizer = Load();
        using var document = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "unicode-regressions.json")));
        var index = 0;
        foreach (var vector in document.RootElement.GetProperty("vectors").EnumerateArray())
        {
            var text = vector.GetProperty("text").GetString()!;
            var expected = vector.GetProperty("ids").EnumerateArray().Select(e => e.GetInt32()).ToArray();
            CollectionAssert.AreEqual(expected, tokenizer.EncodeToIds(text).ToArray(), $"Vector {index}: IDs");
            Assert.AreEqual(expected.Length, tokenizer.CountTokens(text), $"Vector {index}: count");
            Assert.AreEqual(text, tokenizer.Decode(expected), $"Vector {index}: round trip");
            index++;
        }
        Assert.IsGreaterThanOrEqualTo(16, index);
    }

    [TestMethod]
    public void PreTokenization_AlwaysPartitionsInput_AndOwnsSpanSnapshot()
    {
        var preTokenizer = Load().PreTokenizer!;
        foreach (var text in new[] { "你好 world 😀\n", "a\0b c\0d", "tail\0", "🧑🏽‍💻", string.Empty })
        {
            var buffer = text.ToCharArray();
            var delayed = preTokenizer.PreTokenize(buffer.AsSpan());
            Array.Fill(buffer, 'x');
            var ranges = delayed.ToArray();
            CollectionAssert.AreEqual(preTokenizer.PreTokenize(text).ToArray(), ranges);
            var end = 0;
            foreach (var (offset, length) in ranges)
            {
                Assert.AreEqual(end, offset, "Every input character must be covered exactly once.");
                Assert.IsTrue(length > 0 && length <= text.Length - offset, "Range must stay inside input.");
                end += length;
            }
            Assert.AreEqual(text.Length, end);
        }
    }

    [TestMethod]
    public void LongSharedTokenizer_IsStableUnderConcurrentCounting()
    {
        var tokenizer = Load();
        var text = string.Concat(Enumerable.Repeat("你好 world 😀\nabc\0def\n", 4000));
        var expected = tokenizer.EncodeToIds(text).Count;
        Assert.IsGreaterThan(4000, expected);
        Parallel.For(0, 16, _ => Assert.AreEqual(expected, tokenizer.CountTokens(text)));
    }
}
