using PuddingCode.Models;

namespace PuddingCoreTests.MessageFabric;

[TestClass]
public sealed class AgentReplyImageDirectiveTests
{
    private const string ArtifactId =
        "vision-0123456789abcdef0123456789abcdef";

    [TestMethod]
    public void Parse_PureArtifactFence_IsImageOnly()
    {
        var result = AgentReplyImageDirective.Parse(
            $"```image\n{ArtifactId}\n```");

        Assert.IsTrue(result.HasImages);
        Assert.IsTrue(result.IsPureImage);
        Assert.AreEqual(ArtifactId, result.Items.Single().Reference);
    }

    [TestMethod]
    public void Parse_MixedAbsolutePaths_PreservesOrder()
    {
        var first = $@"D:\data\workspaces\default\vision-artifacts\{ArtifactId}.png";
        const string secondId =
            "vision-abcdef0123456789abcdef0123456789";
        var second =
            $@"D:\data\workspaces\default\vision-artifacts\{secondId}.jpg";
        var result = AgentReplyImageDirective.Parse(
            $"前文\n\n```IMAGE\n\"{first}\"\n```\n\n后文\n\n```image\n{second}\n```");

        Assert.HasCount(2, result.Items);
        Assert.IsFalse(result.IsPureImage);
        Assert.AreEqual(first, result.Items[0].Reference);
        Assert.AreEqual(second, result.Items[1].Reference);
        Assert.IsGreaterThan(result.Items[0].MatchIndex, result.Items[1].MatchIndex);
    }

    [TestMethod]
    public void Parse_RejectsUrlsRelativePathsAndArbitraryLocalFiles()
    {
        var url = AgentReplyImageDirective.Parse(
            "```image\nhttps://example.com/cat.png\n```");
        var relative = AgentReplyImageDirective.Parse(
            "```image\nimages/cat.png\n```");
        var arbitrary = AgentReplyImageDirective.Parse(
            "```image\nD:\\temp\\cat.png\n```");

        Assert.IsFalse(url.HasImages);
        Assert.IsFalse(relative.HasImages);
        Assert.IsFalse(arbitrary.HasImages);
    }

    [TestMethod]
    public void Parse_LimitsImagesAndRecognizesStreamingPrefix()
    {
        var reply = string.Join(
            "\n",
            Enumerable.Range(0, 5).Select(index =>
            {
                var id = $"vision-{index:x32}";
                return $"```image\n{id}\n```";
            }));

        Assert.HasCount(4, AgentReplyImageDirective.Parse(reply).Items);
        Assert.IsTrue(AgentReplyImageDirective.CouldBePureImagePrefix("```im"));
        Assert.IsTrue(
            AgentReplyImageDirective.CouldBePureImagePrefix(
                $"```image\n{ArtifactId}"));
        Assert.IsFalse(
            AgentReplyImageDirective.CouldBePureImagePrefix(
                $"```image\n{ArtifactId}\n```\n普通文本"));
        Assert.IsFalse(
            AgentReplyImageDirective.CouldBePureImagePrefix(
                "```ImageGeneration"));
        Assert.IsFalse(
            AgentReplyImageDirective.CouldBePureImagePrefix(
                "普通文本"));
    }
}
