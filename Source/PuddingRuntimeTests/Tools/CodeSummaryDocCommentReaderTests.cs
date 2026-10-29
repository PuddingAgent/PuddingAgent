using Microsoft.CodeAnalysis.CSharp;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingCodeIndex.Contracts;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Tools;

/// <summary>
/// <see cref="CodeSummaryDocCommentReader"/> 的**直测**。
/// <para>
/// 之前只有"经工具 + 真实文件"的合并用例：快路径返回 null 时会回退到 Roslyn AST，
/// 两条路只要有一条能找到注释就过。于是把快路径的前缀判断改坏，测试**仍然全绿** ——
/// 等于快路径没有覆盖。这里直接调它，把两条路分别钉住。
/// </para>
/// </summary>
[TestClass]
public sealed class CodeSummaryDocCommentReaderTests
{
    private string _root = null!;

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "doc-reader-" + Guid.NewGuid().ToString("N"));
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
    public void FastPath_WithDocComment_ReturnsIt()
    {
        var path = Write("Alpha.cs", """
            namespace Demo;

            /// <summary>Alpha 的文档。</summary>
            public class Alpha { }
            """);

        var doc = CodeSummaryDocCommentReader.ExtractDocCommentFast(path, startLine: 4);

        Assert.IsNotNull(doc);
        StringAssert.Contains(doc, "Alpha 的文档");
    }

    [TestMethod]
    public void FastPath_WithoutDocComment_ReturnsNull()
    {
        // 返回 null 不是失败：它表示"快路径没找到"，调用方据此回退到 AST。
        // 这条同时守住"别为了好看硬返回空串"——空串会让回退永远不发生。
        var path = Write("Beta.cs", """
            namespace Demo;

            public class Beta { }
            """);

        Assert.IsNull(CodeSummaryDocCommentReader.ExtractDocCommentFast(path, startLine: 3));
    }

    [TestMethod]
    public void AstPath_WithDocComment_ReturnsIt()
    {
        var source = """
            namespace Demo;

            /// <summary>Gamma 的文档。</summary>
            public class Gamma { }
            """;
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var symbol = Symbol("Gamma", 4);

        var doc = CodeSummaryDocCommentReader.ExtractDocComment(root, symbol);

        Assert.IsNotNull(doc);
        StringAssert.Contains(doc, "Gamma 的文档");
    }

    [TestMethod]
    public void AstPath_WithoutDocComment_ReturnsNull()
    {
        var source = """
            namespace Demo;

            public class Delta { }
            """;
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();

        Assert.IsNull(CodeSummaryDocCommentReader.ExtractDocComment(root, Symbol("Delta", 3)));
    }

    private string Write(string name, string content)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static CodeSymbolRecord Symbol(string name, int startLine) =>
        new("ws-1", "proj-1", @"D:\repo\x.cs", "sym-1", name, CodeSymbolKind.Class, startLine, startLine + 3);
}
