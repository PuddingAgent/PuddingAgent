using PuddingRuntime.Services.Skills;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Tools;

/// <summary>
/// 工具对常见误用的处置：夹取而不是让整轮报废，把「传了文件」与「路径不存在」区分开。
/// 回归背景（2026-11-13 实测）：terminal_start 因 yield_time_ms=15000 直接抛错，
/// 让一个子代理回合以 failed 收场；search_grep 收到文件路径时只报「目录不存在」，
/// 两次把子代理引向「路径拼错」的错误方向。
/// </summary>
[TestClass]
public sealed class ToolMisuseGuidanceTests
{
    [TestMethod]
    [DataRow(null, 1000)]
    [DataRow(-5, 0)]
    [DataRow(0, 0)]
    [DataRow(500, 500)]
    [DataRow(10000, 10000)]
    [DataRow(15000, 10000)]
    public void ClampYieldTimeMs_KeepsTheCallAliveAndBoundsTheWindow(int? requested, int expected)
    {
        Assert.AreEqual(expected, TerminalStartTool.ClampYieldTimeMs(requested));
    }

    [TestMethod]
    public void InvalidSearchRoot_ExistingDirectory_IsAccepted()
    {
        Assert.IsNull(SearchGrepTool.InvalidSearchRootMessage(Path.GetTempPath()));
    }

    [TestMethod]
    public void InvalidSearchRoot_ExistingFile_IsNamedAsAFile()
    {
        var file = Path.Combine(Path.GetTempPath(), $"pudding-grep-probe-{Guid.NewGuid():N}.cs");
        File.WriteAllText(file, "// probe");
        try
        {
            var message = SearchGrepTool.InvalidSearchRootMessage(file);

            Assert.IsNotNull(message);
            StringAssert.Contains(message, "is a file, not a directory");
        }
        finally
        {
            File.Delete(file);
        }
    }

    [TestMethod]
    public void InvalidSearchRoot_MissingPath_ReportsNotFound()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"pudding-missing-{Guid.NewGuid():N}");

        var message = SearchGrepTool.InvalidSearchRootMessage(missing);

        Assert.IsNotNull(message);
        StringAssert.Contains(message, "not found");
    }
}
