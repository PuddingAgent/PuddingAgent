using PuddingRuntime.Services;

namespace PuddingRuntimeTests.Services;

/// <summary>
/// 「我在哪里」契约测试：WORKSPACE ENVIRONMENT 层必须把本次执行的实际工作目录
/// 告诉模型，否则它只能自己探测（实测 GPT 子代理为此用了 4 次 shell）。
///
/// 取红方式：去掉 WorkingDirectory 分支，前两条用例立即失败。
/// </summary>
[TestClass]
public sealed class WorkspaceEnvironmentLayerTests
{
    [TestMethod]
    public void WorkingDirectory_DistinctFromRoot_IsRendered()
    {
        var text = ContextPipeline.BuildWorkspaceEnvironmentText(
            @"D:\Data\workspaces\default",
            @"D:\CodeProject\PuddingAgent\PuddingAgent");

        StringAssert.Contains(text, @"WorkspaceRoot: D:\Data\workspaces\default");
        StringAssert.Contains(text, @"WorkingDirectory: D:\CodeProject\PuddingAgent\PuddingAgent");
        // 必须说明它是文件工具的实际根目录，否则模型不知道相对路径按谁解析
        StringAssert.Contains(text, "相对路径按它解析");
    }

    [TestMethod]
    public void WorkingDirectory_SameAsRoot_IsOmitted()
    {
        var text = ContextPipeline.BuildWorkspaceEnvironmentText(
            @"D:\Data\workspaces\default",
            @"D:\Data\workspaces\default");

        Assert.IsFalse(text.Contains("WorkingDirectory:"),
            "同值时再写一行只是白花 token，应当省略");
    }

    [TestMethod]
    public void WorkingDirectory_Null_KeepsLegacyOutput()
    {
        var text = ContextPipeline.BuildWorkspaceEnvironmentText(
            @"D:\Data\workspaces\default",
            null);

        StringAssert.Contains(text, "WorkspaceRoot:");
        Assert.IsFalse(text.Contains("WorkingDirectory:"));
    }

    [TestMethod]
    public void WorkspaceRootUnavailable_StillReportsWorkingDirectory()
    {
        // workspaceRoot 拿不到时，workingDirectory 反而是唯一的位置线索，不能一起丢。
        var text = ContextPipeline.BuildWorkspaceEnvironmentText(
            null,
            @"D:\CodeProject\PuddingAgent\PuddingAgent");

        StringAssert.Contains(text, "WorkspaceRoot: unavailable");
        StringAssert.Contains(text, "WorkingDirectory: D:\\CodeProject\\PuddingAgent\\PuddingAgent");
    }
}
