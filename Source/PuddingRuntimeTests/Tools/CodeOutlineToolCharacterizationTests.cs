using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingCode.Tools;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Tools;

/// <summary>
/// <c>code_outline</c> 的**特征测试**（characterization tests，2026-10-29 补）。
/// <para>
/// 目的不是"验证设计"，而是<b>先把现有行为钉住</b>。这个工具即将被抽取成
/// <c>ICodeOutlineService</c>（见 <c>Docs/12_features/workspace_search能力映射-2026-10-28.md</c> §6.3）：
/// 没有这一层，"抽取"就只是"改完之后还能跑"，无法证明<b>行为没变</b>。
/// </para>
/// <para>
/// 这里断言的都是**外部可见**的东西：成功与否、错误信息里的关键措辞、输出 JSON 的顶层字段。
/// 内部实现（Roslyn 还是 outliner、树形文本的缩进字符）一概不锁 —— 那些正是抽取时可以动的部分。
/// </para>
/// </summary>
[TestClass]
public sealed class CodeOutlineToolCharacterizationTests
{
    private string _root = null!;

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "outline-char-" + Guid.NewGuid().ToString("N"));
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
    public async Task CSharpFile_ReturnsSymbolsAndTree()
    {
        var file = Write("Sample.cs", """
            namespace Demo;

            public class Outer
            {
                public void Alpha() { }
            }
            """);

        var result = await RunAsync(file);

        Assert.IsTrue(result.Success, result.Error);
        using var doc = JsonDocument.Parse(result.Output!);
        var root = doc.RootElement;

        Assert.AreEqual(file, root.GetProperty("file_path").GetString());
        Assert.IsFalse(root.GetProperty("has_syntax_errors").GetBoolean());
        Assert.IsTrue(root.GetProperty("count").GetInt32() >= 1);
        Assert.IsTrue(root.GetProperty("symbols").GetArrayLength() >= 1);
        StringAssert.Contains(root.GetProperty("tree").GetString(), "File: ");
    }

    [TestMethod]
    public async Task MissingFile_Fails()
    {
        var result = await RunAsync(Path.Combine(_root, "nope.cs"));

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "文件不存在");
    }

    [TestMethod]
    public async Task UnsupportedExtension_FailsWithoutOutliner()
    {
        var file = Write("data.xyz", "whatever");

        var result = await RunAsync(file);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "不支持此文件类型");
    }

    [TestMethod]
    public async Task CSharpWithoutTypeDeclarations_Fails()
    {
        var file = Write("OnlyUsings.cs", """
            using System;
            using System.Text;
            """);

        var result = await RunAsync(file);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "没有发现 C# 类型声明");
    }

    private string Write(string name, string content)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static Task<ToolExecutionResult> RunAsync(string absolutePath)
    {
        var tool = new CodeOutlineTool();
        var arguments = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["file_path"] = absolutePath,
        });

        return tool.ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "outline-characterization",
            ArgumentsJson = arguments,
            Context = new ToolExecutionContext
            {
                WorkspaceId = "workspace",
                SessionId = "session",
                AgentInstanceId = "agent",
            },
        });
    }
}
