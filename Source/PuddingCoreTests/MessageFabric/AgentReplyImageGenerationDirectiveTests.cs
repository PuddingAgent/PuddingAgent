using PuddingCode.Models;

namespace PuddingCoreTests.MessageFabric;

[TestClass]
public sealed class AgentReplyImageGenerationDirectiveTests
{
    [TestMethod]
    public void Parse_SimpleFence_UsesDefaultsAndPreservesOriginal()
    {
        const string reply =
            "准备生成：\n\n```ImageGeneration\n一只戴黄色围巾的猫，电影感光影。\n```";

        var result = AgentReplyImageGenerationDirective.Parse(reply);

        Assert.IsTrue(result.HasImageGeneration);
        Assert.AreEqual(reply, result.OriginalContent);
        var item = result.Items.Single();
        Assert.AreEqual("一只戴黄色围巾的猫，电影感光影。", item.Prompt);
        Assert.AreEqual("default", item.Mode);
        Assert.AreEqual("2K", item.Size);
        Assert.IsTrue(item.Watermark);
    }

    [TestMethod]
    public void Parse_EnhancedHeadersAndMultipleBlocks_PreservesOrder()
    {
        const string reference = "vision-0123456789abcdef0123456789abcdef";
        var reply = $"""
            ```ImageGeneration
            mode: precision
            size: 2048x2048
            watermark: false
            output_format: jpeg
            optimize: fast
            references: {reference}

            把图 1 <bbox>120 180 640 760</bbox> 区域替换成花园。
            ```

            ```imagegeneration
            web_search: true

            上海未来五日天气信息图。
            ```
            """;

        var result = AgentReplyImageGenerationDirective.Parse(reply);

        Assert.HasCount(2, result.Items);
        var first = result.Items[0];
        Assert.AreEqual("precision", first.Mode);
        Assert.AreEqual("2048x2048", first.Size);
        Assert.IsFalse(first.Watermark);
        Assert.AreEqual("jpeg", first.OutputFormat);
        Assert.AreEqual("fast", first.OptimizePromptMode);
        CollectionAssert.AreEqual(
            new[] { reference },
            first.ReferenceArtifactIds.ToArray());
        StringAssert.Contains(first.Prompt, "<bbox>120 180 640 760</bbox>");
        Assert.IsTrue(result.Items[1].EnableWebSearch);
    }

    [TestMethod]
    public void Parse_MalformedOrEmptyFence_DoesNotTrigger()
    {
        var unclosed = "```ImageGeneration\n一只猫";
        var invalid = "```ImageGeneration\nwatermark: maybe\n\n一只猫\n```";
        var empty = "```ImageGeneration\n\n```";

        Assert.IsFalse(
            AgentReplyImageGenerationDirective.Parse(unclosed)
                .HasImageGeneration);
        Assert.IsFalse(
            AgentReplyImageGenerationDirective.Parse(invalid)
                .HasImageGeneration);
        Assert.IsFalse(
            AgentReplyImageGenerationDirective.Parse(empty)
                .HasImageGeneration);
    }

    [TestMethod]
    public void Parse_EnforcesFourImageLimit()
    {
        var reply = string.Join(
            "\n",
            Enumerable.Range(0, 5).Select(index =>
                $"```ImageGeneration\n图片 {index}\n```"));

        var result = AgentReplyImageGenerationDirective.Parse(reply);

        Assert.HasCount(4, result.Items);
        Assert.AreEqual(4, result.TotalImageCount);
    }
}
