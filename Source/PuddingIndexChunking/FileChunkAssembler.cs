using System.Text;

namespace PuddingIndexChunking;

/// <summary>
/// Turns one source file into the chunks an index can store as separate documents (ADR-089 C1:
/// P0 outline → P1 doc comment → P2 code text) and applies the C2 filter to every chunk's text.
/// <para>
/// <b>Why chunks are cut per symbol instead of with a sliding line window</b>: with a sliding window a
/// symbol's declaration shares a document with whatever unrelated body text happens to sit on those
/// lines, so the outline cannot be weighted up and the symbol has to out-rank its own neighbourhood.
/// Here the outline arrives through <see cref="IOutlineSource"/> and each symbol yields exactly one P0
/// chunk whose text is the declaration (modifiers + kind + container + name + signature + doc summary)
/// — never the body.
/// </para>
/// <para>
/// <b>Partition invariant</b>: every non-blank line belongs to at most one chunk
/// (<see cref="IndexChunk.StartLine"/>..<see cref="IndexChunk.EndLine"/>). A line consumed by an
/// outline declaration is not re-emitted as code text, and a doc block that a P0 chunk already carries
/// is not re-emitted as P1. Tests assert this, because double-indexing a line would silently inflate
/// the index-size comparison this component exists to make measurable.
/// </para>
/// <para>
/// <b>Comment detection is line-leading and lexical, not syntactic</b>: a line counts as a comment only
/// when its first non-space characters are <c>//</c> or <c>/*</c>. A trailing comment stays in the code
/// text, and a comment marker inside a string literal is treated as code. Doing better requires a full
/// parser, which the adapter side already owns for the outline; the heuristic is stated here so its
/// effect on the numbers can be judged rather than assumed.
/// </para>
/// <para>Pure logic: no IO, no clock, no index. The only collaborator is the <see cref="IOutlineSource"/>
/// port, which is what keeps this class testable without Roslyn or any upper layer present.</para>
/// </summary>
public sealed class FileChunkAssembler
{
    private readonly IOutlineSource _outlineSource;

    public FileChunkAssembler(IOutlineSource outlineSource, ChunkingOptions? options = null)
    {
        _outlineSource = outlineSource ?? throw new ArgumentNullException(nameof(outlineSource));
        Options = options ?? new ChunkingOptions();
        Filter = new ChunkFilter(Options.Filter);

        if (Options.MaxCodeLinesPerChunk < 1)
            throw new ArgumentOutOfRangeException(
                nameof(options), Options.MaxCodeLinesPerChunk, "MaxCodeLinesPerChunk must be >= 1");
    }

    /// <summary>The active assembler configuration.</summary>
    public ChunkingOptions Options { get; }

    /// <summary>The filter applied to every chunk's text.</summary>
    public ChunkFilter Filter { get; }

    /// <summary>
    /// Assembles the chunks of one file. <paramref name="text"/> may be empty (<c>null</c> is treated as
    /// empty): an empty file yields a chunk-free assembly rather than an exception, because "no content"
    /// is a normal outcome for a file walk.
    /// </summary>
    public async Task<ChunkAssembly> AssembleAsync(
        string filePath,
        string? text,
        SourceLanguage language,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("file path is required", nameof(filePath));

        var lines = SplitLines(text);

        return IsCodeLanguage(language)
            ? await AssembleCodeAsync(filePath, text, lines, language, cancellationToken).ConfigureAwait(false)
            : AssembleProse(filePath, lines, language);
    }

    /// <summary>True for languages that have a keyword profile and an outline adapter.</summary>
    public static bool IsCodeLanguage(SourceLanguage language) =>
        language is SourceLanguage.CSharp or SourceLanguage.TypeScript or SourceLanguage.Python;

    // ── code files: P0 outline + P1 comments + P2 code text ───────────────────────────────

