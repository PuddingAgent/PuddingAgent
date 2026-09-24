using PuddingIndexChunking;

namespace PuddingIndexChunkingTests;

/// <summary>
/// Assembler behaviour: outline chunks are cut per symbol (never by a sliding line window), the tiers
/// partition the file's lines, filtering really removes keywords/short tokens, and an empty or
/// comment-only file is a normal outcome rather than an exception.
/// <para>
/// The fixture below is line-numbered in the test body on purpose: every expected line range in this
/// file can be checked by reading the fixture, without running anything.
/// </para>
/// </summary>
[TestClass]
public sealed class FileChunkAssemblerTests
{
    private const string CsFile = "Source/Demo/Worker.cs";

    /// <summary>
    /// 1 namespace Demo;
    /// 2 (blank)
    /// 3 /// &lt;summary&gt;Maintains the index.&lt;/summary&gt;
    /// 4 public sealed class Worker : IWorker
    /// 5 {
    /// 6     private int _attempts;
    /// 7 (blank)
    /// 8     public void Pump(int rounds)
    /// 9     {
    /// 10        _attempts = rounds * 2;
    /// 11    }
    /// 12 }
    /// </summary>
    private static string[] FixtureLines() =>
    [
        "namespace Demo;",
        "",
        "/// <summary>Maintains the index.</summary>",
        "public sealed class Worker : IWorker",
        "{",
        "    private int _attempts;",
        "",
        "    public void Pump(int rounds)",
        "    {",
        "        _attempts = rounds * 2;",
        "    }",
        "}",
    ];

    private static OutlineSymbol[] FixtureSymbols() =>
    [
        new OutlineSymbol("Worker", OutlineSymbolKind.Type, 4, 12, " : IWorker", "public sealed", null, "Maintains the index."),
        new OutlineSymbol("_attempts", OutlineSymbolKind.Field, 6, 6, null, "private", "Worker"),
        new OutlineSymbol("Pump", OutlineSymbolKind.Method, 8, 11, "(int rounds)", "public", "Worker"),
    ];

    private static FileChunkAssembler Assembler(out StubOutlineSource outline, ChunkingOptions? options = null)
    {
        outline = new StubOutlineSource(FixtureSymbols());
        return new FileChunkAssembler(outline, options);
    }

    private static string Lines(params string[] lines) => string.Join('\n', lines);

    [TestMethod]
    public async Task Outline_Chunks_Are_Cut_Per_Symbol_And_Never_Contain_Body_Text()
    {
        var assembler = Assembler(out var outline);
        var assembly = await assembler.AssembleAsync(CsFile, Lines(FixtureLines()), SourceLanguage.CSharp);

        Assert.AreEqual(1, outline.CallCount, "the outline port is consulted once per file");
        Assert.AreEqual(3, assembly.OutlineSymbolCount, "the adapter reported three symbols");
        Assert.AreEqual(12, assembly.LineCount);

        var outlineChunks = assembly.Chunks.Where(c => c.Kind == ChunkKind.Outline).ToArray();
        Assert.HasCount(3, outlineChunks, "one P0 chunk per symbol — not one per line window");

        var typeChunk = outlineChunks.Single(c => c.Text.Contains("Worker", StringComparison.Ordinal) && c.Text.Contains("IWorker", StringComparison.Ordinal));
        Assert.AreEqual(4, typeChunk.StartLine);
        Assert.AreEqual(12, typeChunk.EndLine);
        StringAssert.Contains(typeChunk.Text, "type");
        StringAssert.Contains(typeChunk.Text, "Maintains", "the documentation summary is part of the P0 payload");
        Assert.IsFalse(
            typeChunk.Text.Contains("public", StringComparison.Ordinal),
            "'public' is a C# keyword, so the C2 filter removes it from the P0 text as well");
        Assert.IsFalse(typeChunk.Text.Contains("sealed", StringComparison.Ordinal));
        Assert.IsFalse(typeChunk.Text.Contains("_attempts", StringComparison.Ordinal), "the declaration chunk must not carry the class body");
        Assert.IsFalse(typeChunk.Text.Contains("Pump(", StringComparison.Ordinal));

        var methodChunk = outlineChunks.Single(c => c.Text.Contains("Pump", StringComparison.Ordinal));
        StringAssert.Contains(methodChunk.Text, "method");
        StringAssert.Contains(methodChunk.Text, "rounds");
        Assert.IsFalse(methodChunk.Text.Contains("_attempts = rounds", StringComparison.Ordinal), "the method body stays out of the outline tier");

        // Tier ordering: P0 first, then P1, then P2.
        CollectionAssert.AreEqual(
            new[] { ChunkKind.Outline, ChunkKind.Outline, ChunkKind.Outline, ChunkKind.CodeText, ChunkKind.CodeText },
            assembly.Chunks.Select(c => c.Kind).ToArray());
    }

