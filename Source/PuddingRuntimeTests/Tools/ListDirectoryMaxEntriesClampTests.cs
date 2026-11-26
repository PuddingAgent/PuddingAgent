using System.Text.Json;
using PuddingCode.Tools;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Tools;

/// <summary>
/// <c>list_dir</c> 对 <c>max_entries</c> 做两端夹取，**两端都要在回执里说明**。
/// 超硬顶那一侧早有说明（描述里写着「把 max_entries 调大再列一次」，而超过 2000 已做不到）；
/// 低于下限那一侧原本静默：调用要 0 条，实际拿到 1 条，回执里看不出来。
/// </summary>
[TestClass]
public sealed class ListDirectoryMaxEntriesClampTests : IDisposable
{
    private readonly string _root;

    public ListDirectoryMaxEntriesClampTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "pudding-listdir-clamp", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        for (var i = 1; i <= 3; i++)
            File.WriteAllText(Path.Combine(_root, $"f{i}.txt"), "x");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [TestMethod]
    public async Task MaxEntriesBelowFloor_IsClampedToOneAndExplained()
    {
        var result = await ListAsync(maxEntries: 0);

        Assert.AreEqual(1, result.Count, "低于下限的请求会被夹取到 1 条，回执不得暗示返回了 0 条");
        Assert.IsTrue(result.Truncated, "只返回 1 条而目录里有 3 个文件，必须标 Truncated");
        Assert.IsNotNull(result.MaxEntriesNotice, "夹取到下限时必须在 MaxEntriesNotice 里说明，否则是静默增量");
        StringAssert.Contains(
            result.MaxEntriesNotice!,
            "below the minimum 1",
            "提示要点明是被下限夹取，而不是超上限；实际: " + result.MaxEntriesNotice);
    }

    [TestMethod]
    public async Task MaxEntriesWithinRange_HasNoNotice()
    {
        var result = await ListAsync(maxEntries: 2);

        Assert.AreEqual(2, result.Count);
        Assert.IsNull(
            result.MaxEntriesNotice,
            "区间内的请求没有被夹取，不得输出提示；实际: " + result.MaxEntriesNotice);
    }

    private async Task<(int Count, bool Truncated, string? MaxEntriesNotice)> ListAsync(int maxEntries)
    {
        var tool = new ListDirectoryTool();
        var result = await tool.ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "listdir-clamp",
            ArgumentsJson = JsonSerializer.Serialize(new { path = _root, max_entries = maxEntries }),
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
        var root = doc.RootElement;
        var count = root.GetProperty("Count").GetInt32();
        var truncated = root.GetProperty("Truncated").GetBoolean();
        var notice = root.TryGetProperty("MaxEntriesNotice", out var n) ? n.GetString() : null;
        return (count, truncated, notice);
    }
}
