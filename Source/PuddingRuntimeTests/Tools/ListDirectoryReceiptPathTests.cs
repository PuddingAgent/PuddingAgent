using System.Text.Json;
using PuddingCode.Tools;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Tools;

/// <summary>
/// <c>list_dir</c> 回执里的 <c>Path</c> 必须能被调用方定位。
/// 越出执行根时若仍给相对路径，调用方拿到的是 <c>..\..\..\CodeProject\…</c> —— 只能自己数层数
/// （2026-11-26 运行时实测）。与 <c>file_write</c> / <c>file_patch</c> 用同一个渲染器后，
/// 工作区内仍是相对路径、越界退回绝对路径，两侧都钉住。
/// </summary>
[TestClass]
public sealed class ListDirectoryReceiptPathTests : IDisposable
{
    private readonly string _baseDir;
    private readonly string _root;
    private readonly string _outside;

    public ListDirectoryReceiptPathTests()
    {
        _baseDir = Path.Combine(Path.GetTempPath(), "pudding-listdir-receipt", Guid.NewGuid().ToString("N"));
        _root = Path.Combine(_baseDir, "workspace");
        _outside = Path.Combine(_baseDir, "outside");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_outside);
    }

    public void Dispose()
    {
        try { Directory.Delete(_baseDir, recursive: true); } catch { /* best effort */ }
    }

    [TestMethod]
    public async Task PathInsideExecutionRoot_IsRenderedRelative()
    {
        Directory.CreateDirectory(Path.Combine(_root, "inner"));

        var path = await ListAsync("inner");

        Assert.IsFalse(
            Path.IsPathRooted(path),
            "工作区内的回执路径应保持相对，避免把绝对路径刷进模型历史；实际: " + path);
        Assert.AreEqual(
            "inner",
            path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            "工作区内的回执路径应相对执行根");
    }

    [TestMethod]
    public async Task PathEscapingExecutionRoot_FallsBackToAbsolute()
    {
        var path = await ListAsync(_outside);

        Assert.IsTrue(
            Path.IsPathRooted(path),
            "越界时必须给绝对路径，实际: " + path);
        Assert.IsFalse(
            path.StartsWith("..", StringComparison.Ordinal),
            "不得给调用方无法定位的上级相对路径，实际: " + path);
    }

    private async Task<string> ListAsync(string target)
    {
        var tool = new ListDirectoryTool();
        var result = await tool.ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "listdir-receipt",
            ArgumentsJson = JsonSerializer.Serialize(new { path = target }),
            Context = new ToolExecutionContext
            {
                WorkspaceId = "test",
                SessionId = "test",
                AgentInstanceId = "test",
                WorkingDirectory = _root,
                IsYoloMode = true,
            },
        });

        Assert.IsTrue(result.Success, result.Error);
        using var doc = JsonDocument.Parse(result.Output!);
        return doc.RootElement.GetProperty("Path").GetString()!;
    }
}
