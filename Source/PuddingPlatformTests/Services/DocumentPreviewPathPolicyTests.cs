using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

/// <summary>
/// <see cref="DocumentPreviewPathPolicy"/> 的路径安全策略断言。
/// <para>
/// 每个用例对应一条"读到根目录之外"或"读到不该读的类型"的具体手法。
/// 这些手法在 HTTP 层难以稳定复现（框架对 URL 编码、大小写盘符处理不一致），
/// 所以把判定逻辑抽成纯函数后在这里逐条钉死。
/// </para>
/// </summary>
[TestClass]
public sealed class DocumentPreviewPathPolicyTests
{
    [DataTestMethod]
    [DataRow("Docs/12_features/x.md", "Docs/12_features/x.md")]
    [DataRow("x.markdown", "x.markdown")]
    [DataRow("notes/readme.TXT", "notes/readme.TXT")]
    [DataRow(@"Docs\14_reports\报告.md", "Docs/14_reports/报告.md")]
    [DataRow("  Docs/a.md  ", "Docs/a.md")]
    public void TryNormalize_Accepts_Relative_Text_Paths(string raw, string expected)
    {
        Assert.IsTrue(
            DocumentPreviewPathPolicy.TryNormalize(raw, out var normalized, out var code, out var message),
            $"应当接受：{raw}（code={code} message={message}）");
        Assert.AreEqual(expected, normalized);
    }

    [DataTestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    public void TryNormalize_Rejects_Empty_Path(string? raw)
    {
        Assert.IsFalse(DocumentPreviewPathPolicy.TryNormalize(raw, out _, out var code, out _));
        Assert.AreEqual(DocumentPreviewErrorCodes.InvalidPath, code);
    }

    /// <remarks>
    /// 整体首尾空白是**故意 Trim 掉**的（查询参数易带上空格），所以 `"Docs/a.md "` 属于合法输入；
    /// 真正危险的是**段内**尾随空格（`"Docs /a.md"`），Windows 会把它与 `Docs` 视作同一目录，必须拒绝。
    /// </remarks>
    [DataTestMethod]
    [DataRow("/etc/passwd.md")]
    [DataRow("//server/share/x.md")]
    [DataRow("C:/Users/x.md")]
    [DataRow("c:x.md")]
    [DataRow("Docs/../../secrets.md")]
    [DataRow("Docs/./a.md")]
    [DataRow("Docs//a.md")]
    [DataRow("Docs/a.md::$DATA")]
    [DataRow("Docs /a.md")]
    [DataRow("Docs/a.md.")]
    public void TryNormalize_Rejects_Traversal_And_Absolute_Forms(string raw)
    {
        Assert.IsFalse(
            DocumentPreviewPathPolicy.TryNormalize(raw, out var normalized, out var code, out _),
            $"应当拒绝：{raw}（却规整为 {normalized}）");
        Assert.AreEqual(DocumentPreviewErrorCodes.InvalidPath, code, $"拒绝原因应为 invalid_path：{raw}");
    }

    [DataTestMethod]
    [DataRow("Docs/a.pdf")]
    [DataRow("Docs/a.html")]
    [DataRow("Docs/a.png")]
    [DataRow("Docs/a")]
    public void TryNormalize_Rejects_Unsupported_Extensions(string raw)
    {
        Assert.IsFalse(DocumentPreviewPathPolicy.TryNormalize(raw, out _, out var code, out _));
        Assert.AreEqual(DocumentPreviewErrorCodes.UnsupportedType, code);
    }

    [TestMethod]
    public void TryNormalize_Rejects_Control_Characters()
    {
        Assert.IsFalse(DocumentPreviewPathPolicy.TryNormalize("Docs/a\u0000b.md", out _, out var code, out _));
        Assert.AreEqual(DocumentPreviewErrorCodes.InvalidPath, code);
    }

    [TestMethod]
    public void TryResolveFullPath_Keeps_Path_Under_Root()
    {
        var root = Path.Combine(Path.GetTempPath(), "pudding-docp-root");
        Assert.IsTrue(DocumentPreviewPathPolicy.TryResolveFullPath(root, "Docs/a.md", out var full));
        StringAssert.StartsWith(full, Path.GetFullPath(root));
        StringAssert.EndsWith(full, Path.Combine("Docs", "a.md"));
    }

    [TestMethod]
    public void TryResolveFullPath_Rejects_Sibling_Directory_Sharing_Prefix()
    {
        // 关键负例：root=…\Docs 时，…\DocsEvil\a.md 以 root 字符串为前缀，
        // 只做 StartsWith(root) 会放行。策略里拼了分隔符，这里锁住它。
        var root = Path.Combine(Path.GetTempPath(), "pudding-docs");
        var sibling = Path.Combine(Path.GetTempPath(), "pudding-docsEvil");

        Assert.IsFalse(
            DocumentPreviewPathPolicy.TryResolveFullPath(root, "../pudding-docsEvil/a.md", out _),
            "带分隔符的前缀复核必须拒绝同前缀的兄弟目录");

        Assert.IsTrue(
            DocumentPreviewPathPolicy.TryResolveFullPath(root, "a.md", out var inside),
            "根内文件应通过");
        StringAssert.StartsWith(inside, Path.GetFullPath(root) + Path.DirectorySeparatorChar);
        Assert.IsFalse(
            inside.StartsWith(sibling, StringComparison.OrdinalIgnoreCase),
            "根内解析结果不得落到兄弟目录");
    }

    [TestMethod]
    public void ResolveRoot_Uses_Explicit_Configuration_When_It_Exists()
    {
        var configured = Path.Combine(Path.GetTempPath(), "pudding-docs-configured");
        Directory.CreateDirectory(configured);
        try
        {
            var resolved = DocumentPreviewPathPolicy.ResolveRoot(() => configured);
            Assert.AreEqual(Path.GetFullPath(configured), resolved);
        }
        finally
        {
            Directory.Delete(configured);
        }
    }

    [TestMethod]
    public void ResolveRoot_Returns_Null_When_Configured_Path_Is_Missing()
    {
        var missing = Path.Combine(Path.GetTempPath(), "pudding-docs-does-not-exist-" + Guid.NewGuid().ToString("N"));
        var resolved = DocumentPreviewPathPolicy.ResolveRoot(() => missing);

        // 配置了但目录不存在 ⇒ 不许回退到"向上找仓库"或 cwd，必须 fail-closed。
        Assert.IsNull(resolved);
    }
}
