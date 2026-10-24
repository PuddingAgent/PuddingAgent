using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Tools;

/// <summary>
/// 回归保护：网络层失败必须自带「能不能重试 + 改做什么」。
/// </summary>
/// <remarks>
/// <para>
/// 修复前是 <c>$"GitHub search request failed: {ex.Message}"</c> —— 只有事实，
/// 没有下一步。实测在本机失败时输出就是裸的
/// 「The SSL connection could not be established」，调用方除了重试没有别的动作可选，
/// 而重试必然继续失败（GitHub API 被 DNS/TLS 阻断）。
/// </para>
/// <para>
/// 断言统一用 <c>message.Contains(...)</c> 而不是 <c>Assert.Contains(...)</c>：
/// MSTest 4 的签名是 <c>Contains(substring, value)</c>，参数写反时失败信息会读成
/// 「字符串 A 不包含字符串 B」，很容易被误判成实现有 bug（本轮就这么绕过一次）。
/// </para>
/// </remarks>
[TestClass]
public sealed class GitHubSearchFailureMessageTests
{
    [TestMethod]
    public void TlsFailure_SaysDoNotRetryAndNamesAlternatives()
    {
        var message = GitHubSearchTool.DescribeRequestFailure(
            new HttpRequestException("The SSL connection could not be established"));

        Assert.IsTrue(
            message.Contains("GitHub search request failed", StringComparison.Ordinal),
            $"缺少前缀。实际为「{message}」。");
        Assert.IsTrue(
            message.Contains("TLS", StringComparison.Ordinal),
            $"缺少失败类别。实际为「{message}」。");
        Assert.IsTrue(
            message.Contains("retrying it keeps failing", StringComparison.Ordinal),
            $"缺少「不要重试」的结论。实际为「{message}」。");

        // 检索场景专属替代源（分类器默认只提「取内容」）。
        Assert.IsTrue(
            message.Contains("anysearch_search", StringComparison.Ordinal),
            $"缺少替代搜索源。实际为「{message}」。");
        Assert.IsTrue(
            message.Contains("doubao_search", StringComparison.Ordinal),
            $"缺少替代搜索源。实际为「{message}」。");

        // 实测可用的仓库文件通道，避免调用方以为 GitHub 内容完全取不到。
        Assert.IsTrue(
            message.Contains("raw.githubusercontent.com", StringComparison.Ordinal),
            $"缺少可用的仓库文件通道。实际为「{message}」。");
    }

    [TestMethod]
    public void NonTlsFailure_DoesNotRecommendSwitchingSearchProvider()
    {
        var message = GitHubSearchTool.DescribeRequestFailure(
            new InvalidOperationException("something else entirely"));

        Assert.IsTrue(
            message.Contains("GitHub search request failed", StringComparison.Ordinal),
            $"缺少前缀。实际为「{message}」。");
        Assert.IsFalse(
            message.Contains("anysearch_search", StringComparison.Ordinal),
            $"只有 TLS/DNS 阻断才该建议换搜索源；其他失败换源是误导。实际为「{message}」。");
    }

    [TestMethod]
    public void Message_CarriesMoreThanTheBareExceptionText()
    {
        // 这条盯的是回归点本身：修复前消息几乎等于 ex.Message。
        var message = GitHubSearchTool.DescribeRequestFailure(new Exception("boom"));
        var bareMessage = "GitHub search request failed: boom";

        Assert.IsTrue(
            message.Length > bareMessage.Length,
            $"消息必须比裸 Exception.Message 多出可操作内容；实际为「{message}」。");
    }
}