    private async Task<ChunkAssembly> AssembleCodeAsync(
        string filePath,
        string? text,
        IReadOnlyList<string> lines,
        SourceLanguage language,
        CancellationToken cancellationToken)
    {
        var outlineChunks = new List<IndexChunk>();
        var docChunks = new List<IndexChunk>();
        var codeChunks = new List<IndexChunk>();

        var consumedByDeclaration = new bool[lines.Count];
        var documentationDeclarations = new HashSet<int>();
        var symbolCount = 0;
        string? outlineError = null;

        if (Options.EmitOutlineChunks && lines.Count > 0)
        {
            var outline = await _outlineSource
                .GetOutlineAsync(filePath, text ?? string.Empty, language, cancellationToken)
                .ConfigureAwait(false);

            symbolCount = outline.Symbols.Count;
            outlineError = outline.Error;

            // Sorted so the output does not depend on an adapter's enumeration order.
            foreach (var symbol in outline.Symbols.OrderBy(s => s.StartLine).ThenBy(s => s.EndLine))
            {
                if (symbol.Kind is OutlineSymbolKind.Unknown or OutlineSymbolKind.Namespace)
                    continue;

                if (string.IsNullOrWhiteSpace(symbol.Name))
                    continue;

                var startLine = Clamp(symbol.StartLine, 1, lines.Count);
                var endLine = Clamp(symbol.EndLine, startLine, lines.Count);
                var carriesDocumentation = Options.IncludeDocumentationInOutlineChunk
                                           && !string.IsNullOrWhiteSpace(symbol.Documentation);

                var filtered = Filter.Apply(BuildOutlineText(symbol, carriesDocumentation), language);

                consumedByDeclaration[startLine - 1] = true;
                if (carriesDocumentation)
                    documentationDeclarations.Add(startLine);

                if (filtered.Length > 0)
                    outlineChunks.Add(new IndexChunk(ChunkKind.Outline, filtered, filePath, startLine, endLine));
            }
        }

        var commentGroups = FindCommentGroups(lines);
        var inComment = new bool[lines.Count];
        foreach (var group in commentGroups)
            for (var line = group.StartLine; line <= group.EndLine; line++)
                inComment[line - 1] = true;

        if (Options.EmitDocCommentChunks)
        {
            foreach (var group in commentGroups)
            {
                // The doc block directly above a symbol whose P0 chunk already carries the doc summary.
                if (documentationDeclarations.Contains(group.EndLine + 1))
                    continue;

                AddTextChunk(
                    docChunks, ChunkKind.DocComment, filePath, lines, group.StartLine - 1, group.EndLine - 1, language);
            }
        }

        if (Options.EmitCodeTextChunks)
        {
            var runStart = -1;
            for (var i = 0; i <= lines.Count; i++)
            {
                var isCode = i < lines.Count
                             && !inComment[i]
                             && !consumedByDeclaration[i]
                             && !string.IsNullOrWhiteSpace(lines[i]);

                if (isCode)
                {
                    if (runStart < 0)
                        runStart = i;

                    if (i - runStart + 1 < Options.MaxCodeLinesPerChunk)
                        continue;

                    AddTextChunk(codeChunks, ChunkKind.CodeText, filePath, lines, runStart, i, language);
                    runStart = -1;
                    continue;
                }

                if (runStart >= 0)
                {
                    AddTextChunk(codeChunks, ChunkKind.CodeText, filePath, lines, runStart, i - 1, language);
                    runStart = -1;
                }
            }
        }

        var chunks = new List<IndexChunk>(outlineChunks.Count + docChunks.Count + codeChunks.Count);
        chunks.AddRange(outlineChunks);
        chunks.AddRange(docChunks);
        chunks.AddRange(codeChunks);

        return new ChunkAssembly(filePath, language, lines.Count, symbolCount, chunks, outlineError);
    }

    // ── non-code files: language-neutral P1 blocks, no code-text tier ─────────────────────