    [TestMethod]
    public async Task Symbol_Documentation_Is_Carried_By_The_Outline_Chunk_And_Its_Comment_Block_Is_Not_Indexed_Twice()
    {
        var assembler = Assembler(out _);
        var assembly = await assembler.AssembleAsync(CsFile, Lines(FixtureLines()), SourceLanguage.CSharp);

        Assert.IsTrue(
            assembly.Chunks.Any(c => c.Kind == ChunkKind.Outline && c.Text.Contains("Maintains", StringComparison.Ordinal)),
            "the doc summary is in the P0 chunk");

        var docChunks = assembly.Chunks.Where(c => c.Kind == ChunkKind.DocComment).ToArray();
        Assert.IsEmpty(
            docChunks,
            "the /// block at line 3 belongs to the symbol whose P0 chunk already carries it — indexing it twice would inflate the index");
    }

    [TestMethod]
    public async Task Comment_Block_Is_Indexed_As_DocComment_When_The_Symbol_Has_No_Documentation()
    {
        // Same file, but the adapter could not extract documentation -> the raw comment block is the only
        // place the text exists, so it must be indexed as P1.
        var outline = new StubOutlineSource(
            new OutlineSymbol("Worker", OutlineSymbolKind.Type, 4, 12, " : IWorker", "public sealed"));
        var assembler = new FileChunkAssembler(outline);

        var assembly = await assembler.AssembleAsync(CsFile, Lines(FixtureLines()), SourceLanguage.CSharp);

        var docChunks = assembly.Chunks.Where(c => c.Kind == ChunkKind.DocComment).ToArray();
        Assert.HasCount(1, docChunks);
        Assert.AreEqual(3, docChunks[0].StartLine);
        Assert.AreEqual(3, docChunks[0].EndLine);
        StringAssert.Contains(docChunks[0].Text, "Maintains");
    }

    [TestMethod]
    public async Task Documentation_Can_Be_Left_Out_Of_The_Outline_Chunk()
    {
        var assembler = Assembler(out _, new ChunkingOptions { IncludeDocumentationInOutlineChunk = false });
        var assembly = await assembler.AssembleAsync(CsFile, Lines(FixtureLines()), SourceLanguage.CSharp);

        var typeChunk = assembly.Chunks.Single(c => c.Kind == ChunkKind.Outline && c.Text.Contains("IWorker", StringComparison.Ordinal));
        Assert.IsFalse(typeChunk.Text.Contains("Maintains", StringComparison.Ordinal));

        Assert.AreEqual(
            1,
            assembly.Chunks.Count(c => c.Kind == ChunkKind.DocComment),
            "with the summary out of P0 the /// block becomes the only carrier of that text");
    }

