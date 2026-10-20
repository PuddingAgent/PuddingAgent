using PuddingCore.Documents;

namespace PuddingCoreTests.Documents;

/// <summary>
/// <see cref="DocumentFileReader"/> 的行为断言。用真实临时目录，因为这一层存在的意义
/// 就是"在真实文件系统上不越界" —— 完全 mock 掉 File 只会测到自己的假设。
/// </summary>
[TestClass]
public sealed class DocumentFileReaderTests
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "pudding-docreader-" + Guid.NewGuid().ToString("N"));

    [TestInitialize]
    public void Initialize() => Directory.CreateDirectory(_root);

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 清理失败不影响断言 */ }
    }

    private void Write(string relativePath, string content)
    {
        var full = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    [TestMethod]
    public void Reads_text_and_reports_disk_size_in_bytes()
    {
        Write("Docs/a.md", "# 标题\n\n正文");

        var ok = DocumentFileReader.TryRead(_root, "Docs/a.md", out var file, out var code, out _);

        Assert.IsTrue(ok);
        Assert.IsNull(code);
        Assert.IsNotNull(file);
        Assert.AreEqual("a.md", file!.Name);
        Assert.AreEqual("# 标题\n\n正文", file.Content);
        // 字节数不是字符数：中文 3 字节，两者必须区分开。
        Assert.IsTrue(file.SizeBytes > file.Content.Length);
    }

    [TestMethod]
    public void Rejects_traversal_before_touching_the_filesystem()
    {
        var ok = DocumentFileReader.TryRead(_root, "../outside.md", out var file, out var code, out _);

        Assert.IsFalse(ok);
        Assert.IsNull(file);
        Assert.AreEqual(DocumentPathErrors.InvalidPath, code);
    }

    [TestMethod]
    public void Missing_file_reports_not_found()
    {
        var ok = DocumentFileReader.TryRead(_root, "Docs/none.md", out var file, out var code, out _);

        Assert.IsFalse(ok);
        Assert.IsNull(file);
        Assert.AreEqual(DocumentPathErrors.NotFound, code);
    }

    [TestMethod]
    public void Unsupported_extension_is_rejected()
    {
        Write("Docs/a.pdf", "x");

        var ok = DocumentFileReader.TryRead(_root, "Docs/a.pdf", out _, out var code, out _);

        Assert.IsFalse(ok);
        Assert.AreEqual(DocumentPathErrors.UnsupportedType, code);
    }

    [TestMethod]
    public void Oversized_file_is_rejected_rather_than_truncated()
    {
        // 上限 +1 字节就够触发；写满 2 MiB 只会让测试变慢。
        var content = new string('a', DocumentPathPolicy.MaxContentBytes + 1);
        Write("Docs/big.md", content);

        var ok = DocumentFileReader.TryRead(_root, "Docs/big.md", out var file, out var code, out _);

        Assert.IsFalse(ok);
        Assert.IsNull(file);
        Assert.AreEqual(DocumentPathErrors.TooLarge, code);
    }

    [TestMethod]
    public void Sibling_directory_with_same_prefix_is_not_inside_root()
    {
        // 裸前缀比较会把 …\pudding-docreader-xEvil 当成根内；带分隔符复核必须拒绝。
        var evil = _root + "Evil";
        Directory.CreateDirectory(evil);
        try
        {
            File.WriteAllText(Path.Combine(evil, "a.md"), "secret");

            // 用相对路径逃出根、再进同名前缀目录。
            var ok = DocumentFileReader.TryRead(
                _root,
                "../" + Path.GetFileName(evil) + "/a.md",
                out var file,
                out var code,
                out _);

            Assert.IsFalse(ok);
            Assert.IsNull(file);
            Assert.AreEqual(DocumentPathErrors.InvalidPath, code);
        }
        finally
        {
            try { Directory.Delete(evil, recursive: true); } catch { }
        }
    }

    [TestMethod]
    public void Backslash_path_is_normalized()
    {
        Write("Docs/b.md", "x");

        var ok = DocumentFileReader.TryRead(_root, @"Docs\b.md", out var file, out _, out _);

        Assert.IsTrue(ok);
        Assert.AreEqual("b.md", file!.Name);
    }
}
