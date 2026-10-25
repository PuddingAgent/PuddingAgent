using System.Text.Json;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Services;

/// <summary>
/// L1 摘要级工具定义（ToolSummaryTier）回归：
/// 只保留「做什么（首句）+ 参数名 + 必填」，丢掉逐参说明与 RawJsonSchema。
/// 关键不变量：**幂等**——重复压缩不得再变形，否则每轮都会改写工具块字节、
/// 让前缀缓存反复失效（见 Docs/11_design/工具三级披露与动态加载-2026-10-25.md §4）。
/// </summary>
[TestClass]
public sealed class ToolSummaryTierTests
{
    [TestMethod]
    public void Apply_KeepsNamesAndRequired_DropsParameterDescriptionsAndRawSchema()
    {
        using var document = JsonDocument.Parse("""{"type":"object","properties":{"path":{"type":"string"}}}""");
        var tool = new LlmToolDefinition
        {
            Name = "file_read",
            Description = "读取文件内容。支持 offset/limit，路径必须是绝对路径。",
            Parameters = new ToolParameterSchema(
                [
                    new ToolParameter("path", "string", "文件绝对路径，必填"),
                    new ToolParameter("offset", "integer", "起始行，从 0 开始"),
                ],
                ["path"],
                document.RootElement),
        };

        var summary = ToolSummaryTier.Apply(tool);

        Assert.AreEqual("file_read", summary.Name, "工具名不得改动");
        CollectionAssert.AreEqual(
            new[] { "path", "offset" },
            summary.Parameters.Properties.Select(parameter => parameter.Name).ToArray(),
            "参数名必须全部保留，否则模型无法试调");
        Assert.IsTrue(
            summary.Parameters.Properties.All(parameter => parameter.Description.Length == 0),
            "逐参说明必须被清空——它是字节大户");
        CollectionAssert.AreEqual(new[] { "path" }, summary.Parameters.Required.ToArray(), "必填信息必须保留");
        Assert.IsNull(summary.Parameters.RawJsonSchema, "RawJsonSchema 必须丢掉，否则等于没压缩");
        Assert.IsFalse(summary.Description.Contains("offset/limit"), "只保留首句");
    }

    [TestMethod]
    public void Apply_KeepsOnlyFirstSentence()
    {
        var tool = Tool("terminal_start", "在后台启动一个命令并返回 job id。参数 timeout 单位为秒，默认 30。");

        var summary = ToolSummaryTier.Apply(tool);

        Assert.AreEqual("在后台启动一个命令并返回 job id", summary.Description);
    }

    [TestMethod]
    public void Apply_TruncatesOverlongFirstSentenceToLimit()
    {
        var tool = Tool("long_tool", new string('x', 500));

        var summary = ToolSummaryTier.Apply(tool, descriptionChars: 40);

        Assert.AreEqual(40, summary.Description.Length);
    }

    [TestMethod]
    public void Apply_DoesNotSplitOnDecimalOrAbbreviation()
    {
        // 英文句点只有「后面是空白或已到结尾」才算句末，避免把 v1.2 / e.g. 切成半句。
        var tool = Tool("api_tool", "Calls the v1.2 endpoint, e.g. /usage, and returns rows。Second sentence here.");

        var summary = ToolSummaryTier.Apply(tool);

        Assert.AreEqual("Calls the v1.2 endpoint, e.g. /usage, and returns rows", summary.Description);
    }

    [TestMethod]
    public void Apply_EmptyDescription_ReturnsEmptyNotNull()
    {
        var summary = ToolSummaryTier.Apply(Tool("quiet_tool", "   "));

        Assert.AreEqual(string.Empty, summary.Description);
    }

    [TestMethod]
    public void Apply_IsIdempotent()
    {
        var tool = Tool("file_patch", "按行区间打补丁。支持 dry_run，默认 false；失败时返回可判错误码。");

        var once = ToolSummaryTier.Apply(tool);
        var twice = ToolSummaryTier.Apply(once);

        Assert.AreEqual(once.Description, twice.Description, "重复压缩不得再变形，否则每轮都改写工具块字节");
        Assert.AreEqual(once.Parameters.Properties.Count, twice.Parameters.Properties.Count);
    }

    private static LlmToolDefinition Tool(string name, string description) => new()
    {
        Name = name,
        Description = description,
        Parameters = new ToolParameterSchema(
            [new ToolParameter("arg", "string", "参数说明")],
            ["arg"]),
    };
}