    [TestMethod]
    public async Task Tiers_Can_Be_Disabled_Individually()
    {
        var noCode = Assembler(out _, new ChunkingOptions { EmitCodeTextChunks = false });
        var noCodeAssembly = await noCode.AssembleAsync(CsFile, Lines(FixtureLines()), SourceLanguage.CSharp);
        Assert.IsEmpty(noCodeAssembly.Chunks.Where(c => c.Kind == ChunkKind.CodeText));
        Assert.IsGreaterThan(0, noCodeAssembly.CountOf(ChunkKind.Outline), "the P0 tier survives, which is the point of disabling P2");

        var noDocs = Assembler(out _, new ChunkingOptions { EmitDocCommentChunks = false });
        var noDocsAssembly = await noDocs.AssembleAsync(CsFile, Lines(FixtureLines()), SourceLanguage.CSharp);
        Assert.IsEmpty(noDocsAssembly.Chunks.Where(c => c.Kind == ChunkKind.DocComment));

        var noOutline = Assembler(out var outlineUsed, new ChunkingOptions { EmitOutlineChunks = false });
        var noOutlineAssembly = await noOutline.AssembleAsync(CsFile, Lines(FixtureLines()), SourceLanguage.CSharp);
        Assert.IsEmpty(noOutlineAssembly.Chunks.Where(c => c.Kind == ChunkKind.Outline));
        Assert.AreEqual(0, outlineUsed.CallCount, "with the P0 tier off the outline port must not be consulted at all");
        Assert.IsGreaterThan(0, noOutlineAssembly.CountOf(ChunkKind.CodeText), "without P0 the declaration lines fall back into the P2 tier");
    }

    [TestMethod]
    public async Task Empty_File_Yields_No_Chunks_And_Does_Not_Consult_The_Outline_Port()
    {
        var assembler = Assembler(out var outline);

        foreach (var text in new string?[] { null, string.Empty })
        {
            var assembly = await assembler.AssembleAsync(CsFile, text, SourceLanguage.CSharp);
            Assert.AreEqual(0, assembly.LineCount, "no text means no lines");
            Assert.IsEmpty(assembly.Chunks);
        }

        Assert.AreEqual(0, outline.CallCount, "a file with no lines has nothing to outline");

        // A whitespace-only file does have one (blank) line; it still yields no chunk because a blank line
        // carries no token. Separate assembler, so the call-count assertion above stays meaningful.
        var blankAssembler = new FileChunkAssembler(StubOutlineSource.WithNoSymbols());
        var blankAssembly = await blankAssembler.AssembleAsync(CsFile, "   ", SourceLanguage.CSharp);
        Assert.AreEqual(1, blankAssembly.LineCount);
        Assert.IsEmpty(blankAssembly.Chunks);
    }

    [TestMethod]
    public async Task Comments_Only_File_Yields_DocComment_Chunks_Only()
    {
        var outline = StubOutlineSource.WithNoSymbols();
        var assembler = new FileChunkAssembler(outline);

        var assembly = await assembler.AssembleAsync(
            CsFile,
            Lines("// first note", "// second note", "", "/// <summary>Doc</summary>", ""),
            SourceLanguage.CSharp);

        Assert.AreEqual(2, assembly.CountOf(ChunkKind.DocComment));
        Assert.AreEqual(0, assembly.CountOf(ChunkKind.CodeText));
        Assert.AreEqual(0, assembly.CountOf(ChunkKind.Outline));

        var first = assembly.Chunks.First();
        Assert.AreEqual(1, first.StartLine);
        Assert.AreEqual(2, first.EndLine);
        StringAssert.Contains(first.Text, "first");
        StringAssert.Contains(first.Text, "second");

        Assert.IsTrue(assembly.Chunks.All(c => c.Priority == ChunkPriorities.P1));
    }

    [TestMethod]
    public async Task Non_Code_Language_Does_Not_Consult_The_Outline_Port()
    {
        var outline = new StubOutlineSource(FixtureSymbols());
        var assembler = new FileChunkAssembler(outline);

        var assembly = await assembler.AssembleAsync("docs/readme.md", Lines("# Title", "", "Body text", "more body", "", "- item"), SourceLanguage.Markdown);

        Assert.AreEqual(0, outline.CallCount, "prose has no outline; consulting the port would be wasted work");
        Assert.AreEqual(3, assembly.CountOf(ChunkKind.DocComment), "one chunk per non-blank block");
        Assert.AreEqual(0, assembly.CountOf(ChunkKind.CodeText), "prose has no code-text tier");
        Assert.AreEqual(0, assembly.CountOf(ChunkKind.Outline));
        CollectionAssert.AreEqual(new[] { 1, 3, 6 }, assembly.Chunks.Select(c => c.StartLine).ToArray());
    }

