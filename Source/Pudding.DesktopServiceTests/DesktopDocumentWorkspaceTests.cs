using Pudding.DesktopService;

namespace DesktopServiceTests;

/// <summary>
/// Desktop 侧文档路径解析与读取：**纵深防御**的那一层。
///
/// Core 已经用 DocumentPathPolicy 拦过一次，但 Desktop 不能假设上游一定对
/// （另一条传输、旧版本 Core、被改过的请求）。这里的用例钉住两件事：
/// ① 每种拒绝都有**可区分的原因码**（上层才能如实说明为什么不行）；
/// ② 解析结果永远落在根目录内 —— 这是最后一道闸。
/// </summary>
public sealed class DesktopDocumentWorkspaceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "pudding-preview-" + Guid.NewGuid().ToString("N"));

    public DesktopDocumentWorkspaceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响断言结果。
        }
    }

    [Fact]
    public void Resolve_NormalizesSeparatorsAndDotSegments()
    {
        var workspace = new DesktopDocumentWorkspace(_root);

        Assert.True(workspace.TryResolve(@"Docs\a.md", out var backslash, out _, out _));
        Assert.Equal(Path.Combine(_root, "Docs", "a.md"), backslash);

        Assert.True(workspace.TryResolve("./Docs/./b.txt", out var dotted, out _, out _));
        Assert.Equal(Path.Combine(_root, "Docs", "b.txt"), dotted);
    }

    [Theory]
    [InlineData("../escape.md", "path_traversal")]
    [InlineData("Docs/../a.md", "path_traversal")]
    [InlineData("/etc/passwd.md", "path_absolute")]
    [InlineData("C:/secret.md", "path_absolute")]
    [InlineData("Docs/a.md::$DATA", "path_absolute")]
    [InlineData("Docs/a.pdf", "path_unsupported_extension")]
    [InlineData("", "path_empty")]
    [InlineData("   ", "path_empty")]
    public void Resolve_RejectsSuspiciousPathsWithDistinctCodes(string path, string expectedCode)
    {
        var workspace = new DesktopDocumentWorkspace(_root);

        Assert.False(workspace.TryResolve(path, out var fullPath, out var errorCode, out var errorMessage));

        Assert.Null(fullPath);
        Assert.Equal(expectedCode, errorCode);
        Assert.False(string.IsNullOrWhiteSpace(errorMessage));
    }

    [Fact]
    public void Resolve_KeepsResultInsideRootEvenWithNestedTraversal()
    {
        var workspace = new DesktopDocumentWorkspace(_root);

        // 已经用 .. 逃出去的形式全部拒绝；合法深路径解析后仍在根内。
        Assert.True(workspace.TryResolve("a/b/c/deep.md", out var deep, out _, out _));
        Assert.StartsWith(workspace.RootDirectory, deep, StringComparison.OrdinalIgnoreCase);

        Assert.False(workspace.TryResolve("a/../../../outside.md", out _, out var code, out _));
        Assert.Equal("path_traversal", code);
    }

    [Fact]
    public void Constructor_RejectsRelativeRoot()
    {
        Assert.Throws<ArgumentException>(() => new DesktopDocumentWorkspace("relative/root"));
        Assert.Throws<ArgumentException>(() => new DesktopDocumentWorkspace(""));
    }

    [Fact]
    public void Read_ReturnsTextAndFlagsTruncation()
    {
        var workspace = new DesktopDocumentWorkspace(_root);

        var small = Path.Combine(_root, "small.md");
        File.WriteAllText(small, "# 标题\n正文");
        Assert.True(workspace.TryReadText(small, out var text, out var truncated, out _, out _));
        Assert.Equal("# 标题\n正文", text);
        Assert.False(truncated);

        var big = Path.Combine(_root, "big.md");
        File.WriteAllText(big, new string('x', DesktopDocumentWorkspace.MaxPreviewCharacters + 10));
        Assert.True(workspace.TryReadText(big, out var bigText, out var bigTruncated, out _, out _));
        Assert.Equal(DesktopDocumentWorkspace.MaxPreviewCharacters, bigText!.Length);
        // 截断是如实标注的结果，不是错误。
        Assert.True(bigTruncated);
    }

    [Fact]
    public void Read_MissingFileFailsWithDistinctCode()
    {
        var workspace = new DesktopDocumentWorkspace(_root);

        Assert.False(workspace.TryReadText(
            Path.Combine(_root, "nope.md"), out var text, out _, out var errorCode, out _));

        Assert.Null(text);
        Assert.Equal("file_not_found", errorCode);
    }
}
