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

    // ── Helpers ──

    // Parameterless constructors resolve data paths from HostFileToolPaths.WorkspaceRoot,
    // which the test redirects to a temp dir via PUDDING_REPOSITORY_ROOT.
    private string GetPath(string name) => Path.Combine(_tempDir, name);

    private void WriteFile(string name, string content) =>
        File.WriteAllText(GetPath(name), content, Encoding.UTF8);

    private string ReadFile(string name) =>
        File.ReadAllText(GetPath(name), Encoding.UTF8);

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