    [TestMethod]
    public async Task Prose_Blocks_Are_Split_At_The_Configured_Cap()
    {
        var assembler = new FileChunkAssembler(
            StubOutlineSource.WithNoSymbols(),
            new ChunkingOptions { MaxCodeLinesPerChunk = 2 });

        var assembly = await assembler.AssembleAsync(
            "docs/readme.md",
            Lines("alpha one", "beta two", "gamma three", "delta four", "epsilon five"),
            SourceLanguage.Markdown);

        CollectionAssert.AreEqual(new[] { (1, 2), (3, 4), (5, 5) }, assembly.Chunks.Select(c => (c.StartLine, c.EndLine)).ToArray());
    }

    [TestMethod]
    public async Task Long_Code_Runs_Are_Split_At_The_Configured_Cap()
    {
        var assembler = new FileChunkAssembler(
            StubOutlineSource.WithNoSymbols(),
            new ChunkingOptions { MaxCodeLinesPerChunk = 2 });

        var assembly = await assembler.AssembleAsync(
            CsFile,
            Lines("alpha one", "beta two", "gamma three", "delta four", "epsilon five"),
            SourceLanguage.CSharp);

        CollectionAssert.AreEqual(new[] { (1, 2), (3, 4), (5, 5) }, assembly.Chunks.Select(c => (c.StartLine, c.EndLine)).ToArray());
        CollectionAssert.AreEqual(
            new[] { "alpha one beta two", "gamma three delta four", "epsilon five" },
            assembly.Chunks.Select(c => c.Text).ToArray(),
            "each chunk carries the lines of its own range, not one line");
    }

    [TestMethod]
    public async Task Out_Of_Range_Symbol_Lines_Are_Clamped()
    {
        var outline = new StubOutlineSource(
            new OutlineSymbol("TooFar", OutlineSymbolKind.Type, 999, 5000),
            new OutlineSymbol("Before", OutlineSymbolKind.Method, -4, -1));
        var assembler = new FileChunkAssembler(outline);

        var assembly = await assembler.AssembleAsync(CsFile, Lines("alpha one", "beta two", "gamma three"), SourceLanguage.CSharp);

        Assert.AreEqual(2, assembly.CountOf(ChunkKind.Outline));
        Assert.IsTrue(assembly.Chunks.All(c => c.StartLine >= 1 && c.EndLine <= 3), "every chunk must stay inside the file");
        Assert.AreEqual(3, assembly.Chunks.Single(c => c.Text.Contains("TooFar", StringComparison.Ordinal)).StartLine);
        Assert.AreEqual(1, assembly.Chunks.Single(c => c.Text.Contains("Before", StringComparison.Ordinal)).EndLine);
    }

