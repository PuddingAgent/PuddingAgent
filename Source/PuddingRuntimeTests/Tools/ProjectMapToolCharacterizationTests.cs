using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingCode.Tools;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Tools;

/// <summary>
/// <c>project_map</c> 的**特征测试**（抽取前先钉行为）。
/// <para>
/// 全仓此前对它零测试。覆盖三条可判定分支：目录不存在、正常出树、depth 越界被夹取。
/// 刻意<b>不锁</b>树形文本的缩进字符与节点排序 —— 那些是抽取时可能动的部分。
/// </para>
/// </summary>
[TestClass]
public sealed class ProjectMapToolCharacterizationTests
{
    private string _root = null!;

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "project-map-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // 清理失败不影响断言结论。
        }
    }

    [TestMethod]
    public async Task MissingDirectory_ReportsDirectoryNotFoundWithResolvedPath()
    {
        var missing = Path.Combine(_root, "nope");

        var result = await RunAsync(missing, depth: null);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "目录不存在");
        StringAssert.Contains(result.Error, "nope");
    }

    [TestMethod]
    public async Task SmallTree_ReturnsTextPlusJsonWithTree()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        await File.WriteAllTextAsync(Path.Combine(_root, "src", "A.cs"), "public class A { }");

        var result = await RunAsync(_root, depth: 2);

        Assert.IsTrue(result.Success, result.Error);

        var jsonPart = SplitJson(result.Output!);
        using var doc = JsonDocument.Parse(jsonPart);
        var root = doc.RootElement;

        Assert.AreEqual(_root, root.GetProperty("path").GetString());
        Assert.AreEqual(2, root.GetProperty("depth").GetInt32());
        Assert.AreNotEqual(JsonValueKind.Null, root.GetProperty("tree").ValueKind);

        // 人读文本里必须能看到子目录名（否则"看项目结构"这件事没成立）。
        StringAssert.Contains(result.Output, "src");
    }

    [TestMethod]
    public async Task OutOfRangeDepth_IsClampedToThree()
    {
        var result = await RunAsync(_root, depth: 9);

        Assert.IsTrue(result.Success, result.Error);
        using var doc = JsonDocument.Parse(SplitJson(result.Output!));
        Assert.AreEqual(3, doc.RootElement.GetProperty("depth").GetInt32(), "depth 上限是 3，越界要夹取而不是透传。");
    }

    [TestMethod]
    public async Task ZeroDepth_IsClampedToOne()
    {
        var result = await RunAsync(_root, depth: 0);

        Assert.IsTrue(result.Success, result.Error);
        using var doc = JsonDocument.Parse(SplitJson(result.Output!));
        Assert.AreEqual(1, doc.RootElement.GetProperty("depth").GetInt32(), "depth 下限是 1。");
    }

    private static Task<ToolExecutionResult> RunAsync(string? projectPath, int? depth)
    {
        var tool = new ProjectMapTool();
        var arguments = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["project_path"] = projectPath,
            ["depth"] = depth,
        });

        return tool.ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "project-map-characterization",
            ArgumentsJson = arguments,
            Context = new ToolExecutionContext
            {
                WorkspaceId = "ws-1",
                SessionId = "session",
                AgentInstanceId = "agent",
                WorkingDirectory = projectPath,
            },
        });
    }

    private static string SplitJson(string output)
    {
        var index = output.LastIndexOf("---", StringComparison.Ordinal);
        Assert.IsTrue(index >= 0, "输出里必须有分隔线。");
        return output[(index + 3)..];
    }
}
