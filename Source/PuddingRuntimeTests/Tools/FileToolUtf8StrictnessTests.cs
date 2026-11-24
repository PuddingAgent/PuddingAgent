using System.Text;
using System.Text.Json;
using PuddingCode.Tools;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Tools;

/// <summary>
/// 守卫 file_write(file) / file_patch 的编码契约：当目标文件不是合法 UTF-8 时必须**拒绝**。
///
/// 动机（现场实测的静默数据销毁）：这两个工具都是「整文件读入 → 改 → 整文件重写」，
/// 旧实现用 <c>Encoding.UTF8</c> 宽松解码，于是文件里的非法字节先被替换成 U+FFFD、再落盘，
/// 工具回报成功。实测把 <c>41 42 FF 43 44</c> 追加一个 "Z" 后变成
/// <c>41 42 EF BF BD 43 44 5A</c> —— 单个 0xFF 被永久替换成三字节替换字符，且无任何提示。
///
/// 因此断言分两半：拒绝（Success=false）**且**原文件字节未被改动。
/// 另配正向对照：「合法 UTF-8 + 同样操作」必须照常成功 —— 否则一个「拒绝一切写入」的
/// 实现同样能通过负向断言，测试就是假门禁。
/// </summary>
[TestClass]
public sealed class FileToolUtf8StrictnessTests
{
    private string _tempDir = null!;
    private string _originalRepoRoot = null!;

    /// <summary>合法 UTF-8 前缀 + 单个非法字节 0xFF + 合法后缀。</summary>
    private static readonly byte[] InvalidUtf8Bytes = [0x41, 0x42, 0xFF, 0x43, 0x44];

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"pudding-utf8-{Guid.NewGuid():N}");
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

    // ── file_write append ──

    [TestMethod]
    public async Task FileWrite_Append_RefusesNonUtf8File_AndLeavesBytesUntouched()
    {
        var path = Path.Combine(_tempDir, "legacy.txt");
        File.WriteAllBytes(path, InvalidUtf8Bytes);

        var result = await ExecuteAsync(new FileWriteTool(), new Dictionary<string, object?>
        {
            ["path"] = "legacy.txt",
            ["content"] = "Z",
            ["append"] = true,
        });

        Assert.IsFalse(result.Success, "appending to a non-UTF-8 file must fail rather than mangle it");
        StringAssert.Contains(result.Error, "UTF-8");
        CollectionAssert.AreEqual(InvalidUtf8Bytes, File.ReadAllBytes(path),
            "a refused append must not touch a single byte of the original file");
    }

    [TestMethod]
    public async Task FileWrite_Append_StillWorksOnValidUtf8()
    {
        var path = Path.Combine(_tempDir, "plain.txt");
        File.WriteAllText(path, "AB", new UTF8Encoding(false));

        var result = await ExecuteAsync(new FileWriteTool(), new Dictionary<string, object?>
        {
            ["path"] = "plain.txt",
            ["content"] = "Z",
            ["append"] = true,
        });

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual("ABZ", File.ReadAllText(path, Encoding.UTF8));
        // 顺带守住「不写 BOM」：追加不得改变文件头。
        Assert.AreNotEqual(0xEF, File.ReadAllBytes(path)[0], "append must not introduce a UTF-8 BOM");
    }

    // ── file_patch ──

    [TestMethod]
    public async Task FilePatch_RefusesNonUtf8File_AndLeavesBytesUntouched()
    {
        var path = Path.Combine(_tempDir, "legacy.txt");
        File.WriteAllBytes(path, InvalidUtf8Bytes);

        var result = await ExecuteAsync(new FilePatchTool(), new Dictionary<string, object?>
        {
            ["path"] = "legacy.txt",
            ["operations"] = new object[]
            {
                // 宽松解码下 "AB" 是能匹配到的 —— 正因如此旧实现才会「成功」地毁掉 0xFF。
                new Dictionary<string, object?> { ["type"] = "replace", ["old_text"] = "AB", ["new_text"] = "XY" },
            },
        });

        Assert.IsFalse(result.Success, "patching a non-UTF-8 file must fail rather than mangle it");
        StringAssert.Contains(result.Error, "UTF-8");
        CollectionAssert.AreEqual(InvalidUtf8Bytes, File.ReadAllBytes(path),
            "a refused patch must not touch a single byte of the original file");
    }

    [TestMethod]
    public async Task FilePatch_StillWorksOnValidUtf8()
    {
        var path = Path.Combine(_tempDir, "plain.txt");
        File.WriteAllText(path, "AB", new UTF8Encoding(false));

        var result = await ExecuteAsync(new FilePatchTool(), new Dictionary<string, object?>
        {
            ["path"] = "plain.txt",
            ["operations"] = new object[]
            {
                new Dictionary<string, object?> { ["type"] = "replace", ["old_text"] = "AB", ["new_text"] = "XY" },
            },
        });

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual("XY", File.ReadAllText(path, Encoding.UTF8));
    }

    // ── Helper ──

    private static Task<ToolExecutionResult> ExecuteAsync<TArgs>(
        PuddingToolBase<TArgs> tool, IReadOnlyDictionary<string, object?> parameters)
        where TArgs : class
    {
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