    [TestMethod]
    public async Task Line_Coverage_Is_Partitioned_Within_Each_Tier()
    {
        var assembler = Assembler(out _);
        var assembly = await assembler.AssembleAsync(CsFile, Lines(FixtureLines()), SourceLanguage.CSharp);

        var lines = FixtureLines();

        // P0 ranges nest on purpose: a type's range contains its members' ranges, because the range says
        // "this symbol lives here" while the chunk text is only the declaration. Asserted, so the nesting
        // is a decided behaviour rather than an accident.
        var typeChunk = assembly.Chunks.Single(c => c.Kind == ChunkKind.Outline && c.Text.Contains("IWorker", StringComparison.Ordinal));
        var fieldChunk = assembly.Chunks.Single(c => c.Kind == ChunkKind.Outline && c.Text.Contains("_attempts", StringComparison.Ordinal));
        Assert.IsGreaterThan(typeChunk.StartLine, fieldChunk.StartLine, "the field starts after the type declaration begins");
        Assert.IsGreaterThan(fieldChunk.EndLine, typeChunk.EndLine, "and ends before the type does");

        // Within a tier nothing may overlap: that is what "a line is indexed once" means for the tiers
        // whose chunks store line text verbatim (P1 comments, P2 code text).
        foreach (var kind in new[] { ChunkKind.DocComment, ChunkKind.CodeText })
        {
            var covered = new List<int>();
            foreach (var chunk in assembly.Chunks.Where(c => c.Kind == kind))
                for (var line = chunk.StartLine; line <= chunk.EndLine; line++)
                    covered.Add(line);

            CollectionAssert.AreEqual(
                covered.Distinct().OrderBy(line => line).ToArray(),
                covered.OrderBy(line => line).ToArray(),
                $"{kind}: two chunks of the same tier must not cover the same line");
        }

        // Code text must never re-emit a line that an outline declaration already consumed.
        var declarationLines = assembly.Chunks
            .Where(c => c.Kind == ChunkKind.Outline)
            .Select(c => c.StartLine)
            .ToHashSet();

        Assert.IsEmpty(
            assembly.Chunks.Where(c => c.Kind == ChunkKind.CodeText)
                .SelectMany(c => Enumerable.Range(c.StartLine, c.EndLine - c.StartLine + 1))
                .Where(declarationLines.Contains),
            "a declaration line must not also be indexed as code text");

        // …and the other direction: a line carrying at least one token must not be silently dropped.
        var coverage = new int[assembly.LineCount + 1];
        foreach (var chunk in assembly.Chunks)
            for (var line = chunk.StartLine; line <= chunk.EndLine; line++)
                coverage[line]++;

        var uncovered = Enumerable.Range(1, lines.Length)
            .Where(i => ChunkTokenizer.HasTokens(lines[i - 1]) && coverage[i] == 0)
            .ToArray();

        // The only legitimate way for a token-bearing line to be missing from every range is a doc block
        // whose text the P0 chunk of the symbol directly below already carries. Asserted explicitly, so a
        // future suppression bug cannot hide behind the word "legitimate".
        var outlineStartLines = assembly.Chunks
            .Where(c => c.Kind == ChunkKind.Outline)
            .Select(c => c.StartLine)
            .ToHashSet();

        foreach (var line in uncovered)
        {
            Assert.IsTrue(
                lines[line - 1].TrimStart().StartsWith("//", StringComparison.Ordinal),
                $"line {line} carries tokens but reaches no chunk and is not a comment");
            Assert.Contains(
                line + 1,
                outlineStartLines,
                $"the suppressed doc block at line {line} must belong to the outline chunk starting on line {line + 1}");
        }

        Assert.HasCount(1, uncovered, "exactly the documented doc block is suppressed in this fixture");
    }

    [TestMethod]
    public async Task Code_Text_Is_Filtered_So_Keywords_And_Short_Tokens_Never_Reach_The_Index()
    {
        var filtered = new FileChunkAssembler(StubOutlineSource.WithNoSymbols());
        var filteredAssembly = await filtered.AssembleAsync(CsFile, Lines("int i = 0;"), SourceLanguage.CSharp);
        Assert.IsEmpty(
            filteredAssembly.Chunks,
            "every token of 'int i = 0;' is a keyword or too short, so the chunk carries nothing indexable");

        var unfiltered = new FileChunkAssembler(
            StubOutlineSource.WithNoSymbols(),
            new ChunkingOptions { Filter = new ChunkFilterOptions { RemoveShortTokens = false, RemoveStopWords = false } });
        var unfilteredAssembly = await unfiltered.AssembleAsync(CsFile, Lines("int i = 0;"), SourceLanguage.CSharp);

        Assert.HasCount(1, unfilteredAssembly.Chunks);
        Assert.AreEqual("int i 0", unfilteredAssembly.Chunks[0].Text, "the same text with the rules off keeps every token");
    }

