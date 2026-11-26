using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
    // 契约测试：参数说明必须与实现语义一致（2026-11-27）。
    // 旧说明「Use with OffsetLines」暗示 limit_lines 必须与 offset_lines 成对，
    // 而实现已允许单独传入 = 从首行起前 N 行；说明不跟上，模型就不会单独用它。
    [TestMethod]
    public void LimitLinesDescription_DocumentsStandaloneFirstLines()
    {
        var limit = typeof(FileReadArgs).GetProperty(nameof(FileReadArgs.LimitLines))!
            .GetCustomAttribute<ToolParamAttribute>()!;
        StringAssert.Contains(limit.Description, "first N lines");
        StringAssert.Contains(limit.Description, "OffsetLines");
    }

    [TestMethod]
    public void OffsetLinesDescription_DoesNotImplyMandatoryPairing()
    {
        var offset = typeof(FileReadArgs).GetProperty(nameof(FileReadArgs.OffsetLines))!
            .GetCustomAttribute<ToolParamAttribute>()!;
        Assert.IsFalse(offset.Description.Contains("Use with LimitLines"),
            $"OffsetLines description still implies a mandatory pairing: {offset.Description}");
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

    // Test 19b: 2026-11-27 — HeadLines/TailLines 存在时 OffsetLines 不参与窗口计算。
    // 旧行为静默：offset=8 + tail=3 返回 L8..L10，调用方以为「从第 9 行起的末 3 行」。
    [TestMethod]
    public async Task OffsetLines_WithTailLines_ReportsIgnored()
    {
        var filePath = Path.Combine(_tempDir, "offset_with_tail.txt");
        await File.WriteAllTextAsync(filePath, "L1\nL2\nL3\nL4\nL5\nL6\nL7\nL8\nL9\nL10\n", Encoding.UTF8);

        var tool = new FileReadTool(NullLogger<FileReadTool>.Instance, new FileChunkService());
        var result = await ExecuteAsync(tool, filePath, new Dictionary<string, object?>
        {
            ["OffsetLines"] = 8,
            ["TailLines"] = 3
        });

        Assert.IsTrue(result.Success, result.Error);
        StringAssert.Contains(result.Output, "[PAGING:");
        StringAssert.Contains(result.Output, "OffsetLines=8 was ignored");
        StringAssert.Contains(result.Output, "TailLines=3");
    }

    // Test 19c: LimitLines 同样被 HeadLines 静默推翻。
    [TestMethod]
    public async Task LimitLines_WithHeadLines_ReportsIgnored()
    {
        var filePath = Path.Combine(_tempDir, "limit_with_head.txt");
        await File.WriteAllTextAsync(filePath, "L1\nL2\nL3\nL4\nL5\n", Encoding.UTF8);

        var tool = new FileReadTool(NullLogger<FileReadTool>.Instance, new FileChunkService());
        var result = await ExecuteAsync(tool, filePath, new Dictionary<string, object?>
        {
            ["HeadLines"] = 5,
            ["LimitLines"] = 2
        });

        Assert.IsTrue(result.Success, result.Error);
        StringAssert.Contains(result.Output, "LimitLines=2 was ignored");
        StringAssert.Contains(result.Output, "L5");
    }

    // Test 19d: HeadLines 优先于 TailLines。
    [TestMethod]
    public async Task TailLines_WithHeadLines_ReportsPrecedence()
    {
        var filePath = Path.Combine(_tempDir, "tail_with_head.txt");
        await File.WriteAllTextAsync(filePath, "L1\nL2\nL3\nL4\nL5\n", Encoding.UTF8);

        var tool = new FileReadTool(NullLogger<FileReadTool>.Instance, new FileChunkService());
        var result = await ExecuteAsync(tool, filePath, new Dictionary<string, object?>
        {
            ["HeadLines"] = 2,
            ["TailLines"] = 3
        });

        Assert.IsTrue(result.Success, result.Error);
        StringAssert.Contains(result.Output, "TailLines=3 was ignored because HeadLines takes precedence");
    }

    // Test 19e: 正向对照 —— 单独给 HeadLines 不该出现 [PAGING:。
    // 没有这一条，一个恒发提示的实现也能让上面三条全绿。
    [TestMethod]
    public async Task HeadLines_Alone_HasNoPagingNotice()
    {
        var filePath = Path.Combine(_tempDir, "head_alone.txt");
        await File.WriteAllTextAsync(filePath, "L1\nL2\nL3\n", Encoding.UTF8);

        var tool = new FileReadTool(NullLogger<FileReadTool>.Instance, new FileChunkService());
        var result = await ExecuteAsync(tool, filePath, new Dictionary<string, object?>
        {
            ["HeadLines"] = 2
        });

        Assert.IsTrue(result.Success, result.Error);
        Assert.IsFalse(result.Output.Contains("[PAGING:"), result.Output);
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

    // Test 27: MaxChars and the 8 KiB inline byte budget both bind. The receipt used to carry two
    // continuation offsets computed from different truncation points: the body showed only the lines
    // that fit the byte budget, while the MaxChars notice told the caller to resume past lines that
    // were never delivered. Both notices must name the SAME offset, and that offset must sit exactly
    // one line past the last fully delivered line. 2026-11-27.
    [TestMethod]
    public async Task MaxChars_WithInlineByteBudgetBound_ReportsSingleContinuationOffset()
    {
        var lines = Enumerable.Range(1, 500).Select(i => $"ROW{i:D4}-0123456789012345678901234567890123456789");
        var filePath = Path.Combine(_tempDir, "maxchars_inline_bound.txt");
        await File.WriteAllTextAsync(filePath, string.Join("\n", lines) + "\n", Encoding.UTF8);

        var fileInfo = new FileInfo(filePath);
        Assert.IsTrue(
            fileInfo.Length < FileChunkService.LargeFileByteThreshold,
            $"small-file path expected, got {fileInfo.Length} bytes");
        Assert.IsTrue(fileInfo.Length > 10_000, $"file must exceed the 8 KiB inline budget, got {fileInfo.Length}");

        var tool = new FileReadTool(NullLogger<FileReadTool>.Instance, new FileChunkService());
        var result = await ExecuteAsync(tool, filePath, new Dictionary<string, object?>
        {
            ["MaxChars"] = 20_000
        });

        Assert.IsTrue(result.Success, result.Error);
        StringAssert.Contains(result.Output, "inline budget bound first");
        Assert.IsFalse(result.Output.Contains("truncated at 20000 chars"), result.Output);
        AssertContinuationMatchesDelivery(result.Output);
    }

    // Test 28: the same invariant on the streaming path — it is a separate call site, so a fix
    // applied to only one branch must fail here.
    [TestMethod]
    public async Task MaxChars_OnLargeFilePath_WithInlineByteBudgetBound_ReportsSingleContinuationOffset()
    {
        var lines = Enumerable.Range(1, 4000).Select(i => $"ROW{i:D4}-0123456789012345678901234567890123456789");
        var filePath = Path.Combine(_tempDir, "maxchars_inline_bound_large.txt");
        await File.WriteAllTextAsync(filePath, string.Join("\n", lines), Encoding.UTF8);

        var fileInfo = new FileInfo(filePath);
        Assert.IsTrue(
            fileInfo.Length > FileChunkService.LargeFileByteThreshold,
            $"large-file path expected, got {fileInfo.Length} bytes");

        var tool = new FileReadTool(NullLogger<FileReadTool>.Instance, new FileChunkService());
        var result = await ExecuteAsync(tool, filePath, new Dictionary<string, object?>
        {
            ["MaxChars"] = 20_000
        });

        Assert.IsTrue(result.Success, result.Error);
        StringAssert.Contains(result.Output, "inline budget bound first");
        Assert.IsFalse(result.Output.Contains("truncated at 20000 chars"), result.Output);
        AssertContinuationMatchesDelivery(result.Output);
    }

    /// <summary>
    /// 两道预算同时生效时必须只有一个续读位置，且它恰好等于「已完整交付的最后一行 + 1」。
    /// 断两个方向：少一条 notice 会变多值（Count != 1），偏移跳远会与正文末行对不上。
    /// </summary>
    private static void AssertContinuationMatchesDelivery(string output)
    {
        var offsets = Regex.Matches(output, @"OffsetLines=(\d+)\+LimitLines")
            .Select(m => int.Parse(m.Groups[1].Value))
            .Distinct()
            .ToList();

        Assert.AreEqual(
            1,
            offsets.Count,
            $"expected one continuation offset, got [{string.Join(", ", offsets)}]");

        var offset = offsets[0];
        StringAssert.Contains(output, $"ROW{offset:D4}-", $"line ROW{offset:D4} must be the first line after the window");
        Assert.IsFalse(
            output.Contains($"ROW{offset + 1:D4}-"),
            $"ROW{offset + 1:D4} was never delivered, so it cannot sit before the continuation point");
    }

    // Test 28: a budget shorter than the first line delivers no complete line, and the old notice
    // still advised `OffsetLines=0` — following that advice re-issued the same call and returned the
    // same receipt, forever. 实测 max_chars=10 对 18 字首行得到「continue with OffsetLines=0」。
    // 2026-11-27.
    [TestMethod]
    public async Task MaxChars_ShorterThanFirstLine_DoesNotAdviseNonAdvancingOffset()
    {
        var content = "0123456789abcdef\nsecond line\nthird line\n";
        var filePath = Path.Combine(_tempDir, "maxchars_short_first_line.txt");
        await File.WriteAllTextAsync(filePath, content, Encoding.UTF8);

        var tool = new FileReadTool(NullLogger<FileReadTool>.Instance, new FileChunkService());
        var result = await ExecuteAsync(tool, filePath, new Dictionary<string, object?>
        {
            ["MaxChars"] = 5
        });

        Assert.IsTrue(result.Success, result.Error);
        StringAssert.Contains(result.Output, "no complete line fits in that budget");
        StringAssert.Contains(result.Output, "would return this same output again");
        Assert.IsFalse(
            result.Output.Contains("continue with OffsetLines=0"),
            "a non-advancing continuation offset is a dead end, not advice");
    }

    // Test 29: 首行自身就超过 inline 字节预算时「Raise max_chars」是死路 —— 实测
    // max_chars=20000 对 20000 字节首行仍得到同一句，而 max_chars 已等于行长。2026-11-27.
    [TestMethod]
    public async Task MaxChars_FirstLineExceedsInlineBudget_DoesNotAdviseRaisingMaxChars()
    {
        var content = new string('F', 20_000) + "\n" + string.Join("\n", Enumerable.Range(1, 5).Select(i => $"tail{i}"));
        var filePath = Path.Combine(_tempDir, "maxchars_first_line_over_budget.txt");
        await File.WriteAllTextAsync(filePath, content, Encoding.UTF8);

        var fileInfo = new FileInfo(filePath);
        Assert.IsTrue(
            fileInfo.Length < FileChunkService.LargeFileByteThreshold,
            $"small-file path expected, got {fileInfo.Length} bytes");

        var tool = new FileReadTool(NullLogger<FileReadTool>.Instance, new FileChunkService());
        var result = await ExecuteAsync(tool, filePath, new Dictionary<string, object?>
        {
            ["MaxChars"] = 20_000
        });

        Assert.IsTrue(result.Success, result.Error);
        StringAssert.Contains(result.Output, "no max_chars value can deliver it");
        Assert.IsFalse(
            result.Output.Contains("Raise max_chars"),
            "max_chars already equals the line length; raising it cannot deliver a line longer than the byte budget");
    }

    // Test 30: 同一个判断的大文件路径调用点 —— 只修一处时另一处必须红。2026-11-27.
    [TestMethod]
    public async Task MaxChars_OnLargeFilePath_FirstLineExceedsInlineBudget_DoesNotAdviseRaisingMaxChars()
    {
        var lines = new[] { new string('G', 20_000) }.Concat(
            Enumerable.Range(1, 1500).Select(i => $"ROW{i:D4}-0123456789012345678901234567890123456789012345678901"));
        var filePath = Path.Combine(_tempDir, "maxchars_first_line_over_budget_large.txt");
        await File.WriteAllTextAsync(filePath, string.Join("\n", lines), Encoding.UTF8);

        var fileInfo = new FileInfo(filePath);
        Assert.IsTrue(
            fileInfo.Length > FileChunkService.LargeFileByteThreshold,
            $"large-file path expected, got {fileInfo.Length} bytes");

        var tool = new FileReadTool(NullLogger<FileReadTool>.Instance, new FileChunkService());
        var result = await ExecuteAsync(tool, filePath, new Dictionary<string, object?>
        {
            ["MaxChars"] = 20_000
        });

        Assert.IsTrue(result.Success, result.Error);
        StringAssert.Contains(result.Output, "no max_chars value can deliver it");
        Assert.IsFalse(
            result.Output.Contains("Raise max_chars"),
            "the large-file call site must reach the same verdict as the small-file one");
    }

    // Test 31: 首行自身超 inline 字节预算时，即使请求的预算比首行还小，结论也必须与小预算无关 ——
    // 实测 20001 字节首行、max_chars=3000 先说「Raise max_chars to at least the length of one line」，
    // 而照做（max_chars=20000）才改口「no max_chars value can deliver it」，白费一次往返。2026-11-27.
    [TestMethod]
    public async Task MaxChars_BelowFirstLine_WithFirstLineOverBudget_SaysNoBudgetCanDeliver()
    {
        var content = new string('H', 20_000) + "\n" + string.Join("\n", Enumerable.Range(1, 5).Select(i => $"tail{i}"));
        var filePath = Path.Combine(_tempDir, "maxchars_below_first_line_over_budget.txt");
        await File.WriteAllTextAsync(filePath, content, Encoding.UTF8);

        var fileInfo = new FileInfo(filePath);
        Assert.IsTrue(
            fileInfo.Length < FileChunkService.LargeFileByteThreshold,
            $"small-file path expected, got {fileInfo.Length} bytes");

        var tool = new FileReadTool(NullLogger<FileReadTool>.Instance, new FileChunkService());
        var result = await ExecuteAsync(tool, filePath, new Dictionary<string, object?>
        {
            ["MaxChars"] = 3_000
        });

        Assert.IsTrue(result.Success, result.Error);
        StringAssert.Contains(result.Output, "no max_chars value can deliver it");
        Assert.IsFalse(
            result.Output.Contains("Raise max_chars"),
            "the first line alone exceeds the byte budget, so no max_chars value can deliver it — raising the budget only changes the receipt, never the outcome");
    }

    // Test 32: 同一个判据的大文件路径调用点 —— 只修一处时另一处必须红。2026-11-27.
    [TestMethod]
    public async Task MaxChars_BelowFirstLine_OnLargeFilePath_SaysNoBudgetCanDeliver()
    {
        var lines = new[] { new string('J', 20_000) }.Concat(
            Enumerable.Range(1, 1500).Select(i => $"ROW{i:D4}-0123456789012345678901234567890123456789012345678901"));
        var filePath = Path.Combine(_tempDir, "maxchars_below_first_line_over_budget_large.txt");
        await File.WriteAllTextAsync(filePath, string.Join("\n", lines), Encoding.UTF8);

        var fileInfo = new FileInfo(filePath);
        Assert.IsTrue(
            fileInfo.Length > FileChunkService.LargeFileByteThreshold,
            $"large-file path expected, got {fileInfo.Length} bytes");

        var tool = new FileReadTool(NullLogger<FileReadTool>.Instance, new FileChunkService());
        var result = await ExecuteAsync(tool, filePath, new Dictionary<string, object?>
        {
            ["MaxChars"] = 3_000
        });

        Assert.IsTrue(result.Success, result.Error);
        StringAssert.Contains(result.Output, "no max_chars value can deliver it");
        Assert.IsFalse(
            result.Output.Contains("Raise max_chars"),
            "the large-file call site must reach the same verdict as the small-file one");
    }

    // Test 29: a one-line file larger than the 8 KiB inline budget was cut to 7680 bytes with NO
    // notice at all — `emittedLines < lines.Length` was false because there is only one line, so
    // the truncation was real and completely silent. MaxChars is passed only to bypass the
    // guardrail preview and reach the byte gate with the whole file as the window. 2026-11-27.
    [TestMethod]
    public async Task SingleLineLongerThanInlineBudget_AnnouncesTruncation()
    {
        var filePath = Path.Combine(_tempDir, "single_long_line.txt");
        await File.WriteAllTextAsync(filePath, new string('x', 20_000), Encoding.UTF8);

        var tool = new FileReadTool(NullLogger<FileReadTool>.Instance, new FileChunkService());
        var result = await ExecuteAsync(tool, filePath, new Dictionary<string, object?>
        {
            ["MaxChars"] = 20_000
        });

        Assert.IsTrue(result.Success, result.Error);
        StringAssert.Contains(result.Output, "[TRUNCATED:");
    }

    // Test 30: when the first line alone overflows the byte gate, the old notice said
    // 「Use OffsetLines=1+LimitLines to continue from line 2」 — jumping over the ~12 KB of that line
    // that were never delivered. A line offset cannot resume inside a line, so the receipt must say
    // so instead of naming an offset. 2026-11-27.
    [TestMethod]
    public async Task LongFirstLine_DoesNotAdviseOffsetPastUndeliveredRemainder()
    {
        var lines = new List<string> { new string('X', 20_000) };
        lines.AddRange(Enumerable.Range(2, 10).Select(i => $"tail-{i}"));
        var filePath = Path.Combine(_tempDir, "long_first_line_multi.txt");
        await File.WriteAllTextAsync(filePath, string.Join("\n", lines) + "\n", Encoding.UTF8);

        var tool = new FileReadTool(NullLogger<FileReadTool>.Instance, new FileChunkService());
        var result = await ExecuteAsync(tool, filePath, new Dictionary<string, object?>
        {
            ["MaxChars"] = 30_000
        });

        Assert.IsTrue(result.Success, result.Error);
        StringAssert.Contains(result.Output, "not addressable");
        Assert.IsFalse(result.Output.Contains("continue from line 2"), result.Output);
        Assert.IsFalse(result.Output.Contains("tail-2"), result.Output);
    }

    // 2026-11-27: 两处 GUARDRAIL（小文件路径与大文件路径）原先无条件写
    // "Use OffsetLines={taken}+LimitLines"，而首行被切成半行时 taken 已把该行算作已交付。
    // 于是回执让人从第 2 行续读，而第 1 行尚未交付的剩余内容无法用任何行偏移取回。
    // 实测：20002 字节单行文件报 "continue from line 2"（文件只有 1 行）。
    [TestMethod]
    public async Task Guardrail_SingleLineLongerThanInlineBudget_DoesNotAdviseLineOffset()
    {
        var filePath = Path.Combine(_tempDir, "guardrail_single_line.txt");
        await File.WriteAllTextAsync(filePath, new string('X', 20_000), Encoding.UTF8);

        var tool = new FileReadTool(NullLogger<FileReadTool>.Instance, new FileChunkService());
        var result = await ExecuteAsync(tool, filePath);

        Assert.IsTrue(result.Success, result.Error);
        StringAssert.Contains(result.Output, "GUARDRAIL");
        StringAssert.Contains(result.Output, "not addressable");
        Assert.IsFalse(result.Output.Contains("continue from line 2"), result.Output);
    }

    // 同上，带后续行：行偏移无法落入第 1 行中间，第 1 行余下的字节永远读不到。
    [TestMethod]
    public async Task Guardrail_LongFirstLineWithFollowingLines_DoesNotAdviseLineOffset()
    {
        var lines = new List<string> { new string('Y', 20_000) };
        lines.AddRange(Enumerable.Range(2, 19).Select(i => $"tail-line-{i:D2}"));
        var filePath = Path.Combine(_tempDir, "guardrail_long_first.txt");
        await File.WriteAllTextAsync(filePath, string.Join("\n", lines) + "\n", Encoding.UTF8);

        var tool = new FileReadTool(NullLogger<FileReadTool>.Instance, new FileChunkService());
        var result = await ExecuteAsync(tool, filePath);

        Assert.IsTrue(result.Success, result.Error);
        StringAssert.Contains(result.Output, "GUARDRAIL");
        StringAssert.Contains(result.Output, "not addressable");
        Assert.IsFalse(result.Output.Contains("continue from line 2"), result.Output);
        // 首行吃满预算，后续行本来就不该出现。
        Assert.IsFalse(result.Output.Contains("tail-line-02"), result.Output);
    }

    // 正向对照：首行不超预算时，GUARDRAIL 必须**仍然**给出可直接照抄的行偏移。
    // 没有这一条，一个「一律不给偏移」的实现也能让上面两条全绿。
    [TestMethod]
    public async Task Guardrail_FittingFirstLine_StillAdvisesLineOffset()
    {
        var lines = Enumerable.Range(1, 500).Select(i => $"line {i}");
        var filePath = Path.Combine(_tempDir, "guardrail_fitting_first.txt");
        await File.WriteAllTextAsync(filePath, string.Join("\n", lines), Encoding.UTF8);

        var tool = new FileReadTool(NullLogger<FileReadTool>.Instance, new FileChunkService());
        var result = await ExecuteAsync(tool, filePath);

        Assert.IsTrue(result.Success, result.Error);
        StringAssert.Contains(result.Output, "GUARDRAIL");
        StringAssert.Contains(result.Output, "OffsetLines=400+LimitLines");
        Assert.IsFalse(result.Output.Contains("not addressable"), result.Output);
    }

    // 大文件路径（> FileChunkService.LargeFileByteThreshold = 100_000 字节，走 chunk 读）是
    // 与上面两条**不同的调用点**：两者同源但各自缩了 out _。不加这条，大文件那处的
    // DescribeGuardrailSuffix 调用没有任何用例守护。
    [TestMethod]
    public async Task Guardrail_LargeFileSingleLineLongerThanInlineBudget_DoesNotAdviseLineOffset()
    {
        var filePath = Path.Combine(_tempDir, "guardrail_large_single_line.txt");
        await File.WriteAllTextAsync(filePath, new string('Z', 121_000), Encoding.UTF8);
        Assert.IsTrue(new FileInfo(filePath).Length > FileChunkService.LargeFileByteThreshold);

        var tool = new FileReadTool(NullLogger<FileReadTool>.Instance, new FileChunkService());
        var result = await ExecuteAsync(tool, filePath);

        Assert.IsTrue(result.Success, result.Error);
        StringAssert.Contains(result.Output, "GUARDRAIL");
        StringAssert.Contains(result.Output, "not addressable");
        Assert.IsFalse(result.Output.Contains("continue from line 2"), result.Output);
    }

    // 2026-11-27：LimitLines 单独给出时也是显式窗口（前 N 行）。
    // 旧实现只把 LimitLines 当 OffsetLines 的附属参数，于是单独传入等于没传：
    // 正文返回整个文件，而 LimitLines<=0 的提示又写着「body is empty」——回执与正文矛盾。
    [TestMethod]
    public async Task LimitLines_Alone_ReadsFirstLines()
    {
        var filePath = Path.Combine(_tempDir, "limit_alone.txt");
        await File.WriteAllTextAsync(filePath, "L1\nL2\nL3\nL4\nL5\n", Encoding.UTF8);

        var tool = new FileReadTool(NullLogger<FileReadTool>.Instance, new FileChunkService());
        var result = await ExecuteAsync(tool, filePath, new Dictionary<string, object?>
        {
            ["LimitLines"] = 2
        });

        Assert.IsTrue(result.Success, result.Error);
        StringAssert.Contains(result.Output, "L2");
        Assert.IsFalse(result.Output.Contains("L4"), result.Output);
    }

    // 正向对照：合法窗口不该发提示，否则提示变噪音。
    [TestMethod]
    public async Task LimitLines_Alone_HasNoPagingNotice()
    {
        var filePath = Path.Combine(_tempDir, "limit_alone_quiet.txt");
        await File.WriteAllTextAsync(filePath, "L1\nL2\nL3\nL4\nL5\n", Encoding.UTF8);

        var tool = new FileReadTool(NullLogger<FileReadTool>.Instance, new FileChunkService());
        var result = await ExecuteAsync(tool, filePath, new Dictionary<string, object?>
        {
            ["LimitLines"] = 2
        });

        Assert.IsTrue(result.Success, result.Error);
        Assert.IsFalse(result.Output.Contains("[PAGING:"), result.Output);
    }

    // 回执与正文必须一致：旧实现在 limit<=0 时报「body is empty」但正文是整个文件。
    [TestMethod]
    public async Task NonPositiveLimitLines_Alone_EmptyBodyMatchesNotice()
    {
        var filePath = Path.Combine(_tempDir, "limit_zero_alone.txt");
        await File.WriteAllTextAsync(filePath, "alpha\nbeta\n", Encoding.UTF8);

        var tool = new FileReadTool(NullLogger<FileReadTool>.Instance, new FileChunkService());
        var result = await ExecuteAsync(tool, filePath, new Dictionary<string, object?>
        {
            ["LimitLines"] = 0
        });

        Assert.IsTrue(result.Success, result.Error);
        StringAssert.Contains(result.Output, "requests no line");
        Assert.IsFalse(result.Output.Contains("alpha"), result.Output);
    }

    // LimitLines 必须算作「显式切片」：否则护栏先命中，用户给的 limit 被路过的预览推翻。
    [TestMethod]
    public async Task LimitLines_Alone_BypassesGuardrail()
    {
        var lines = Enumerable.Range(1, 350).Select(i => $"row {i:D3} " + new string('w', 36));
        var filePath = Path.Combine(_tempDir, "limit_alone_guardrail.txt");
        await File.WriteAllTextAsync(filePath, string.Join("\n", lines) + "\n", Encoding.UTF8);

        var tool = new FileReadTool(NullLogger<FileReadTool>.Instance, new FileChunkService());
        var result = await ExecuteAsync(tool, filePath, new Dictionary<string, object?>
        {
            ["LimitLines"] = 3
        });

        Assert.IsTrue(result.Success, result.Error);
        Assert.IsFalse(result.Output.Contains("GUARDRAIL"), result.Output);
        StringAssert.Contains(result.Output, "row 003");
        Assert.IsFalse(result.Output.Contains("row 004"), result.Output);
    }

    // 大文件路径（>100_000 字节，走 chunk 读）是与小文件**不同的调用点**：
    // 它的选择器分支与 hasExplicitSliceLarge 各需一条守护。
    [TestMethod]
    public async Task LimitLines_Alone_LargeFileReadsFirstLines()
    {
        var lines = Enumerable.Range(1, 400).Select(i => $"row {i:D5} " + new string('q', 290));
        var filePath = Path.Combine(_tempDir, "limit_alone_large.txt");
        await File.WriteAllTextAsync(filePath, string.Join("\n", lines) + "\n", Encoding.UTF8);
        Assert.IsTrue(new FileInfo(filePath).Length > FileChunkService.LargeFileByteThreshold);

        var tool = new FileReadTool(NullLogger<FileReadTool>.Instance, new FileChunkService());
        var result = await ExecuteAsync(tool, filePath, new Dictionary<string, object?>
        {
            ["LimitLines"] = 3
        });

        Assert.IsTrue(result.Success, result.Error);
        Assert.IsFalse(result.Output.Contains("GUARDRAIL"), result.Output);
        StringAssert.Contains(result.Output, "row 00003");
        Assert.IsFalse(result.Output.Contains("row 00004"), result.Output);
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