    private ChunkAssembly AssembleProse(string filePath, IReadOnlyList<string> lines, SourceLanguage language)
    {
        var chunks = new List<IndexChunk>();

        if (Options.EmitDocCommentChunks)
        {
            var blockStart = -1;
            for (var i = 0; i <= lines.Count; i++)
            {
                var isBlock = i < lines.Count && !string.IsNullOrWhiteSpace(lines[i]);

                if (isBlock)
                {
                    if (blockStart < 0)
                        blockStart = i;

                    if (i - blockStart + 1 < Options.MaxCodeLinesPerChunk)
                        continue;

                    AddTextChunk(chunks, ChunkKind.DocComment, filePath, lines, blockStart, i, language);
                    blockStart = -1;
                    continue;
                }

                if (blockStart >= 0)
                {
                    AddTextChunk(chunks, ChunkKind.DocComment, filePath, lines, blockStart, i - 1, language);
                    blockStart = -1;
                }
            }
        }

        return new ChunkAssembly(filePath, language, lines.Count, 0, chunks);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────

    private void AddTextChunk(
        List<IndexChunk> target,
        ChunkKind kind,
        string filePath,
        IReadOnlyList<string> lines,
        int startIndex,
        int endIndex,
        SourceLanguage language)
    {
        var builder = new StringBuilder();
        for (var i = startIndex; i <= endIndex; i++)
        {
            if (i > startIndex)
                builder.Append('\n');

            builder.Append(lines[i]);
        }

        var filtered = Filter.Apply(builder.ToString(), language);
        if (filtered.Length == 0)
            return;

        target.Add(new IndexChunk(kind, filtered, filePath, startIndex + 1, endIndex + 1));
    }

    /// <summary>
    /// Full-line comment blocks, 1-based and inclusive. Only the leading marker counts, so
    /// <c>var url = "http://x";</c> is code and a trailing <c>// note</c> stays with its code line.
    /// </summary>
    private static List<(int StartLine, int EndLine)> FindCommentGroups(IReadOnlyList<string> lines)
    {
        var groups = new List<(int StartLine, int EndLine)>();
        var i = 0;

        while (i < lines.Count)
        {
            var trimmed = lines[i].TrimStart();

            if (trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                var start = i;
                while (i < lines.Count && lines[i].TrimStart().StartsWith("//", StringComparison.Ordinal))
                    i++;

                groups.Add((start + 1, i));
                continue;
            }

            if (trimmed.StartsWith("/*", StringComparison.Ordinal))
            {
                var start = i;
                while (i < lines.Count)
                {
                    var current = lines[i].TrimStart();
                    var isOpeningLine = i == start;
                    i++;

                    if (!isOpeningLine
                        && !current.StartsWith('*')
                        && !current.StartsWith("/*", StringComparison.Ordinal))
                    {
                        i--; // the block ended on the previous line; this line is code again
                        break;
                    }

                    if (current.Contains("*/", StringComparison.Ordinal))
                        break;
                }

                groups.Add((start + 1, i));
                continue;
            }

            i++;
        }

        return groups;
    }

    /// <summary>
    /// Builds the P0 text of one symbol: <c>modifiers kind Container.Name signature [documentation]</c>.
    /// The token string, not prose, is what gets indexed — the filter runs over it like any other tier.
    /// </summary>
    private static string BuildOutlineText(OutlineSymbol symbol, bool includeDocumentation)
    {
        var builder = new StringBuilder();

        AppendPart(builder, symbol.Modifiers);
        AppendPart(builder, KindText(symbol.Kind));

        var name = symbol.Name.Trim();
        if (!string.IsNullOrWhiteSpace(symbol.Container))
            name = symbol.Container.Trim() + "." + name;
        AppendPart(builder, name);

        if (!string.IsNullOrWhiteSpace(symbol.Signature))
        {
            var signature = symbol.Signature.Trim();
            if (builder.Length > 0
                && !signature.StartsWith('(')
                && !signature.StartsWith('<')
                && !signature.StartsWith(':'))
            {
                builder.Append(' ');
            }

            builder.Append(signature);
        }

        if (includeDocumentation && !string.IsNullOrWhiteSpace(symbol.Documentation))
            AppendPart(builder, symbol.Documentation);

        return builder.ToString();
    }

    private static void AppendPart(StringBuilder builder, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        if (builder.Length > 0)
            builder.Append(' ');

        builder.Append(value.Trim());
    }

    private static string KindText(OutlineSymbolKind kind) => kind switch
    {
        OutlineSymbolKind.Namespace => "namespace",
        OutlineSymbolKind.Type => "type",
        OutlineSymbolKind.Method => "method",
        OutlineSymbolKind.Property => "property",
        OutlineSymbolKind.Field => "field",
        OutlineSymbolKind.Event => "event",
        OutlineSymbolKind.Constructor => "constructor",
        OutlineSymbolKind.Indexer => "indexer",
        OutlineSymbolKind.Delegate => "delegate",
        OutlineSymbolKind.EnumMember => "enum member",
        _ => "symbol",
    };

    private static IReadOnlyList<string> SplitLines(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return [];

        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        return normalized.Split('\n');
    }

    private static int Clamp(int value, int minimum, int maximum) =>
        value < minimum ? minimum : value > maximum ? maximum : value;
}
