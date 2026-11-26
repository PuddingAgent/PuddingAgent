using Microsoft.Extensions.Logging.Abstractions;
using System.Text;
using System.Text.Json;
using PuddingCode.Tools;
using PuddingRuntime.Services;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Tools;

[TestClass]
public sealed class FileReadToolTests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"pudding-frt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // Test 1: Small file unaffected — returns full content
    [TestMethod]
    public async Task SmallFile_Unaffected_ReturnsFullContent()
    {
        var content = "line1\nline2\nline3\n";
        var filePath = Path.Combine(_tempDir, "small.txt");
        await File.WriteAllTextAsync(filePath, content, Encoding.UTF8);

        var tool = new FileReadTool(NullLogger<FileReadTool>.Instance, new FileChunkService());
        var result = await ExecuteAsync(tool, filePath);

        Assert.IsTrue(result.Success, result.Error);
        StringAssert.Contains(result.Output, "line1");
        StringAssert.Contains(result.Output, "line2");
        StringAssert.Contains(result.Output, "line3");
        Assert.IsFalse(result.Output.Contains("GUARDRAIL"));
    }

    // Test 2: Large file (>300 lines) triggers guardrail
    [TestMethod]
    public async Task LargeFile_TriggersGuardrail_ShowsFirst400Lines()
    {
        var lines = Enumerable.Range(1, 500).Select(i => $"line {i}");
        var content = string.Join("\n", lines);
        var filePath = Path.Combine(_tempDir, "large.txt");
        await File.WriteAllTextAsync(filePath, content, Encoding.UTF8);

        var tool = new FileReadTool(NullLogger<FileReadTool>.Instance, new FileChunkService());
        var result = await ExecuteAsync(tool, filePath);

        Assert.IsTrue(result.Success, result.Error);
        StringAssert.Contains(result.Output, "GUARDRAIL");
        StringAssert.Contains(result.Output, "500 lines");
        StringAssert.Contains(result.Output, "line 1");
        // 2026-08-22 窗口 120→400：减少模型同文件翻页重读
        StringAssert.Contains(result.Output, "line 400");
        Assert.IsFalse(result.Output.Contains("line 450"));
    }

    // Test 3: File above the progressive 8KB threshold triggers guardrail even if <300 lines
    [TestMethod]
    public async Task LargeByBytes_TriggersGuardrail()
    {
        var sb = new StringBuilder();
        var longLine = new string('x', 1000);
        for (int i = 0; i < 50; i++)
        {
            if (i > 0) sb.Append('\n');
            sb.Append($"{i:D4} {longLine}");
        }
        var content = sb.ToString();
        var filePath = Path.Combine(_tempDir, "wide.txt");
        await File.WriteAllTextAsync(filePath, content, Encoding.UTF8);

        var fileInfo = new FileInfo(filePath);
        Assert.IsTrue(fileInfo.Length > 40_000, $"File should be >40KB, got {fileInfo.Length}");

        var tool = new FileReadTool(NullLogger<FileReadTool>.Instance, new FileChunkService());
        var result = await ExecuteAsync(tool, filePath);

        Assert.IsTrue(result.Success, result.Error);
        StringAssert.Contains(result.Output, "GUARDRAIL");
        StringAssert.Contains(result.Output, "0000");
    }

    // Test 4: FullFile=true bypasses guardrail
    [TestMethod]
    public async Task FullFile_BypassesGuardrail_ReturnsAllContent()
    {
        var lines = Enumerable.Range(1, 500).Select(i => $"line {i}");
        var content = string.Join("\n", lines);
        var filePath = Path.Combine(_tempDir, "full_bypass.txt");
        await File.WriteAllTextAsync(filePath, content, Encoding.UTF8);

        var tool = new FileReadTool(NullLogger<FileReadTool>.Instance, new FileChunkService());
        var result = await ExecuteAsync(tool, filePath, new Dictionary<string, object?>
        {
            ["FullFile"] = true
        });

        Assert.IsTrue(result.Success, result.Error);
        Assert.IsFalse(result.Output.Contains("GUARDRAIL"));
        StringAssert.Contains(result.Output, "line 500");
    }

    // Test 5: Explicit HeadLines bypasses guardrail
    [TestMethod]
    public async Task ExplicitHeadLines_BypassesGuardrail()
    {
        var lines = Enumerable.Range(1, 500).Select(i => $"line {i}");
        var content = string.Join("\n", lines);
        var filePath = Path.Combine(_tempDir, "head_bypass.txt");
        await File.WriteAllTextAsync(filePath, content, Encoding.UTF8);

        var tool = new FileReadTool(NullLogger<FileReadTool>.Instance, new FileChunkService());
        var result = await ExecuteAsync(tool, filePath, new Dictionary<string, object?>
        {
            ["HeadLines"] = 50
        });

        Assert.IsTrue(result.Success, result.Error);
        Assert.IsFalse(result.Output.Contains("GUARDRAIL"));
        StringAssert.Contains(result.Output, "line 50");
        Assert.IsFalse(result.Output.Contains("line 51"));
    }

    // Test 6: OffsetLines + LimitLines bypasses guardrail
    [TestMethod]
    public async Task OffsetLines_BypassesGuardrail_ReturnsWindow()
    {
        var lines = Enumerable.Range(1, 500).Select(i => $"line {i}");
        var content = string.Join("\n", lines);
        var filePath = Path.Combine(_tempDir, "offset_bypass.txt");
        await File.WriteAllTextAsync(filePath, content, Encoding.UTF8);

        var tool = new FileReadTool(NullLogger<FileReadTool>.Instance, new FileChunkService());
        var result = await ExecuteAsync(tool, filePath, new Dictionary<string, object?>
        {
            ["OffsetLines"] = 400,
            ["LimitLines"] = 50
        });

        Assert.IsTrue(result.Success, result.Error);
        Assert.IsFalse(result.Output.Contains("GUARDRAIL"));
        StringAssert.Contains(result.Output, "line 401");
        StringAssert.Contains(result.Output, "line 450");
    }

    // Test 7: UTF-8 boundary safety — multi-byte characters near truncation
    [TestMethod]
    public async Task Utf8_MultiByteCharacters_PreservedAtBoundary()
    {
        var specialLine = "line 100 München € café résumé naïve";
        var sb = new StringBuilder();
        for (int i = 1; i <= 350; i++)
        {
            if (i > 1) sb.Append('\n');
            if (i == 100)
                sb.Append(specialLine);
            else
                sb.Append($"line {i}");
        }
        var content = sb.ToString();
        var filePath = Path.Combine(_tempDir, "utf8.txt");
        await File.WriteAllTextAsync(filePath, content, Encoding.UTF8);

        var tool = new FileReadTool(NullLogger<FileReadTool>.Instance, new FileChunkService());
        var result = await ExecuteAsync(tool, filePath);

        Assert.IsTrue(result.Success, result.Error);
        StringAssert.Contains(result.Output, "GUARDRAIL");
        Assert.IsTrue(result.Output.Contains("München"), "ü (U+00FC) should be preserved");
        Assert.IsTrue(result.Output.Contains("€"), "€ (Euro sign, 3-byte UTF-8) should be preserved");
        Assert.IsTrue(result.Output.Contains("café"), "é should be preserved");
        Assert.IsTrue(result.Output.Contains("résumé"), "é should be preserved");
        Assert.IsTrue(result.Output.Contains("naïve"), "ï should be preserved");
    }

    // Test 8: TailLines bypasses guardrail
    [TestMethod]
    public async Task TailLines_BypassesGuardrail_ShowsLastLines()
    {
        var lines = Enumerable.Range(1, 500).Select(i => $"line {i}");
        var content = string.Join("\n", lines);
        var filePath = Path.Combine(_tempDir, "tail_bypass.txt");
        await File.WriteAllTextAsync(filePath, content, Encoding.UTF8);

        var tool = new FileReadTool(NullLogger<FileReadTool>.Instance, new FileChunkService());
        var result = await ExecuteAsync(tool, filePath, new Dictionary<string, object?>
        {
            ["TailLines"] = 10
        });

        Assert.IsTrue(result.Success, result.Error);
        Assert.IsFalse(result.Output.Contains("GUARDRAIL"));
        StringAssert.Contains(result.Output, "line 500");
        Assert.IsFalse(result.Output.Contains("line 489"));
    }

    // Test 9: Large file with trailing newline — TailLines returns the last N real lines
    [TestMethod]
    public async Task LargeFile_TrailingNewline_TailLines_ReturnsLastLines()
    {
        var lines = Enumerable.Range(1, 3000).Select(i => $"line {i:D5} " + new string('x', 40));
        var content = string.Join("\n", lines) + "\n"; // trailing newline
        var filePath = Path.Combine(_tempDir, "tail_trailing_large.txt");
        await File.WriteAllTextAsync(filePath, content, Encoding.UTF8);

        var fileInfo = new FileInfo(filePath);
        Assert.IsTrue(fileInfo.Length > FileChunkService.LargeFileByteThreshold,
            $"File should exceed large-file byte threshold, got {fileInfo.Length}");

        var tool = new FileReadTool(NullLogger<FileReadTool>.Instance, new FileChunkService());
        var result = await ExecuteAsync(tool, filePath, new Dictionary<string, object?>
        {
            ["TailLines"] = 3
        });

        Assert.IsTrue(result.Success, result.Error);
        Assert.IsFalse(result.Output.Contains("GUARDRAIL"));
        StringAssert.Contains(result.Output, "line 02998");
        StringAssert.Contains(result.Output, "line 02999");
        StringAssert.Contains(result.Output, "line 03000");
        Assert.IsFalse(result.Output.Contains("line 02997"));
    }

    // Test 10: Small file with trailing newline — TailLines returns the last real line
    [TestMethod]
    public async Task SmallFile_TrailingNewline_TailLines_ReturnsLastLine()
    {
        var content = "alpha\nbeta\ngamma\n";
        var filePath = Path.Combine(_tempDir, "tail_trailing_small.txt");
        await File.WriteAllTextAsync(filePath, content, Encoding.UTF8);

        var tool = new FileReadTool(NullLogger<FileReadTool>.Instance, new FileChunkService());
        var result = await ExecuteAsync(tool, filePath, new Dictionary<string, object?>
        {
            ["TailLines"] = 1
        });

        Assert.IsTrue(result.Success, result.Error);
        Assert.IsFalse(result.Output.Contains("GUARDRAIL"));
        StringAssert.Contains(result.Output, "gamma");
        Assert.IsFalse(result.Output.Contains("beta"));
        Assert.IsFalse(result.Output.Contains("alpha"));
    }

    // 2026-11-26 缺陷：大文件路径曾把 fileInfo.Length（字节）写进 `chars` 字段，
    // 同一字段在小/大文件下分别是字符数与字节数。下面两条用例分别钉住两个路径。
    [TestMethod]
    public async Task Meta_OnLargeFile_ReportsBytesNotChars()
    {
        var line = new string('中', 20) + "\n";
        var content = string.Concat(Enumerable.Repeat(line, 2000)); // 42000 chars, 122000 bytes
        var filePath = Path.Combine(_tempDir, "meta_large.txt");
        await File.WriteAllTextAsync(filePath, content, new UTF8Encoding(false));

        var fileInfo = new FileInfo(filePath);
        Assert.IsTrue(fileInfo.Length > FileChunkService.LargeFileByteThreshold,
            $"Fixture must exceed the large-file threshold, got {fileInfo.Length}");
        Assert.AreNotEqual(fileInfo.Length, content.Length, "Fixture must have bytes != chars");

        var tool = new FileReadTool(NullLogger<FileReadTool>.Instance, new FileChunkService());
        var result = await ExecuteAsync(tool, filePath, new Dictionary<string, object?>
        {
            ["HeadLines"] = 1
        });

        Assert.IsTrue(result.Success, result.Error);
        StringAssert.Contains(result.Output, $"size={fileInfo.Length} bytes");
        Assert.IsFalse(result.Output.Contains($"size={fileInfo.Length} chars"),
            "A byte count must never be rendered as chars: " + result.Output.Split('\n')[0]);
    }

    [TestMethod]
    public async Task Meta_OnSmallFile_ReportsCharsAndBytes()
    {
        var line = new string('中', 20) + "\n";
        var content = string.Concat(Enumerable.Repeat(line, 1000)); // 21000 chars, 61000 bytes
        var filePath = Path.Combine(_tempDir, "meta_small.txt");
        await File.WriteAllTextAsync(filePath, content, new UTF8Encoding(false));

        var fileInfo = new FileInfo(filePath);
        Assert.IsTrue(fileInfo.Length < FileChunkService.LargeFileByteThreshold,
            $"Fixture must stay below the large-file threshold, got {fileInfo.Length}");
        Assert.AreNotEqual(fileInfo.Length, content.Length, "Fixture must have bytes != chars");

        var tool = new FileReadTool(NullLogger<FileReadTool>.Instance, new FileChunkService());
        var result = await ExecuteAsync(tool, filePath, new Dictionary<string, object?>
        {
            ["HeadLines"] = 1
        });

        Assert.IsTrue(result.Success, result.Error);
        StringAssert.Contains(result.Output, $"size={content.Length} chars / {fileInfo.Length} bytes");
    }

    // Test 11: OffsetLines out of range on a small file returns empty Ok (not Fail)
    [TestMethod]
    public async Task OffsetLines_OutOfRange_SmallFile_ReturnsEmptyOk()
    {
        var content = "alpha\nbeta\ngamma\n";
        var filePath = Path.Combine(_tempDir, "offset_oob_small.txt");
        await File.WriteAllTextAsync(filePath, content, Encoding.UTF8);

        var tool = new FileReadTool(NullLogger<FileReadTool>.Instance, new FileChunkService());
        var result = await ExecuteAsync(tool, filePath, new Dictionary<string, object?>
        {
            ["OffsetLines"] = 100
        });

        Assert.IsTrue(result.Success, result.Error);
        Assert.IsFalse(result.Output.Contains("alpha"));
        Assert.IsFalse(result.Output.Contains("beta"));
        Assert.IsFalse(result.Output.Contains("gamma"));
    }

    // Test 12: OffsetLines out of range on a large file returns empty Ok (consistent with small path)
    [TestMethod]
    public async Task OffsetLines_OutOfRange_LargeFile_ReturnsEmptyOk()
    {
        var lines = Enumerable.Range(1, 3000).Select(i => $"line {i:D5} " + new string('x', 40));
        var content = string.Join("\n", lines);
        var filePath = Path.Combine(_tempDir, "offset_oob_large.txt");
        await File.WriteAllTextAsync(filePath, content, Encoding.UTF8);

        var fileInfo = new FileInfo(filePath);
        Assert.IsTrue(fileInfo.Length > FileChunkService.LargeFileByteThreshold,
            $"File should exceed large-file byte threshold, got {fileInfo.Length}");

        var tool = new FileReadTool(NullLogger<FileReadTool>.Instance, new FileChunkService());
        var result = await ExecuteAsync(tool, filePath, new Dictionary<string, object?>
        {
            ["OffsetLines"] = 100000
        });

        Assert.IsTrue(result.Success, result.Error);
        Assert.IsFalse(result.Output.Contains("line 03000"));
    }

    // Test 13: guardrail output must fit the tool-result inline budget.
    // Regression: the 2026-08-22 window bump (120→400 lines) made the default preview
    // exceed ToolResultContextPolicy's 8 KiB bound. Every file_read then got spilled
    // into a brand-new artifact whose notice told the model to file_read again — the
    // model looped between "read artifact" and "produce artifact" (observed: 28 requests,
    // 3.62M tokens, no convergence).
    [TestMethod]
    public async Task Guardrail_WideLines_OutputFitsInlineBudget()
    {
        // 400 lines x ~100 wide bytes = ~40 KiB, far above the inline bound.
        var lines = Enumerable.Range(1, 400).Select(i => $"line {i} " + new string('x', 90));
        var content = string.Join("\n", lines);
        var filePath = Path.Combine(_tempDir, "wide.txt");
        await File.WriteAllTextAsync(filePath, content, Encoding.UTF8);

        var tool = new FileReadTool(NullLogger<FileReadTool>.Instance, new FileChunkService());
        var result = await ExecuteAsync(tool, filePath);

        Assert.IsTrue(result.Success, result.Error);
        StringAssert.Contains(result.Output, "GUARDRAIL");
        var bytes = Encoding.UTF8.GetByteCount(result.Output);
        Assert.IsTrue(
            bytes <= ToolResultContextPolicy.MaxInlineUtf8Bytes,
            $"file_read returned {bytes} bytes, above the {ToolResultContextPolicy.MaxInlineUtf8Bytes}-byte inline bound; " +
            "the result would be spilled again and the model would re-read it forever.");
    }

    // Test 14: the large-file path must respect the same bound.
    [TestMethod]
    public async Task Guardrail_LargeFile_OutputFitsInlineBudget()
    {
        var lines = Enumerable.Range(1, 3000).Select(i => $"line {i:D5} " + new string('y', 80));
        var content = string.Join("\n", lines);
        var filePath = Path.Combine(_tempDir, "wide_large.txt");
        await File.WriteAllTextAsync(filePath, content, Encoding.UTF8);

        var fileInfo = new FileInfo(filePath);
        Assert.IsTrue(fileInfo.Length > FileChunkService.LargeFileByteThreshold,
            $"File should exceed large-file byte threshold, got {fileInfo.Length}");

        var tool = new FileReadTool(NullLogger<FileReadTool>.Instance, new FileChunkService());
        var result = await ExecuteAsync(tool, filePath);

        Assert.IsTrue(result.Success, result.Error);
        StringAssert.Contains(result.Output, "GUARDRAIL");
        var bytes = Encoding.UTF8.GetByteCount(result.Output);
        Assert.IsTrue(
            bytes <= ToolResultContextPolicy.MaxInlineUtf8Bytes,
            $"large-file guardrail returned {bytes} bytes, above the {ToolResultContextPolicy.MaxInlineUtf8Bytes}-byte bound.");
    }

    // Test 15: FullFile=true must NOT bypass the inline byte budget.
    // Regression: FullFile (and explicit pagination) skipped the guardrail entirely, so a model
    // reading a spilled 21 KiB artifact got the whole thing back, the runtime spilled it again,
    // and the artifact notice told the model to file_read again -- the loop was still reachable.
    // Observed: claude-sonnet-5-5, 32 requests / 4.15M tokens, no convergence.
    [TestMethod]
    public async Task FullFile_DoesNotBypassInlineByteBudget()
    {
        // Three logical lines but ~21 KiB: exactly the shape of a spilled tool-result artifact.
        var wide = new string('x', 21_000);
        var content = "header line\n" + wide + "\nfooter line";
        var filePath = Path.Combine(_tempDir, "wide_few_lines.txt");
        await File.WriteAllTextAsync(filePath, content, Encoding.UTF8);

        var tool = new FileReadTool(NullLogger<FileReadTool>.Instance, new FileChunkService());
        var result = await ExecuteAsync(tool, filePath, new Dictionary<string, object?>
        {
            ["FullFile"] = true
        });

        Assert.IsTrue(result.Success, result.Error);
        var bytes = Encoding.UTF8.GetByteCount(result.Output);
        Assert.IsTrue(
            bytes <= ToolResultContextPolicy.MaxInlineUtf8Bytes,
            $"FullFile=true returned {bytes} bytes, above the {ToolResultContextPolicy.MaxInlineUtf8Bytes}-byte bound; " +
            "the artifact read-back loop is still reachable.");
        StringAssert.Contains(result.Output, "OffsetLines=");
    }

    // Test 16: explicit pagination must NOT bypass the inline byte budget either.
    [TestMethod]
    public async Task ExplicitPagination_DoesNotBypassInlineByteBudget()
    {
        var lines = Enumerable.Range(1, 2000).Select(i => $"line {i:D5} " + new string('z', 80));
        var content = string.Join("\n", lines);
        var filePath = Path.Combine(_tempDir, "wide_paged.txt");
        await File.WriteAllTextAsync(filePath, content, Encoding.UTF8);

        var tool = new FileReadTool(NullLogger<FileReadTool>.Instance, new FileChunkService());
        var result = await ExecuteAsync(tool, filePath, new Dictionary<string, object?>
        {
            ["OffsetLines"] = 0,
            ["LimitLines"] = 2000
        });

        Assert.IsTrue(result.Success, result.Error);
        var bytes = Encoding.UTF8.GetByteCount(result.Output);
        Assert.IsTrue(
            bytes <= ToolResultContextPolicy.MaxInlineUtf8Bytes,
            $"OffsetLines=0+LimitLines=2000 returned {bytes} bytes, above the {ToolResultContextPolicy.MaxInlineUtf8Bytes}-byte bound.");
    }

    // Test 17: positive control -- the byte gate must not truncate content that already fits.
    [TestMethod]
    public async Task FullFile_SmallFile_ReturnsWholeContentWithoutTruncationNotice()
    {
        var content = "alpha\nbeta\ngamma";
        var filePath = Path.Combine(_tempDir, "small_full.txt");
        await File.WriteAllTextAsync(filePath, content, Encoding.UTF8);

        var tool = new FileReadTool(NullLogger<FileReadTool>.Instance, new FileChunkService());
        var result = await ExecuteAsync(tool, filePath, new Dictionary<string, object?>
        {
            ["FullFile"] = true
        });

        Assert.IsTrue(result.Success, result.Error);
        StringAssert.Contains(result.Output, "alpha");
        StringAssert.Contains(result.Output, "gamma");
        Assert.IsFalse(result.Output.Contains("TRUNCATED"), result.Output);
    }

    // Test 18: offset beyond the end of the file must say so, not return a bare META line.
    // 2026-11-27: 旧行为回到空正文 + 无提示，与「文件真的是空的」不可区分。
    [TestMethod]
    public async Task OffsetBeyondEnd_ReportsPagingNotice()
    {
        var content = "line1\nline2\nline3\n";
        var filePath = Path.Combine(_tempDir, "offset_beyond.txt");
        await File.WriteAllTextAsync(filePath, content, Encoding.UTF8);

        var tool = new FileReadTool(NullLogger<FileReadTool>.Instance, new FileChunkService());
        var result = await ExecuteAsync(tool, filePath, new Dictionary<string, object?>
        {
            ["OffsetLines"] = 99
        });

        Assert.IsTrue(result.Success, result.Error);
        StringAssert.Contains(result.Output, "[PAGING:");
        StringAssert.Contains(result.Output, "beyond the end of the file");
        StringAssert.Contains(result.Output, "3 line(s)");
    }

    // Test 19: same silence on the large-file streaming path. This is the arm that would break
    // if the check were implemented twice and only one copy kept.
    [TestMethod]
    public async Task OffsetBeyondEnd_ReportsPagingNotice_OnLargeFilePath()
    {
        var lines = Enumerable.Range(1, 4000).Select(i => $"line {i:D5} " + new string('y', 30));
        var filePath = Path.Combine(_tempDir, "offset_beyond_large.txt");
        await File.WriteAllTextAsync(filePath, string.Join("\n", lines), Encoding.UTF8);

        var fileInfo = new FileInfo(filePath);
        Assert.IsTrue(
            fileInfo.Length > FileChunkService.LargeFileByteThreshold,
            $"File should exceed large-file byte threshold, got {fileInfo.Length}");

        var tool = new FileReadTool(NullLogger<FileReadTool>.Instance, new FileChunkService());
        var result = await ExecuteAsync(tool, filePath, new Dictionary<string, object?>
        {
            ["OffsetLines"] = 999_999
        });

        Assert.IsTrue(result.Success, result.Error);
        StringAssert.Contains(result.Output, "[PAGING:");
        StringAssert.Contains(result.Output, "beyond the end of the file");
    }

    // Test 20: a negative offset used to be clamped to 0 silently — the caller got the first line
    // and had no way to tell its argument was rewritten.
    [TestMethod]
    public async Task NegativeOffset_ReportsClampedNotice_AndReadsFromFirstLine()
    {
        var content = "alpha\nbeta\ngamma\n";
        var filePath = Path.Combine(_tempDir, "offset_negative.txt");
        await File.WriteAllTextAsync(filePath, content, Encoding.UTF8);

        var tool = new FileReadTool(NullLogger<FileReadTool>.Instance, new FileChunkService());
        var result = await ExecuteAsync(tool, filePath, new Dictionary<string, object?>
        {
            ["OffsetLines"] = -5,
            ["LimitLines"] = 1
        });

        Assert.IsTrue(result.Success, result.Error);
        StringAssert.Contains(result.Output, "negative and was applied as 0");
        StringAssert.Contains(result.Output, "alpha");
        Assert.IsFalse(result.Output.Contains("beta"), result.Output);
    }

    // Test 21: LimitLines <= 0 reads nothing; without the notice the empty body looks like an empty file.
    [TestMethod]
    public async Task NonPositiveLimitLines_ReportsEmptyBodyNotice()
    {
        var content = "alpha\nbeta\n";
        var filePath = Path.Combine(_tempDir, "limit_zero.txt");
        await File.WriteAllTextAsync(filePath, content, Encoding.UTF8);

        var tool = new FileReadTool(NullLogger<FileReadTool>.Instance, new FileChunkService());
        var result = await ExecuteAsync(tool, filePath, new Dictionary<string, object?>
        {
            ["OffsetLines"] = 0,
            ["LimitLines"] = 0
        });

        Assert.IsTrue(result.Success, result.Error);
        StringAssert.Contains(result.Output, "requests no line");
    }

    // Test 22: positive control — a well-formed window must stay quiet, otherwise the notice
    // becomes noise and models learn to ignore it.
    [TestMethod]
    public async Task OffsetWithinRange_HasNoPagingNotice()
    {
        var content = "alpha\nbeta\ngamma\ndelta\n";
        var filePath = Path.Combine(_tempDir, "offset_fine.txt");
        await File.WriteAllTextAsync(filePath, content, Encoding.UTF8);

        var tool = new FileReadTool(NullLogger<FileReadTool>.Instance, new FileChunkService());
        var result = await ExecuteAsync(tool, filePath, new Dictionary<string, object?>
        {
            ["OffsetLines"] = 1,
            ["LimitLines"] = 2
        });

        Assert.IsTrue(result.Success, result.Error);
        Assert.IsFalse(result.Output.Contains("[PAGING:"), result.Output);
        StringAssert.Contains(result.Output, "beta");
    }

    // Test 23: MaxChars is a budget on THIS window. The old implementation applied it to the
    // whole file before the paging branches ran, so max_chars silently discarded OffsetLines.
    // 2026-11-27.
    [TestMethod]
    public async Task MaxChars_WithOffset_TruncatesWindowNotFileHead()
    {
        var content = "line1\nline2\nline3\nline4\nline5\nline6\n";
        var filePath = Path.Combine(_tempDir, "maxchars_offset.txt");
        await File.WriteAllTextAsync(filePath, content, Encoding.UTF8);

        var tool = new FileReadTool(NullLogger<FileReadTool>.Instance, new FileChunkService());
        var result = await ExecuteAsync(tool, filePath, new Dictionary<string, object?>
        {
            ["OffsetLines"] = 3,
            ["LimitLines"] = 2,
            ["MaxChars"] = 8
        });

        Assert.IsTrue(result.Success, result.Error);
        StringAssert.Contains(result.Output, "line4");
        Assert.IsFalse(result.Output.Contains("line1"), result.Output);
        StringAssert.Contains(result.Output, "truncated at 8 chars");
        StringAssert.Contains(result.Output, "continue with OffsetLines=4+LimitLines");
    }

    // Test 24: a file above the guardrail thresholds used to skip MaxChars entirely — the
    // guardrail returned its own preview and max_chars never took effect. MaxChars is itself
    // an explicit output budget, so it must win over the automatic preview.
    [TestMethod]
    public async Task MaxChars_OnFileAboveGuardrail_DoesNotFallBackToGuardrail()
    {
        var lines = Enumerable.Range(1, 350).Select(i => $"row {i:D5} " + new string('z', 40));
        var filePath = Path.Combine(_tempDir, "maxchars_guardrail.txt");
        await File.WriteAllTextAsync(filePath, string.Join("\n", lines) + "\n", Encoding.UTF8);

        var tool = new FileReadTool(NullLogger<FileReadTool>.Instance, new FileChunkService());
        var result = await ExecuteAsync(tool, filePath, new Dictionary<string, object?>
        {
            ["MaxChars"] = 200
        });

        Assert.IsTrue(result.Success, result.Error);
        StringAssert.Contains(result.Output, "truncated at 200 chars");
        Assert.IsFalse(result.Output.Contains("[GUARDRAIL:"), result.Output);
    }

    // Test 25: the same silence on the large-file streaming path — the arm that would break if
    // the window were selected twice and only one copy honoured the paging arguments.
    [TestMethod]
    public async Task MaxChars_OnLargeFilePath_HonorsOffsetAndReportsContinuation()
    {
        var lines = Enumerable.Range(1, 4000).Select(i => $"line {i:D5} " + new string('y', 30));
        var filePath = Path.Combine(_tempDir, "maxchars_large_offset.txt");
        await File.WriteAllTextAsync(filePath, string.Join("\n", lines), Encoding.UTF8);

        var fileInfo = new FileInfo(filePath);
        Assert.IsTrue(
            fileInfo.Length > FileChunkService.LargeFileByteThreshold,
            $"File should exceed large-file byte threshold, got {fileInfo.Length}");

        var tool = new FileReadTool(NullLogger<FileReadTool>.Instance, new FileChunkService());
        var result = await ExecuteAsync(tool, filePath, new Dictionary<string, object?>
        {
            ["OffsetLines"] = 3500,
            ["LimitLines"] = 5,
            ["MaxChars"] = 50
        });

        Assert.IsTrue(result.Success, result.Error);
        StringAssert.Contains(result.Output, "line 03501");
        Assert.IsFalse(result.Output.Contains("line 00001"), result.Output);
        StringAssert.Contains(result.Output, "continue with OffsetLines=3501+LimitLines");
    }

    // Test 26: positive control — when the window fits the budget nothing is announced, so a
    // notice that always fires cannot pass this suite.
    [TestMethod]
    public async Task MaxChars_WhenWindowFits_HasNoTruncationNotice()
    {
        var content = "alpha\nbeta\ngamma\n";
        var filePath = Path.Combine(_tempDir, "maxchars_fits.txt");
        await File.WriteAllTextAsync(filePath, content, Encoding.UTF8);

        var tool = new FileReadTool(NullLogger<FileReadTool>.Instance, new FileChunkService());
        var result = await ExecuteAsync(tool, filePath, new Dictionary<string, object?>
        {
            ["OffsetLines"] = 1,
            ["MaxChars"] = 10_000
        });

        Assert.IsTrue(result.Success, result.Error);
        Assert.IsFalse(result.Output.Contains("truncated at"), result.Output);
        Assert.IsFalse(result.Output.Contains("[GUARDRAIL:"), result.Output);
        StringAssert.Contains(result.Output, "beta");
        Assert.IsFalse(result.Output.Contains("alpha"), result.Output);
    }

    private static Task<ToolExecutionResult> ExecuteAsync(
        FileReadTool tool,
        string path,
        IReadOnlyDictionary<string, object?>? extraParameters = null)
    {
        var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["path"] = path
        };

        if (extraParameters != null)
        {
            foreach (var kvp in extraParameters)
                args[kvp.Key] = kvp.Value;
        }

        return tool.ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "call-1",
            ArgumentsJson = JsonSerializer.Serialize(args),
            Context = new ToolExecutionContext
            {
                AgentInstanceId = "agent",
                WorkspaceId = "workspace",
                SessionId = "session",
            },
        });
    }
}
