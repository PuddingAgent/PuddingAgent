using System.Text;
using System.Text.Json;
using PuddingCode.Tools;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Tools;

/// <summary>
/// Regression tests for three field-measured file_patch defects:
/// D1 — camelCase parameter aliases (oldText/newText) were silently dropped (snake_case-only
///      [JsonPropertyName]) and surfaced as misleading "requires 'old_text'" errors;
/// D2 — whitespace-tolerant matching started the match span at the first content character, so
///      the old line's indentation survived the replacement and stacked with the indentation
///      inside new_text (+4/+8/+12 drift);
/// D3 — the string-replace path spliced new_text verbatim, writing LF snippets into CRLF files
///      and producing mixed line endings.
/// </summary>
[TestClass]
public sealed class FilePatchToolTests
{
    private string _tempDir = null!;
    private string _originalRepoRoot = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"pudding-fpt-reg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _originalRepoRoot = Environment.GetEnvironmentVariable("PUDDING_REPOSITORY_ROOT") ?? string.Empty;
        Environment.SetEnvironmentVariable("PUDDING_REPOSITORY_ROOT", _tempDir);
        HostFileToolPaths.InvalidateWorkspaceRootCache();
    }

    [TestCleanup]
    public void Cleanup()
    {
        Environment.SetEnvironmentVariable("PUDDING_REPOSITORY_ROOT", _originalRepoRoot);
        HostFileToolPaths.InvalidateWorkspaceRootCache();
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    // ── D1: parameter-name tolerance ──

    [TestMethod]
    public void Deserialize_Operation_AcceptsCamelCaseAndSnakeCaseAliases()
    {
        const string json = """{"type":"replace","oldText":"a","new_text":"b","replace_all":true,"startLine":2,"end_line":3}""";
        var op = JsonSerializer.Deserialize<FilePatchOperation>(json);

        Assert.IsNotNull(op);
        Assert.AreEqual("replace", op.Type);
        Assert.AreEqual("a", op.OldText, "camelCase oldText must bind");
        Assert.AreEqual("b", op.NewText, "snake_case new_text must still bind");
        Assert.IsTrue(op.ReplaceAll!.Value);
        Assert.AreEqual(2, op.StartLine!.Value, "camelCase startLine must bind");
        Assert.AreEqual(3, op.EndLine!.Value, "snake_case end_line must still bind");
    }

    [TestMethod]
    public void Deserialize_Operation_UnknownParameterStillRejected()
    {
        Assert.ThrowsExactly<JsonException>(() =>
            JsonSerializer.Deserialize<FilePatchOperation>("""{"type":"replace","oldText":"a","oops":1}"""));
    }

    [TestMethod]
    public async Task Replace_WithCamelCaseFieldNames_AppliesChanges()
    {
        WriteFile("sample.txt", "hello old world\n");
        var result = await ExecuteAsync(new Dictionary<string, object?>
        {
            ["path"] = "sample.txt",
            ["operations"] = new object[]
            {
                new Dictionary<string, object?> { ["type"] = "replace", ["oldText"] = "old", ["newText"] = "new" }
            }
        });

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual("hello new world\n", ReadFile("sample.txt"));
    }

    [TestMethod]
    public async Task Replace_MissingOldTextField_ErrorMentionsBothAcceptedSpellings()
    {
        WriteFile("sample.txt", "keep\n");
        var result = await ExecuteAsync(new Dictionary<string, object?>
        {
            ["path"] = "sample.txt",
            ["operations"] = new object[]
            {
                new Dictionary<string, object?> { ["type"] = "replace", ["newText"] = "x" }
            }
        });

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "oldText", "error must point at the camelCase alias too");
    }

    // ── D2: whitespace-tolerant matches must not stack indentation ──

    [TestMethod]
    public async Task Replace_WhitespaceTolerantMatch_DoesNotStackIndentation()
    {
        WriteFile("code.cs", "class A\n{\n        void M()\n        {\n        }\n}\n");
        var result = await ExecuteAsync(new Dictionary<string, object?>
        {
            ["path"] = "code.cs",
            ["operations"] = new object[]
            {
                // old_text carries 12-space indent while the file line has 8 → exact match is
                // impossible (12-space run does not exist), forcing the whitespace-tolerant path;
                // the whole 8-space indent run must be part of the replaced span.
                new Dictionary<string, object?> { ["type"] = "replace", ["old_text"] = "            void M()", ["new_text"] = "    void M(int x)" }
            }
        });

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual(
            "class A\n{\n    void M(int x)\n        {\n        }\n}\n",
            ReadFile("code.cs"),
            "replacement line must carry exactly the new_text indentation (no +8 stacking)");
    }

    [TestMethod]
    public async Task Replace_MultilineNewText_KeepsAuthoredIndentation()
    {
        WriteFile("code.txt", "  alpha();\n  beta();\n");
        var result = await ExecuteAsync(new Dictionary<string, object?>
        {
            ["path"] = "code.txt",
            ["operations"] = new object[]
            {
                // old_text indent (4) wider than the file indent (2) → whitespace-tolerant path.
                new Dictionary<string, object?> { ["type"] = "replace", ["old_text"] = "    alpha();", ["new_text"] = "    gamma();\n    delta();" }
            }
        });

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual(
            "    gamma();\n    delta();\n  beta();\n",
            ReadFile("code.txt"),
            "first line must use new_text indentation only; continuation lines stay verbatim");
    }

    [TestMethod]
    public async Task Replace_ExactMidLineMatch_UnaffectedByIndentExpansion()
    {
        WriteFile("code.txt", "x = foo();  // keep\n");
        var result = await ExecuteAsync(new Dictionary<string, object?>
        {
            ["path"] = "code.txt",
            ["operations"] = new object[]
            {
                new Dictionary<string, object?> { ["type"] = "replace", ["old_text"] = "foo();", ["new_text"] = "bar();" }
            }
        });

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual("x = bar();  // keep\n", ReadFile("code.txt"));
    }

    // ── D3: replace path must honor the file's dominant line endings ──

    [TestMethod]
    public async Task Replace_InCrlfFile_NewTextLfBreaksNormalizedToCrlf()
    {
        WriteFile("crlf.txt", "line1\r\nold\r\nline3\r\n");
        var result = await ExecuteAsync(new Dictionary<string, object?>
        {
            ["path"] = "crlf.txt",
            ["operations"] = new object[]
            {
                new Dictionary<string, object?> { ["type"] = "replace", ["old_text"] = "old", ["new_text"] = "new1\nnew2" }
            }
        });

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual(
            "line1\r\nnew1\r\nnew2\r\nline3\r\n",
            ReadFile("crlf.txt"),
            "LF breaks inside new_text must be converted to the file's CRLF endings");
    }

    [TestMethod]
    public async Task Replace_InLfFile_NewTextCrlfBreaksNormalizedToLf()
    {
        WriteFile("lf.txt", "a\nold\nb\n");
        var result = await ExecuteAsync(new Dictionary<string, object?>
        {
            ["path"] = "lf.txt",
            ["operations"] = new object[]
            {
                new Dictionary<string, object?> { ["type"] = "replace", ["old_text"] = "old", ["new_text"] = "x\r\ny" }
            }
        });

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual(
            "a\nx\ny\nb\n",
            ReadFile("lf.txt"),
            "CRLF breaks inside new_text must be converted to the file's LF endings");
    }

    // ── Converter regression lock: key normalization & fail-closed contract (task f1d45a15) ──

    [TestMethod]
    public void Deserialize_Operation_PureCamelCaseOldAndNewText_BindToProperties()
    {
        const string json = """{"type":"replace","oldText":"A","newText":"B"}""";
        var op = JsonSerializer.Deserialize<FilePatchOperation>(json);

        Assert.IsNotNull(op);
        Assert.AreEqual("A", op.OldText, "camelCase oldText must bind to OldText");
        Assert.AreEqual("B", op.NewText, "camelCase newText must bind to NewText");
    }

    [TestMethod]
    public void Deserialize_Operation_PureSnakeCaseOldAndNewText_BindToProperties()
    {
        const string json = """{"type":"replace","old_text":"A","new_text":"B"}""";
        var op = JsonSerializer.Deserialize<FilePatchOperation>(json);

        Assert.IsNotNull(op);
        Assert.AreEqual("A", op.OldText, "snake_case old_text must bind to OldText");
        Assert.AreEqual("B", op.NewText, "snake_case new_text must bind to NewText");
    }

    [TestMethod]
    public void Deserialize_Operation_NearMissUnknownKey_IsFailClosedRejected()
    {
        // "oldTextt" normalizes to "oldtextt" which matches no supported key —
        // the converter must throw instead of silently dropping the value.
        var nearMiss = Assert.ThrowsExactly<JsonException>(() =>
            JsonSerializer.Deserialize<FilePatchOperation>("""{"type":"replace","oldTextt":"A"}"""));
        Assert.IsTrue(
            nearMiss.Message.Contains("oldTextt", StringComparison.Ordinal),
            $"rejection must name the offending key, got: {nearMiss.Message}");

        Assert.ThrowsExactly<JsonException>(() =>
            JsonSerializer.Deserialize<FilePatchOperation>("""{"type":"replace","foo":1}"""));
    }

    // ── Helpers ──

    // Parameterless constructors resolve data paths from HostFileToolPaths.WorkspaceRoot,
    // which the test redirects to a temp dir via PUDDING_REPOSITORY_ROOT.
    private string GetPath(string name) => Path.Combine(_tempDir, name);

    private void WriteFile(string name, string content) =>
        File.WriteAllText(GetPath(name), content, Encoding.UTF8);

    private string ReadFile(string name) =>
        File.ReadAllText(GetPath(name), Encoding.UTF8);

    // ── D4: a line break is never split in half; an omitted leading break is an explicit deletion ──

    [TestMethod]
    public async Task Replace_LeadingNewlineAnchoredOldText_NeverSplitsCrlfAndDeletesBreakWhenOmitted()
    {
        // Field-measured on 2026-10-07: anchoring old_text with a leading newline used to match only the
        // LF half of the CRLF before the row, leaving an orphan CR behind. The span now always covers the
        // whole CRLF pair, and the parameters are executed faithfully: new_text supplies no leading break,
        // so the break is deleted (the two lines are joined) instead of being silently restored.
        WriteFile("table.md", "| a | b |\r\n|---|---|\r\n| keep | one |\r\n| target | two |\r\n| tail | three |\r\n");
        var result = await ExecuteAsync(new Dictionary<string, object?>
        {
            ["path"] = "table.md",
            ["operations"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "replace",
                    ["old_text"] = "\n| target | two |",
                    ["new_text"] = "| target | two |\n| ins1 | x |",
                }
            }
        });

        Assert.IsTrue(result.Success, result.Error);
        var actual = ReadFile("table.md");
        Assert.AreEqual(
            "| a | b |\r\n|---|---|\r\n| keep | one || target | two |\r\n| ins1 | x |\r\n| tail | three |\r\n",
            actual,
            "the whole CRLF is consumed and new_text is written verbatim: an omitted leading break deletes it");
        Assert.IsFalse(actual.Contains("|\r|", StringComparison.Ordinal), "no orphan CR may survive");
        Assert.IsFalse(actual.Contains("\r\r\n", StringComparison.Ordinal), "no CRCRLF may be produced");
        StringAssert.Contains(
            result.Output,
            "starts with a line break",
            "the deleted line boundary must be reported to the caller");
    }

    [TestMethod]
    public async Task Replace_LeadingNewlineAnchoredOldText_ConsumesBreakWhenNewTextRestoresIt()
    {
        WriteFile("table.md", "| keep | one |\r\n| target | two |\r\n| tail | three |\r\n");
        var result = await ExecuteAsync(new Dictionary<string, object?>
        {
            ["path"] = "table.md",
            ["operations"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "replace",
                    ["old_text"] = "\n| target | two |",
                    ["new_text"] = "\n| target | two |\n| ins1 | x |",
                }
            }
        });

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual(
            "| keep | one |\r\n| target | two |\r\n| ins1 | x |\r\n| tail | three |\r\n",
            ReadFile("table.md"),
            "a symmetric replacement may consume the preceding break because it supplies it back");
    }

    [TestMethod]
    public async Task Replace_LeadingNewlineAnchoredOldText_ReportedDeletionMatchesWrittenBytes()
    {
        // The reporting contract of the case above: the caller is told the boundary was deleted, and the
        // notice never claims a preservation that did not happen.
        WriteFile("table.md", "| keep | one |\r\n| target | two |\r\n");
        var result = await ExecuteAsync(new Dictionary<string, object?>
        {
            ["path"] = "table.md",
            ["operations"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "replace",
                    ["old_text"] = "\n| target | two |",
                    ["new_text"] = "| target | three |",
                }
            }
        });

        Assert.IsTrue(result.Success, result.Error);
        var actual = ReadFile("table.md");
        Assert.AreEqual(
            "| keep | one || target | three |\r\n",
            actual,
            "new_text supplies no leading break, so the break is deleted exactly as requested");
        Assert.IsFalse(actual.Contains("\r\r\n", StringComparison.Ordinal), "no CRCRLF may be produced");
        StringAssert.Contains(
            result.Output,
            "starts with a line break",
            "the caller must be told that an asymmetric leading line break deleted the boundary");
    }

    // ── D5: a tolerant match may not guess a line boundary (P0-2, fail-closed) ──

    [TestMethod]
    public async Task Replace_TolerantMatchWithAsymmetricLeadingBreak_RefusedWithoutWriting()
    {
        // The file indents with 4 spaces while old_text carries 8, so only the whitespace-tolerant
        // strategy can match. old_text anchors with a leading line break that new_text does not supply:
        // the tolerant span is a guess about that boundary, so nothing may be written at all.
        const string before = "a\r\n    target();\r\nb\r\n";
        WriteFile("code.cs", before);
        var result = await ExecuteAsync(new Dictionary<string, object?>
        {
            ["path"] = "code.cs",
            ["operations"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "replace",
                    ["old_text"] = "\n        target();",
                    ["new_text"] = "    replacement();",
                }
            }
        });

        Assert.IsFalse(result.Success, "an ambiguous tolerant boundary must fail closed");
        StringAssert.Contains(result.Error, "ambiguous_boundary_change");
        Assert.AreEqual(before, ReadFile("code.cs"), "a refused patch must leave the file byte-identical");
    }

    [TestMethod]
    public async Task Replace_TolerantMatchCollapsingALineBreak_RefusedWithoutWriting()
    {
        // old_text joins two statements with spaces while the file separates them with a CRLF; the
        // tolerant normalization would silently reflow two lines into one, so the write is refused.
        const string before = "alpha();\r\n    beta();\r\n";
        WriteFile("code.txt", before);
        var result = await ExecuteAsync(new Dictionary<string, object?>
        {
            ["path"] = "code.txt",
            ["operations"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "replace",
                    ["old_text"] = "alpha();  beta();",
                    ["new_text"] = "alpha(); beta();",
                }
            }
        });

        Assert.IsFalse(result.Success, "a tolerant match that crosses a line boundary must fail closed");
        StringAssert.Contains(result.Error, "ambiguous_boundary_change");
        Assert.AreEqual(before, ReadFile("code.txt"), "a refused patch must leave the file byte-identical");
    }

    // ── D6: line endings are not content — an LF-authored snippet must match CRLF text (P0-1) ──

    [TestMethod]
    public async Task Replace_LfAuthoredSnippet_MatchesCrlfFileAsEolEquivalent()
    {
        // The field incident of 2026-10-07 was authored exactly this way: old_text/new_text written with
        // LF on a CRLF file. A single-line snippet still finds a literal match on the LF half of a CRLF,
        // but a snippet spanning two rows does not — its break is preceded by a real character, so the
        // literal search fails. Only the line endings differ, so the match must be exact modulo EOL; it
        // must neither fall into the whitespace-tolerant strategy (which also ignores indentation and
        // guesses the span, and refuses this shape as an ambiguous boundary) nor be refused at all.
        WriteFile("table.md", "| a | b |\r\n| keep | one |\r\n| target | two |\r\n| tail | three |\r\n");
        var result = await ExecuteAsync(new Dictionary<string, object?>
        {
            ["path"] = "table.md",
            ["operations"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "replace",
                    ["old_text"] = "\n| target | two |\n| tail | three |",
                    ["new_text"] = "\n| target | three |\n| tail | four |",
                }
            }
        });

        Assert.IsTrue(result.Success, result.Error);
        var actual = ReadFile("table.md");
        Assert.AreEqual(
            "| a | b |\r\n| keep | one |\r\n| target | three |\r\n| tail | four |\r\n",
            actual,
            "the two-row LF snippet replaces the CRLF rows in place: whole pairs consumed, no orphan CR");
        Assert.IsFalse(actual.Contains("|\r|", StringComparison.Ordinal), "no orphan CR may survive");
        Assert.IsFalse(actual.Contains("\r\r\n", StringComparison.Ordinal), "no CRCRLF may be produced");
        StringAssert.Contains(result.Output, "eol-equivalent", "an EOL-only match must be reported as such");
        Assert.IsFalse(
            result.Output.Contains("whitespace-tolerant", StringComparison.Ordinal),
            "the tolerant strategy must not be reached when only line endings differ");
    }

    [TestMethod]
    public async Task Replace_CrlfAuthoredSnippet_MatchesLfFileAsEolEquivalent()
    {
        const string before = "alpha\nbeta\ngamma\n";
        WriteFile("notes.txt", before);
        var result = await ExecuteAsync(new Dictionary<string, object?>
        {
            ["path"] = "notes.txt",
            ["operations"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "replace",
                    ["old_text"] = "beta\r\ngamma",
                    ["new_text"] = "beta\r\ndelta",
                }
            }
        });

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual("alpha\nbeta\ndelta\n", ReadFile("notes.txt"), "the LF file keeps its own endings");
        StringAssert.Contains(result.Output, "eol-equivalent", "an EOL-only match must be reported as such");
    }

    // ── D7: the preview diff must align lines instead of comparing them by index (P1) ──

    [TestMethod]
    public async Task Diff_SingleLineInsertion_IsReportedAsOneAddedLine()
    {
        // The old renderer compared old[i] with new[i]; one inserted line shifted every following
        // line, so the preview claimed the whole tail had been replaced.
        WriteFile("list.txt", "alpha\r\nbeta\r\ngamma\r\ndelta\r\n");
        var result = await ExecuteAsync(new Dictionary<string, object?>
        {
            ["path"] = "list.txt",
            ["operations"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "replace",
                    ["old_text"] = "beta\r\n",
                    ["new_text"] = "beta\r\ninserted\r\n",
                }
            }
        });

        Assert.IsTrue(result.Success, result.Error);
        CollectionAssert.AreEqual(
            new[] { "+ inserted" },
            DiffLines(result.Output),
            "one inserted line is one added line; no deletion may be invented");
    }

    [TestMethod]
    public async Task Diff_SingleLineDeletion_IsReportedAsOneRemovedLine()
    {
        WriteFile("list.txt", "alpha\r\nbeta\r\ngamma\r\ndelta\r\n");
        var result = await ExecuteAsync(new Dictionary<string, object?>
        {
            ["path"] = "list.txt",
            ["operations"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "replace",
                    ["old_text"] = "gamma\r\n",
                    ["new_text"] = "",
                }
            }
        });

        Assert.IsTrue(result.Success, result.Error);
        CollectionAssert.AreEqual(
            new[] { "- gamma" },
            DiffLines(result.Output),
            "one deleted line is one removed line; no addition may be invented");
    }

    [TestMethod]
    public async Task Diff_MultiLineInsertion_ShowsEveryAddedLineWithoutDeletions()
    {
        WriteFile("list.txt", "alpha\r\nbeta\r\ngamma\r\n");
        var result = await ExecuteAsync(new Dictionary<string, object?>
        {
            ["path"] = "list.txt",
            ["operations"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "replace",
                    ["old_text"] = "beta\r\n",
                    ["new_text"] = "beta\r\none\r\ntwo\r\nthree\r\n",
                }
            }
        });

        Assert.IsTrue(result.Success, result.Error);
        CollectionAssert.AreEqual(
            new[] { "+ one", "+ two", "+ three" },
            DiffLines(result.Output),
            "three inserted lines are three added lines and nothing else");
    }

    [TestMethod]
    public async Task Diff_InPlaceReplacement_StillShowsOneRemovedAndOneAddedLine()
    {
        // Guard against over-correction: a genuine same-line edit must keep its -/+ pair.
        WriteFile("list.txt", "alpha\r\nbeta\r\ngamma\r\ndelta\r\n");
        var result = await ExecuteAsync(new Dictionary<string, object?>
        {
            ["path"] = "list.txt",
            ["operations"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "replace",
                    ["old_text"] = "gamma",
                    ["new_text"] = "GAMMA",
                }
            }
        });

        Assert.IsTrue(result.Success, result.Error);
        CollectionAssert.AreEqual(
            new[] { "- gamma", "+ GAMMA" },
            DiffLines(result.Output),
            "an in-place edit keeps exactly one removed and one added line");
    }

    /// <summary>Only the preview's own diff rows, so surrounding summary prose cannot mask a miss.</summary>
    private static string[] DiffLines(string? output)
    {
        var text = (output ?? string.Empty).Replace("\r\n", "\n");
        var lines = new List<string>();
        foreach (var line in text.Split('\n'))
        {
            if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("+ ", StringComparison.Ordinal))
                lines.Add(line);
        }
        return lines.ToArray();
    }

    // ── D8: scope line ranges must be measured on the original text (P1) ──

    [TestMethod]
    public async Task Replace_ScopeLineRangeOnCrlfFile_CountsTheCarriageReturnsInTheOffset()
    {
        // The scope offsets were computed on an EOL-normalized copy (CRLF folded to LF) while the match
        // offsets come from the original text; every preceding row shrank the window by one character, so
        // a match that carried its own line break fell outside the very scope it belonged to.
        WriteFile("scoped.txt", "alpha\r\ntarget\r\nbeta\r\n");
        var result = await ExecuteAsync(new Dictionary<string, object?>
        {
            ["path"] = "scoped.txt",
            ["scope_start_line"] = 2,
            ["scope_end_line"] = 2,
            ["operations"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "replace",
                    ["old_text"] = "target\r\n",
                    ["new_text"] = "TARGET\r\n",
                }
            }
        });

        Assert.AreEqual(
            "alpha\r\nTARGET\r\nbeta\r\n",
            ReadFile("scoped.txt"),
            "line 2 lies inside the declared scope, so the patch must be applied");
    }

    [TestMethod]
    public async Task Replace_ScopeLineRange_StillRejectsMatchesOutsideTheScope()
    {
        // Control for the fix above: correcting the offsets must not disable scoping altogether.
        const string before = "alpha\r\ntarget\r\nbeta\r\n";
        WriteFile("scoped.txt", before);
        var result = await ExecuteAsync(new Dictionary<string, object?>
        {
            ["path"] = "scoped.txt",
            ["scope_start_line"] = 3,
            ["scope_end_line"] = 3,
            ["operations"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "replace",
                    ["old_text"] = "target\r\n",
                    ["new_text"] = "TARGET\r\n",
                }
            }
        });

        Assert.AreEqual(before, ReadFile("scoped.txt"), "a match outside the scope must never be applied");
        StringAssert.Contains(result.Output, "not found", "the out-of-scope match must be reported as not found");
    }

    [TestMethod]
    public async Task Replace_ScopeLineRangeOnLfFile_KeepsWorking()
    {
        // LF files were never skewed by the normalization; they must stay unaffected by the fix.
        WriteFile("scoped-lf.txt", "alpha\ntarget\nbeta\n");
        var result = await ExecuteAsync(new Dictionary<string, object?>
        {
            ["path"] = "scoped-lf.txt",
            ["scope_start_line"] = 2,
            ["scope_end_line"] = 2,
            ["operations"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "replace",
                    ["old_text"] = "target\n",
                    ["new_text"] = "TARGET\n",
                }
            }
        });

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual("alpha\nTARGET\nbeta\n", ReadFile("scoped-lf.txt"));
    }

    // ── D9: preview-diff truncation semantics (SimpleLineDiff.MaxChangeGroups) ──

    [TestMethod]
    public async Task Preview_ExactlyTenChangeGroups_IsNotMarkedAsTruncated()
    {
        // The preview budget is 10 change groups; a diff with exactly ten must fit untouched,
        // otherwise the marker would lie about every ordinary patch.
        WriteFile("many.txt", NumberedLines(20));
        var result = await ExecuteAsync(new Dictionary<string, object?>
        {
            ["path"] = "many.txt",
            ["operations"] = NumberedReplacements(10),
        });

        Assert.IsTrue(result.Success, result.Error);
        Assert.IsFalse(
            result.Output.Contains("... (more changes)"),
            "ten groups fit the budget exactly, so no truncation marker may appear");
        StringAssert.Contains(result.Output, "ROW-10", "the tenth group must still be rendered");
    }

    [TestMethod]
    public async Task Preview_ElevenChangeGroups_IsTruncatedAfterTenWithMarker()
    {
        var before = NumberedLines(20);
        WriteFile("many.txt", before);
        var result = await ExecuteAsync(new Dictionary<string, object?>
        {
            ["path"] = "many.txt",
            ["operations"] = NumberedReplacements(11),
        });

        Assert.IsTrue(result.Success, result.Error);
        StringAssert.Contains(
            result.Output,
            "... (more changes)",
            "the eleventh group exceeds the budget and must be announced as truncated");
        Assert.IsFalse(
            result.Output.Contains("ROW-11"),
            "the truncated group's content must not leak into the preview");
        Assert.IsTrue(
            ReadFile("many.txt").Contains("ROW-11"),
            "truncation is preview-only: the eleventh change must still have been written");
    }

    private static string NumberedLines(int count)
    {
        var sb = new StringBuilder();
        for (var i = 1; i <= count; i++) sb.Append("row-").Append(i.ToString("00")).Append('\n');
        return sb.ToString();
    }

    private static object[] NumberedReplacements(int count)
    {
        var operations = new object[count];
        for (var i = 1; i <= count; i++)
        {
            operations[i - 1] = new Dictionary<string, object?>
            {
                ["type"] = "replace",
                ["old_text"] = "row-" + i.ToString("00"),
                ["new_text"] = "ROW-" + i.ToString("00"),
            };
        }
        return operations;
    }

    // ── D10: structured result status (file_search / search_grep alignment) ──

    [TestMethod]
    public async Task Patch_AppliedChange_ReportsStatusOk()
    {
        WriteFile("status.txt", "alpha\n");
        var result = await ExecuteAsync(new Dictionary<string, object?>
        {
            ["path"] = "status.txt",
            ["operations"] = new object[]
            {
                new Dictionary<string, object?> { ["type"] = "replace", ["old_text"] = "alpha", ["new_text"] = "beta" }
            }
        });

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual(ToolResultStatuses.Ok, result.Status, "a patch that really rewrote a file is a plain ok");
    }

    [TestMethod]
    public async Task Patch_MatchedNothing_ReportsNoMatchWithoutFailing()
    {
        const string before = "alpha\n";
        WriteFile("status.txt", before);
        var result = await ExecuteAsync(new Dictionary<string, object?>
        {
            ["path"] = "status.txt",
            ["operations"] = new object[]
            {
                new Dictionary<string, object?> { ["type"] = "replace", ["old_text"] = "nowhere", ["new_text"] = "x" }
            }
        });

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual(
            ToolResultStatuses.NoMatch,
            result.Status,
            "nothing was rewritten: that is a successful no_match, not a failure and not a plain ok");
        StringAssert.Contains(result.Output, "not found", "the reason must stay visible in the output");
        Assert.AreEqual(before, ReadFile("status.txt"));
    }

    [TestMethod]
    public async Task DryRun_PreviewWithChange_ReportsStatusOk()
    {
        WriteFile("status.txt", "alpha\n");
        var result = await ExecuteAsync(new Dictionary<string, object?>
        {
            ["path"] = "status.txt",
            ["dry_run"] = true,
            ["operations"] = new object[]
            {
                new Dictionary<string, object?> { ["type"] = "replace", ["old_text"] = "alpha", ["new_text"] = "beta" }
            }
        });

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual(ToolResultStatuses.Ok, result.Status, "a preview that would change the file is an ok result");
        Assert.AreEqual("alpha\n", ReadFile("status.txt"));
    }

    [TestMethod]
    public async Task DryRun_PreviewWithoutChange_ReportsStatusNoMatch()
    {
        WriteFile("status.txt", "alpha\n");
        var result = await ExecuteAsync(new Dictionary<string, object?>
        {
            ["path"] = "status.txt",
            ["dry_run"] = true,
            ["operations"] = new object[]
            {
                new Dictionary<string, object?> { ["type"] = "replace", ["old_text"] = "nowhere", ["new_text"] = "x" }
            }
        });

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual(ToolResultStatuses.NoMatch, result.Status, "an empty preview is a no_match in dry-run too");
    }

    [TestMethod]
    public async Task UnifiedDiff_AppliedPatch_ReportsStatusOk()
    {
        WriteFile("status.txt", "alpha\n");
        var result = await ExecuteAsync(new Dictionary<string, object?>
        {
            ["patch_text"] = "--- a/status.txt\n+++ b/status.txt\n@@ -1 +1 @@\n-alpha\n+beta\n"
        });

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual(ToolResultStatuses.Ok, result.Status, "the patch_text path shares the same status contract");
        Assert.AreEqual("beta\n", ReadFile("status.txt"));
    }

    // ── D11: argument-contract failures carry contract_error (runtime state does not) ──

    [TestMethod]
    public async Task ContractError_NoPatches_ReportsContractError()
    {
        var result = await ExecuteAsync(new Dictionary<string, object?>());

        Assert.IsFalse(result.Success, "an empty request must fail, not silently no-op");
        Assert.AreEqual(ToolResultStatuses.ContractError, result.Status);
    }

    [TestMethod]
    public async Task ContractError_UnknownOperationType_ReportsContractError()
    {
        WriteFile("status.txt", "alpha\n");
        var result = await ExecuteAsync(new Dictionary<string, object?>
        {
            ["path"] = "status.txt",
            ["operations"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "frobnicate",
                    ["old_text"] = "alpha",
                    ["new_text"] = "beta"
                }
            }
        });

        Assert.IsFalse(result.Success);
        Assert.AreEqual(
            ToolResultStatuses.ContractError,
            result.Status,
            "an unknown operation type is the caller's arguments, not a runtime failure");
        Assert.AreEqual("alpha\n", ReadFile("status.txt"));
    }

    [TestMethod]
    public async Task ContractError_ReplaceWithoutOldText_ReportsContractError()
    {
        WriteFile("status.txt", "alpha\n");
        var result = await ExecuteAsync(new Dictionary<string, object?>
        {
            ["path"] = "status.txt",
            ["operations"] = new object[]
            {
                new Dictionary<string, object?> { ["type"] = "replace", ["new_text"] = "beta" }
            }
        });

        Assert.IsFalse(result.Success);
        Assert.AreEqual(ToolResultStatuses.ContractError, result.Status, "omitting old_text is a malformed request");
        Assert.AreEqual("alpha\n", ReadFile("status.txt"));
    }

    [TestMethod]
    public async Task ContractError_RegexReplaceWithoutReplacement_ReportsContractError()
    {
        WriteFile("status.txt", "alpha\n");
        var result = await ExecuteAsync(new Dictionary<string, object?>
        {
            ["path"] = "status.txt",
            ["operations"] = new object[]
            {
                new Dictionary<string, object?> { ["type"] = "regexReplace", ["pattern"] = "alpha" }
            }
        });

        Assert.IsFalse(result.Success);
        Assert.AreEqual(
            ToolResultStatuses.ContractError,
            result.Status,
            "omitting replacement is a malformed request");
        Assert.AreEqual("alpha\n", ReadFile("status.txt"));
    }

    [TestMethod]
    public async Task ContractError_InvalidUnifiedDiff_ReportsContractError()
    {
        var result = await ExecuteAsync(new Dictionary<string, object?>
        {
            ["patch_text"] = "this is not a unified diff\n"
        });

        Assert.IsFalse(result.Success);
        Assert.AreEqual(
            ToolResultStatuses.ContractError,
            result.Status,
            "an unparsable patch_text is a malformed request");
    }

    [TestMethod]
    public async Task FileNotFound_IsNotReportedAsContractError()
    {
        var result = await ExecuteAsync(new Dictionary<string, object?>
        {
            ["path"] = "does-not-exist.txt",
            ["operations"] = new object[]
            {
                new Dictionary<string, object?> { ["type"] = "replace", ["old_text"] = "alpha", ["new_text"] = "beta" }
            }
        });

        Assert.IsFalse(result.Success);
        Assert.AreNotEqual(
            ToolResultStatuses.ContractError,
            result.Status,
            "a missing target is runtime state, not a malformed request");
    }

    private static Task<ToolExecutionResult> ExecuteAsync(IReadOnlyDictionary<string, object?> parameters)
    {
        var tool = new FilePatchTool();
        return tool.ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "call-1",
            ArgumentsJson = JsonSerializer.Serialize(parameters),
            Context = new ToolExecutionContext
            {
                AgentInstanceId = "agent",
                WorkspaceId = "workspace",
                SessionId = "session",
            },
        });
    }
}
