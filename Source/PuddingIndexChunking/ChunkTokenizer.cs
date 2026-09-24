namespace PuddingIndexChunking;

/// <summary>
/// Deterministic tokeniser used by the filter to answer "is this token a keyword?" and "is this token
/// too short?".
/// <para>
/// <b>Definition</b>: a token is a maximal run of letters, digits and <c>_</c>. Everything else
/// (whitespace, punctuation, C#/TS operators, XML tags and comment markers left in a chunk) is a
/// separator. Letters are Unicode letters, so CJK runs survive as tokens.
/// </para>
/// <para>
/// <b>Known difference from the index analyser</b>: the Lucene side analyses text with jieba, which
/// may split a <c>_</c>-joined identifier into parts. This tokeniser keeps <c>_</c> inside a token,
/// so the filtered text never merges or splits identifiers itself — the filter only removes whole
/// tokens, and the engine's analyser still decides the final terms. The difference is one-directional:
/// it can make the filter slightly more conservative, never more aggressive.
/// </para>
/// </summary>
public static class ChunkTokenizer
{
    /// <summary>Maximum number of tokens the tokeniser will emit from one string (guards a runaway chunk).</summary>
    public const int MaxTokens = 200_000;

    /// <summary>Splits text into identifier-like tokens, preserving order and duplicates.</summary>
    public static IReadOnlyList<string> Tokenize(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return [];

        var tokens = new List<string>();
        var start = -1;

        for (var i = 0; i < text.Length; i++)
        {
            var isTokenChar = char.IsLetterOrDigit(text[i]) || text[i] == '_';

            if (isTokenChar)
            {
                if (start < 0)
                    start = i;
                continue;
            }

            if (start >= 0)
            {
                tokens.Add(text[start..i]);
                start = -1;

                if (tokens.Count >= MaxTokens)
                    return tokens;
            }
        }

        if (start >= 0)
            tokens.Add(text[start..]);

        return tokens;
    }

    /// <summary>True when the string contains at least one token.</summary>
    public static bool HasTokens(string? text) => Tokenize(text).Count > 0;
}
