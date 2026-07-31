using PuddingCode.Models;

namespace PuddingCoreTests.MessageFabric;

[TestClass]
public sealed class AgentReplyVoiceDirectiveTests
{
    [TestMethod]
    public void Parse_VoiceOnlyFence_ExtractsSpeechWithoutText()
    {
        var result = AgentReplyVoiceDirective.Parse(
            "```voice\n今天天气真好\n```");

        Assert.IsTrue(result.HasVoice);
        Assert.IsTrue(result.IsVoiceOnly);
        Assert.AreEqual(string.Empty, result.TextContent);
        Assert.AreEqual("今天天气真好", result.VoiceContent);
        Assert.AreEqual("今天天气真好", result.TextFallbackContent);
    }

    [TestMethod]
    public void Parse_MixedMarkdown_RemovesFenceAndPreservesText()
    {
        var result = AgentReplyVoiceDirective.Parse(
            "先看文字。\n\n```voice\n这是语音内容。\n```\n\n补充说明。");

        Assert.IsTrue(result.HasVoice);
        Assert.IsFalse(result.IsVoiceOnly);
        Assert.AreEqual("先看文字。\n\n补充说明。", result.TextContent);
        Assert.AreEqual("这是语音内容。", result.VoiceContent);
        Assert.AreEqual(
            "先看文字。\n\n这是语音内容。\n\n补充说明。",
            result.TextFallbackContent);
    }

    [TestMethod]
    public void Parse_MultipleVoiceFences_CombinesSpeechInSourceOrder()
    {
        var result = AgentReplyVoiceDirective.Parse(
            "正文\n```voice\n第一段\n```\n```voice\n第二段\n```");

        Assert.AreEqual("正文", result.TextContent);
        Assert.AreEqual(
            $"第一段{Environment.NewLine}{Environment.NewLine}第二段",
            result.VoiceContent);
    }

    [TestMethod]
    public void Parse_UnclosedOrEmptyFence_RemainsOrdinaryMarkdown()
    {
        const string unclosed = "```voice\n还没有闭合";
        const string empty = "```voice\n\n```";

        Assert.IsFalse(AgentReplyVoiceDirective.Parse(unclosed).HasVoice);
        Assert.AreEqual(unclosed, AgentReplyVoiceDirective.Parse(unclosed).TextContent);
        Assert.IsFalse(AgentReplyVoiceDirective.Parse(empty).HasVoice);
        Assert.AreEqual(empty, AgentReplyVoiceDirective.Parse(empty).TextContent);
    }

}