    [TestMethod]
    public void Chunk_Priorities_Map_To_P0_P1_P2_And_Boosts_Are_Monotonic()
    {
        // Derived at runtime (not folded away as a constant comparison), so this really fails if the
        // mapping or the enum order drifts.
        var kinds = Enum.GetValues<ChunkKind>();
        CollectionAssert.AreEqual(
            new[] { ChunkPriorities.P0, ChunkPriorities.P1, ChunkPriorities.P2 },
            kinds.Select(ChunkPriorities.Of).ToArray(),
            "the declaration order of ChunkKind is the priority order");
        CollectionAssert.AreEqual(
            kinds.Cast<int>().ToArray(),
            kinds.Select(ChunkPriorities.Of).ToArray(),
            "each kind's enum value equals its priority, so (int)ChunkKind.CodeText == P2");

        Assert.IsGreaterThan(ChunkPriorities.BoostOf(ChunkKind.DocComment), ChunkPriorities.BoostOf(ChunkKind.Outline), "P0 outweighs P1");
        Assert.IsGreaterThan(ChunkPriorities.BoostOf(ChunkKind.CodeText), ChunkPriorities.BoostOf(ChunkKind.DocComment), "P1 outweighs P2");

        Assert.Throws<ArgumentOutOfRangeException>(() => ChunkPriorities.Of((ChunkKind)42));
        Assert.Throws<ArgumentOutOfRangeException>(() => ChunkPriorities.BoostOf((ChunkKind)42));
    }

    [TestMethod]
    public void Invalid_Chunks_And_Options_Are_Rejected()
    {
        Assert.Throws<ArgumentException>(() => new IndexChunk(ChunkKind.Outline, "   ", CsFile, 1, 1));
        Assert.Throws<ArgumentException>(() => new IndexChunk(ChunkKind.Outline, "x", "  ", 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new IndexChunk(ChunkKind.Outline, "x", CsFile, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new IndexChunk(ChunkKind.Outline, "x", CsFile, 3, 2));

        Assert.Throws<ArgumentNullException>(() => new FileChunkAssembler(null!));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new FileChunkAssembler(StubOutlineSource.WithNoSymbols(), new ChunkingOptions { MaxCodeLinesPerChunk = 0 }));
    }

    [TestMethod]
    public async Task Chunk_Provenance_Is_Complete()
    {
        var assembler = Assembler(out _);
        var assembly = await assembler.AssembleAsync(CsFile, Lines(FixtureLines()), SourceLanguage.CSharp);

        Assert.AreEqual(CsFile, assembly.FilePath);
        Assert.AreEqual(SourceLanguage.CSharp, assembly.Language);
        Assert.IsGreaterThan(0, assembly.TotalTextChars);
        Assert.IsTrue(assembly.Chunks.All(c => string.Equals(c.SourceFile, CsFile, StringComparison.Ordinal)));
        Assert.IsTrue(assembly.Chunks.All(c => c.LineCount == c.EndLine - c.StartLine + 1));
    }

    [TestMethod]
    public async Task Symbols_With_Unsupported_Kinds_Are_Skipped()
    {
        var outline = new StubOutlineSource(
            new OutlineSymbol("Demo", OutlineSymbolKind.Namespace, 1, 12),
            new OutlineSymbol("", OutlineSymbolKind.Method, 8, 11),
            new OutlineSymbol("Pump", OutlineSymbolKind.Method, 8, 11, "(int rounds)", "public", "Worker"));
        var assembler = new FileChunkAssembler(outline);

        var assembly = await assembler.AssembleAsync(CsFile, Lines(FixtureLines()), SourceLanguage.CSharp);

        Assert.AreEqual(3, assembly.OutlineSymbolCount, "the report still counts what the adapter returned");
        Assert.AreEqual(1, assembly.CountOf(ChunkKind.Outline), "a namespace and a nameless symbol produce no chunk");
        StringAssert.Contains(assembly.Chunks.Single(c => c.Kind == ChunkKind.Outline).Text, "Pump");
    }
}
