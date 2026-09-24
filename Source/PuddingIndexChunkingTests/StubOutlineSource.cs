using PuddingIndexChunking;

namespace PuddingIndexChunkingTests;

/// <summary>
/// Test double for the <see cref="IOutlineSource"/> port.
/// <para>
/// Its existence is the point of the port: the component's outline-dependent behaviour (per-symbol
/// chunk cutting, doc-block suppression, line clamping) can be driven by an exact, hand-written outline
/// instead of by whatever Roslyn happens to produce — so a failing assertion names the rule, not the
/// parser.
/// </para>
/// </summary>
internal sealed class StubOutlineSource : IOutlineSource
{
    private readonly Func<string, string, SourceLanguage, OutlineSymbolSet> _factory;

    public StubOutlineSource(params OutlineSymbol[] symbols)
        : this((path, _, _) => new OutlineSymbolSet(path, symbols))
    {
    }

    public StubOutlineSource(Func<string, string, SourceLanguage, OutlineSymbolSet> factory)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    /// <summary>A source that reports "no symbols" (as an unsupported language would).</summary>
    public static StubOutlineSource WithNoSymbols() => new();

    /// <summary>Number of calls, so a test can assert the port is not consulted when it must not be.</summary>
    public int CallCount { get; private set; }

    /// <summary>Language of the most recent call.</summary>
    public SourceLanguage? LastLanguage { get; private set; }

    /// <summary>Source text of the most recent call.</summary>
    public string? LastSourceText { get; private set; }

    public Task<OutlineSymbolSet> GetOutlineAsync(
        string filePath,
        string sourceText,
        SourceLanguage language,
        CancellationToken cancellationToken = default)
    {
        CallCount++;
        LastLanguage = language;
        LastSourceText = sourceText;
        return Task.FromResult(_factory(filePath, sourceText, language));
    }
}
