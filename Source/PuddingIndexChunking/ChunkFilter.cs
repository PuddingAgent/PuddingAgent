namespace PuddingIndexChunking;

/// <summary>
/// The C2 filter: turns chunk text into the token string an index actually receives, and reports what
/// each rule removed.
/// <para>
/// <b>Rule order</b> is fixed and stated once: the minimum-length rule is evaluated <b>first</b>, then
/// the keyword rule. A token that is both short and a keyword (e.g. <c>if</c>) is therefore counted as
/// "removed as short" — this matters for the experiment, because with the length rule off the same
/// token must then be caught by the keyword rule, and the counts have to show that movement instead of
/// double-counting it.
/// </para>
/// <para>Pure: no IO, no state beyond the options, no engine.</para>
/// </summary>
public sealed class ChunkFilter
{
    private readonly Dictionary<SourceLanguage, IReadOnlySet<string>> _stopWordCache = [];

    public ChunkFilter(ChunkFilterOptions? options = null)
    {
        Options = options ?? new ChunkFilterOptions();
    }

    /// <summary>The active configuration.</summary>
    public ChunkFilterOptions Options { get; }

    /// <summary>Effective minimum token length for a language (option override, else the language default).</summary>
    public int MinimumTokenLengthFor(SourceLanguage language) =>
        Options.MinimumTokenLength ?? ChunkFilterRules.DefaultMinimumTokenLengthFor(language);

    /// <summary>The keyword set consulted for a language.</summary>
    public IReadOnlySet<string> StopWordsFor(SourceLanguage language)
    {
        if (_stopWordCache.TryGetValue(language, out var cached))
            return cached;

        var stopWords = ChunkFilterRules.DefaultStopWords(language);
        _stopWordCache[language] = stopWords;
        return stopWords;
    }

    /// <summary>True when the token is shorter than the language's minimum length.</summary>
    public bool IsShortToken(string token, SourceLanguage language)
    {
        if (string.IsNullOrEmpty(token))
            return true;

        return token.Length < MinimumTokenLengthFor(language);
    }

    /// <summary>True when the token is a keyword of the language (case-insensitive when configured).</summary>
    public bool IsStopWord(string token, SourceLanguage language)
    {
        if (string.IsNullOrEmpty(token))
            return false;

        var candidate = Options.IgnoreCase ? token.ToLowerInvariant() : token;
        return StopWordsFor(language).Contains(candidate);
    }

    /// <summary>Applies both enabled rules to one token.</summary>
    public bool ShouldKeep(string token, SourceLanguage language) =>
        !(Options.RemoveShortTokens && IsShortToken(token, language))
        && !(Options.RemoveStopWords && IsStopWord(token, language));

    /// <summary>Keeps the tokens that survive the enabled rules, preserving input order and duplicates.</summary>
    public IReadOnlyList<string> FilterTokens(IEnumerable<string> tokens, SourceLanguage language)
    {
        ArgumentNullException.ThrowIfNull(tokens);

        var kept = new List<string>();
        foreach (var token in tokens)
            if (ShouldKeep(token, language))
                kept.Add(token);

        return kept;
    }

    /// <summary>
    /// Tokenises, filters and re-joins: the exact transformation a chunk's text goes through before it
    /// reaches an index. Returns the empty string when everything was filtered out.
    /// </summary>
    public string Apply(string? text, SourceLanguage language) => ApplyDetailed(text, language).Text;

    /// <summary>
    /// Same as <see cref="Apply"/> but also returns the per-rule counts, so a corpus-wide run can
    /// report how many tokens each rule removed instead of only showing the smaller index.
    /// </summary>
    public FilteredChunkText ApplyDetailed(string? text, SourceLanguage language)
    {
        var tokens = ChunkTokenizer.Tokenize(text);
        var kept = new List<string>(tokens.Count);
        var removedAsShort = 0;
        var removedAsStopWord = 0;

        foreach (var token in tokens)
        {
            if (Options.RemoveShortTokens && IsShortToken(token, language))
            {
                removedAsShort++;
                continue;
            }

            if (Options.RemoveStopWords && IsStopWord(token, language))
            {
                removedAsStopWord++;
                continue;
            }

            kept.Add(token);
        }

        var joined = kept.Count == 0 ? string.Empty : string.Join(' ', kept);
        return new FilteredChunkText(
            joined,
            new FilterReport(tokens.Count, kept.Count, removedAsShort, removedAsStopWord));
    }
}
