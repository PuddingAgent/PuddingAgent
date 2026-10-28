using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Tools;

/// <summary>
/// <see cref="CodeOutlineTargetResolver"/> 的单元测试。
/// <para>
/// 重点不是"能解析路径"，而是<b>把三条容易被后续重构悄悄改掉的语义钉住</b>：
/// ①回显用用户原样的路径、②相对路径以工作目录为基准、③绝对路径不做工作区子树校验
/// （旧 XML 注释声称做了，实际没有 —— 这里按<b>实际行为</b>断言，并在注释里写明这不是安全承诺）。
/// </para>
/// </summary>
[TestClass]
public sealed class CodeOutlineTargetResolverTests
{
    private string _root = null!;

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "outline-target-" + Guid.NewGuid().ToString("N"));
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
    public void EmptyPath_ReturnsError()
    {
        var target = CodeOutlineTargetResolver.Resolve("   ", _root, out var error);

        Assert.IsNull(target);
        Assert.AreEqual("file_path is required.", error);
    }

    [TestMethod]
    public void RelativePath_ResolvesAgainstWorkingDirectory_AndKeepsDisplayPath()
    {
        var full = Path.Combine(_root, "Sample.cs");
        File.WriteAllText(full, "public class A { }");

        var target = CodeOutlineTargetResolver.Resolve("Sample.cs", _root, out var error);

        Assert.IsNull(error);
        Assert.IsNotNull(target);
        Assert.AreEqual(full, target!.FullPath);
        Assert.AreEqual("Sample.cs", target.DisplayPath, "回显必须是用户原样给的路径，不能换成绝对路径。");
        Assert.AreEqual(".cs", target.Extension);
    }

    [TestMethod]
    public void AbsolutePathOutsideWorkingDirectory_IsAccepted()
    {
        // 这条是**特征断言**：旧实现没有工作区子树校验。若将来真要加沙箱，
        // 会先让这条红掉，从而逼出"这是行为变更"而不是悄悄变严格。
        var full = Path.Combine(_root, "Outside.cs");
        File.WriteAllText(full, "public class B { }");
        var otherCwd = Path.Combine(_root, "cwd");
        Directory.CreateDirectory(otherCwd);

        var target = CodeOutlineTargetResolver.Resolve(full, otherCwd, out var error);

        Assert.IsNull(error);
        Assert.IsNotNull(target);
        Assert.AreEqual(full, target!.FullPath);
    }

    [TestMethod]
    public void MissingFile_ReportsDisplayPathNotAbsolute()
    {
        var target = CodeOutlineTargetResolver.Resolve("nope.cs", _root, out var error);

        Assert.IsNull(target);
        StringAssert.Contains(error, "nope.cs");
        StringAssert.Contains(error, "文件不存在");
    }

    [TestMethod]
    public void Extension_IsLowerCased_AndEmptyWhenAbsent()
    {
        File.WriteAllText(Path.Combine(_root, "MiXeD.CS"), "public class C { }");
        File.WriteAllText(Path.Combine(_root, "noext"), "x");

        Assert.AreEqual(".cs", CodeOutlineTargetResolver.Resolve("MiXeD.CS", _root, out _)!.Extension);
        Assert.AreEqual(string.Empty, CodeOutlineTargetResolver.Resolve("noext", _root, out _)!.Extension);
    }
}
