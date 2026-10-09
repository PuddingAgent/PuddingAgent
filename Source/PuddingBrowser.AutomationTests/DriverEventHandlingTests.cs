using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PuddingBrowser.AutomationTests;

/// <summary>
/// **驱动的事件接管清单是冻结的**（§8）：这张清单等于"我们接管了哪些会让自动化停在那里的事件"，
/// 增删都必须是有意识的改动。
///
/// 同时钉住一处**已知缺口**：WebView2 SDK 没有文件选择器 API ⇒ 我们接管不了"页面自己弹文件选择框"，
/// 且代码里必须**写明**这件事（避免下一个人以为"都接管了"）。
/// </summary>
[TestClass]
public sealed class DriverEventHandlingTests
{
    /// <summary>冻结的订阅清单（标识符按源码顺序）。</summary>
    private static readonly string[] FrozenHandlers =
    [
        "NavigationStarting",
        "NavigationCompleted",
        "DocumentTitleChanged",
        "SourceChanged",
        "ProcessFailed",
        "ScriptDialogOpening",
        "PermissionRequested",
        "NewWindowRequested",
        "DownloadStarting",
    ];

    [TestMethod]
    public void EverySubscribedPageEventIsAlsoUnsubscribedExactlyOnce()
    {
        // "订阅不增长/不泄漏"（S-11 的静态部分）：驱动必须**对称**地订阅与退订，
        // 而且那 9 个事件在整个文件里只允许出现**一次 `+=`**（出现在 SubscribeCoreWebViewEvents 里）。
        // 出现第二次 += 就意味着"某条路径会重复订阅"（重复应答对话框/重复上报下载，正是要防的）。
        var source = DriverSource();

        StringAssert.Contains(source, "private void UnsubscribeCoreWebViewEvents(CoreWebView2 coreWebView)");

        foreach (var handler in FrozenHandlers)
        {
            var subscribe = Regex.Matches(source, $@"\bcoreWebView\.{handler}\s*\+=").Count;
            var unsubscribe = Regex.Matches(source, $@"\bcoreWebView\.{handler}\s*-=").Count;

            Assert.AreEqual(1, subscribe, $"{handler} 的订阅必须恰好一次（多了就是重复订阅路径）");
            Assert.AreEqual(1, unsubscribe, $"{handler} 必须被对称退订（否则页面释放后回调仍会命中已释放对象）");
        }

        // `_coreWebView`（等待用的 oneshot 处理器）方向相反：允许**一次订阅、多处退订**
        // （同一个处理器在等待的每条出口路径上都要摘掉，重复摘是安全的空操作）。
        // 真正的危险是反方向——**订阅了却没有任何退订**（回调会命中已释放的对象）。
        var oneshotSubscribe = Regex.Matches(source, @"_coreWebView!?\.NavigationCompleted\s*\+=").Count;
        var oneshotUnsubscribe = Regex.Matches(source, @"_coreWebView!?\.NavigationCompleted\s*-=").Count;
        Assert.IsTrue(
            oneshotSubscribe > 0 && oneshotSubscribe <= oneshotUnsubscribe,
            $"oneshot 等待处理器必须每条订阅都有退订（订阅 {oneshotSubscribe} 次 / 退订 {oneshotUnsubscribe} 次）");
    }

    private static string DriverSource()
    {
        var root = RepoRoot();
        return File.ReadAllText(Path.Combine(
            root, "Source", "PuddingBrowser.WebView2", "WebView2BrowserPage.cs"));
    }

    private static string RepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PuddingAgentNetwork.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("repository root not found");
    }

    [TestMethod]
    public void TheSubscribedCoreWebViewEventsMatchTheFrozenList()
    {
        var source = DriverSource();

        // `\b` 很关键：`_coreWebView.Xxx` 是另一个变量，不该算进这份清单。
        var subscribed = Regex.Matches(source, @"\bcoreWebView\.(\w+)\s*\+=", RegexOptions.Multiline)
            .Select(match => match.Groups[1].Value)
            .ToArray();

        CollectionAssert.AreEqual(
            FrozenHandlers,
            subscribed,
            "驱动订阅的页面事件发生了变化：这意味着'接管哪些会卡住自动化的事件'变了。"
            + "请同步更新本冻结清单与 SubscribeCoreWebViewEvents 的说明。");
    }

    [TestMethod]
    public void TheDriverDocumentsThatFilePickersCannotBeIntercepted()
    {
        // 缺口必须写在代码里：SDK 没有该 API ⇒ 我们不接管，也不假装接管。
        var source = DriverSource();

        StringAssert.Contains(source, "文件选择器无法接管");
        StringAssert.Contains(source, "FilePicker");

        // 对策也必须写明（否则下一个人只知道"没有"，不知道"那我们怎么办"）。
        StringAssert.Contains(source, "DOM.setFileInputFiles");
    }

    [TestMethod]
    public void TheCaptureSafetyDefaultsAreStillInPlace()
    {
        // 与"接管"配套的安全默认（§8）：权限拒绝、新窗口抑制、下载取消。
        // 这三条是安全侧默认，不能被"顺手改成允许"而不被发现。
        var source = DriverSource();

        StringAssert.Contains(source, "CoreWebView2PermissionState.Deny");
        // 下载取消与"新窗口抑制"都通过 Cancel/Handled 落地（源码里是 `e.Cancel = true;`）。
        StringAssert.Contains(source, "e.Cancel = true;");
        StringAssert.Contains(source, "Handled = true;");
    }
}
