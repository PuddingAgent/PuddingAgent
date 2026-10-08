using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingBrowser.Automation;

namespace PuddingBrowser.AutomationTests;

/// <summary>
/// 页面周边事件（权限请求/新窗口/下载）的策略（§8 可靠性）：
/// 不处理会让页面停在"等人操作"的状态，处理错了（默认允许/偷偷改语义/随意落盘）更糟。
/// </summary>
[TestClass]
public sealed class PageEventPolicyTests
{
    [TestMethod]
    public void EveryPermissionIsDenied()
    {
        // 刻意没有"按种类放行"的白名单：授权必须是显式的产品决定，而不是策略函数里的隐藏后门。
        foreach (var kind in new[] { "Camera", "Microphone", "Geolocation", "Notifications", "ClipboardRead",
            "OtherSensors", "MultipleAutomaticDownloads", null, "", "SomeFutureKind" })
        {
            Assert.AreEqual(
                PageEventPolicy.PermissionVerdict.Deny,
                PageEventPolicy.DecidePermission(kind),
                $"'{kind}' 不应被默认放行");
        }
    }

    [TestMethod]
    public void PopupsAreSuppressed_AndDownloadsCanceled()
    {
        Assert.AreEqual(PageEventPolicy.WindowVerdict.Suppress, PageEventPolicy.DecideWindow());
        Assert.AreEqual(PageEventPolicy.DownloadVerdict.Cancel, PageEventPolicy.DecideDownload());
    }

    [TestMethod]
    public void HostOfKeepsOnlySchemeAndHost()
    {
        // 完整 URL 可能带 token/查询串，日志与回执里只留 scheme://host。
        Assert.AreEqual("https://example.test", PageEventPolicy.HostOf("https://example.test/a/b?token=secret#x"));
        Assert.AreEqual("http://example.test:8080", PageEventPolicy.HostOf("http://example.test:8080/p?q=1"));
        Assert.AreEqual("https://example.test", PageEventPolicy.HostOf("https://example.test/"));
        Assert.AreEqual(string.Empty, PageEventPolicy.HostOf("not a url"));
        Assert.AreEqual(string.Empty, PageEventPolicy.HostOf(null));
    }

    [TestMethod]
    public void PermissionSummaryNamesTheKindAndNeverLeaksThePath()
    {
        var summary = PageEventPolicy.DescribePermission(
            "Camera", "https://example.test/secret/path?token=abc", PageEventPolicy.PermissionVerdict.Deny);

        Assert.Contains("kind=camera", summary);
        Assert.Contains("answered=deny", summary);
        Assert.Contains("host=https://example.test", summary);
        Assert.IsFalse(summary.Contains("token", StringComparison.Ordinal));
        Assert.IsFalse(summary.Contains("secret", StringComparison.Ordinal));
    }

    [TestMethod]
    public void WindowSummarySaysThePopupWasSuppressed()
    {
        var summary = PageEventPolicy.DescribeWindow("https://example.test/popup", PageEventPolicy.WindowVerdict.Suppress);

        Assert.Contains("answered=suppress", summary);
        Assert.Contains("suppressed", summary);
        Assert.Contains("navigate explicitly", summary);
    }

    [TestMethod]
    public void DownloadSummarySaysNoFileWasWritten()
    {
        var summary = PageEventPolicy.DescribeDownload(
            "report.pdf", "https://example.test/dl", PageEventPolicy.DownloadVerdict.Cancel);

        Assert.Contains("answered=cancel", summary);
        Assert.Contains("suggestedName=report.pdf", summary);
        Assert.Contains("no file was written", summary);
    }

    [TestMethod]
    public void SummariesStayBounded_EvenForHostileNames()
    {
        var hostile = "a\nb\tc\u0007" + new string('x', 400);
        var summary = PageEventPolicy.DescribeDownload(hostile, null, PageEventPolicy.DownloadVerdict.Cancel);

        Assert.IsFalse(summary.Contains('\n'));
        Assert.IsFalse(summary.Contains('\t'));
        Assert.IsTrue(summary.Length < 300, $"摘要过长：{summary.Length}");
        Assert.Contains("…", summary);

        // 没有地址/没有名字时不留空字段。
        var bare = PageEventPolicy.DescribeDownload(null, null, PageEventPolicy.DownloadVerdict.Cancel);
        Assert.IsFalse(bare.Contains("host="));
        Assert.IsFalse(bare.Contains("suggestedName="));
    }
}
