using PuddingPlatform.Services.Conversation;

namespace PuddingPlatformTests.Services;

[TestClass]
public sealed class CompactionSessionSuccessorTests
{
    [DataTestMethod]
    [DataRow("默认助手", "压缩 - 默认助手")]
    [DataRow("压缩 - 默认助手", "压缩 - 默认助手")]
    [DataRow("压缩 - 压缩 - 压缩 - 默认助手", "压缩 - 默认助手")]
    [DataRow("  压缩 - 压缩 - 默认助手  ", "压缩 - 默认助手")]
    public void BuildSuccessorTitle_KeepsExactlyOneCompactionPrefix(
        string previousTitle,
        string expected)
    {
        var actual = CompactionSessionSuccessor.BuildSuccessorTitle(previousTitle);

        Assert.AreEqual(expected, actual);
    }

    [DataTestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("  ")]
    [DataRow("压缩 - ")]
    public void BuildSuccessorTitle_UsesFallbackForMissingBaseTitle(string? previousTitle)
    {
        var actual = CompactionSessionSuccessor.BuildSuccessorTitle(previousTitle);

        Assert.AreEqual("压缩后的新会话", actual);
    }
}
