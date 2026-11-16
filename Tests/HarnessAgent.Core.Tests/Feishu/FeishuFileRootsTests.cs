using HarnessAgent.Core.Connectors.Feishu;

namespace HarnessAgent.Core.Tests.Feishu;

/// <summary>
/// 允许根的解析契约。
///
/// <para>
/// 这里刻意不碰磁盘、不读真实环境变量：只验证「给定输入产出哪些根」。
/// 覆盖 <see cref="FeishuFileRoots.OverrideVariable"/> 的语义是**替换**而不是追加——
/// 若改成追加，运营方想收紧范围时默认根会悄悄留着，收紧就失效了。
/// </para>
/// </summary>
[TestClass]
public sealed class FeishuFileRootsTests
{
    private const string DataRoot = @"D:\Data";
    private const string WorkspacesRoot = @"D:\Data\workspaces";

    [TestMethod]
    public void Default_Roots_Cover_The_Workspace_And_The_Output_Folder()
    {
        var roots = FeishuFileRoots.Resolve(
            DataRoot,
            WorkspacesRoot,
            "default",
            overrideValue: null);

        Assert.AreEqual(2, roots.Count);
        Assert.AreEqual(@"D:\Data\workspaces\default", roots[0]);
        Assert.AreEqual(@"D:\Data\output", roots[1]);
    }

    [TestMethod]
    public void Workspace_Root_Is_Omitted_When_Workspace_Id_Is_Missing()
    {
        var roots = FeishuFileRoots.Resolve(
            DataRoot,
            WorkspacesRoot,
            workspaceId: null,
            overrideValue: null);

        CollectionAssert.AreEqual(
            new[] { @"D:\Data\output" },
            roots.ToArray());
    }

    [TestMethod]
    public void Override_Replaces_The_Defaults_Instead_Of_Appending()
    {
        var roots = FeishuFileRoots.Resolve(
            DataRoot,
            WorkspacesRoot,
            "default",
            @"C:\exports; D:\shared ");

        CollectionAssert.AreEqual(
            new[] { @"C:\exports", @"D:\shared" },
            roots.ToArray());
        Assert.IsFalse(roots.Contains(@"D:\Data\output"));
        Assert.IsFalse(roots.Contains(@"D:\Data\workspaces\default"));
    }

    [TestMethod]
    public void Blank_Override_Falls_Back_To_The_Defaults()
    {
        var roots = FeishuFileRoots.Resolve(
            DataRoot,
            WorkspacesRoot,
            "default",
            "  ;  ");

        Assert.AreEqual(2, roots.Count);
        Assert.AreEqual(@"D:\Data\output", roots[1]);
    }

    [TestMethod]
    public void Override_Variable_Name_Is_The_documented_One()
    {
        // 变量名是外部契约（部署脚本与文档都引它），改名必须同时改这些地方。
        Assert.AreEqual("PUDDING_FEISHU_FILE_ROOTS", FeishuFileRoots.OverrideVariable);
    }
}
