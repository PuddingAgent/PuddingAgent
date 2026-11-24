using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Tools;

/// <summary>
/// 「文件/目录不存在」诊断语的回归用例。
/// <para>
/// 存在动机（2026-11-25 失败归因普查）：<c>file_read</c> 26 条、<c>list_dir</c> 6 条失败的主因是路径
/// 拼错，而原始消息只回显输入路径，模型无法判断它被解析到了哪个根下面。
/// </para>
/// <para>
/// 这些用例只验证**事实是否被说出来**（解析结果、最深已存在祖先、缺失层数），
/// 不验证措辞风格 —— 措辞可以改，事实不能丢。
/// </para>
/// </summary>
[TestClass]
public sealed class MissingPathDiagnosticsTests
{
    private string _root = "";

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "pudding-mpd-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // 清理失败不影响判定。
        }
    }

    [TestMethod]
    public void RelativeInput_ReportsResolvedAbsolutePath()
    {
        var resolved = Path.Combine(_root, "nope.txt");

        var message = MissingPathDiagnostics.Append("File not found: nope.txt", "nope.txt", resolved);

        StringAssert.StartsWith(message, "File not found: nope.txt", "既有前缀必须保留，调用方与测试依赖它");
        StringAssert.Contains(message, "resolved to " + resolved,
            "相对输入被解析过，必须把实际检查的绝对路径说出来");
    }

    [TestMethod]
    public void MissingLevels_ReportsDeepestExistingAncestorAndDepth()
    {
        // 只把 _root 建出来，下面四层都不存在：a/b/c/file.cs
        var resolved = Path.Combine(_root, "a", "b", "c", "file.cs");

        var message = MissingPathDiagnostics.Append("File not found: x", "x", resolved);

        StringAssert.Contains(message, "deepest existing ancestor: " + _root,
            "要把「从哪一层开始错了」摆出来，否则模型只能盲试");
        StringAssert.Contains(message, "4 missing level(s)",
            "缺失层数决定模型是补一层还是换根目录");
    }

    [TestMethod]
    public void AbsoluteInput_DoesNotClaimRelativeResolution()
    {
        var resolved = Path.Combine(_root, "a", "b.txt");

        var message = MissingPathDiagnostics.Append("File not found: x", resolved, resolved);

        Assert.IsFalse(message.Contains("resolved to", StringComparison.Ordinal),
            "输入本来就是绝对路径时，说「resolved to」会误导模型以为有个相对基准");
        StringAssert.Contains(message, "deepest existing ancestor: " + _root);
    }

    [TestMethod]
    public void EmptyResolvedPath_ReturnsMessageUnchanged()
    {
        const string original = "File not found: whatever.cs";

        Assert.AreEqual(original, MissingPathDiagnostics.Append(original, "whatever.cs", ""));
        Assert.AreEqual(original, MissingPathDiagnostics.Append(original, "whatever.cs", "   "));
    }

    [TestMethod]
    public void ExistingPath_ProducesNoExtraDetail()
    {
        var existing = Path.Combine(_root, "real.txt");
        File.WriteAllText(existing, "x");
        const string original = "File not found: real.txt";

        // 绝对输入 + 已存在 ⇒ 没有可补充的事实（祖先 == 解析结果），必须原样返回。
        // 注意：相对输入总会补「resolved to」——那条信息在调用方关心的「没找到」场景里始终成立，
        // 所以本用例用绝对输入来隔离「无话可说时不得凭空加话」这一条。
        Assert.AreEqual(original, MissingPathDiagnostics.Append(original, existing, existing));
    }

    [TestMethod]
    public void UnreachableAncestor_LeavesMessageUsable()
    {
        // 走到盘根仍不存在 ⇒ 没有祖先可报；断言「不抛、前缀仍在」，不假定具体盘符不存在。
        var resolved = Path.Combine(_root, "a") + Path.DirectorySeparatorChar + "b.cs";
        var message = MissingPathDiagnostics.Append("Directory not found: z", "z", resolved);

        StringAssert.StartsWith(message, "Directory not found: z");
    }
}
