using Microsoft.Extensions.Logging.Abstractions;
using System.Text;
using System.Text.Json;
using PuddingCode.Tools;
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
    public async Task LargeFile_TriggersGuardrail_ShowsFirst200Lines()
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
        StringAssert.Contains(result.Output, "line 200");
        Assert.IsFalse(result.Output.Contains("line 201"));
    }

    // Test 3: Large file (>40KB) triggers guardrail even if <300 lines
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
        var specialLine = "line 200 München € café résumé naïve";
        var sb = new StringBuilder();
        for (int i = 1; i <= 350; i++)
        {
            if (i > 1) sb.Append('\n');
            if (i == 200)
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
