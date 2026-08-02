using PuddingBrowser.Abstractions;
using PuddingBrowser.WebView2;

namespace PuddingDesktop.Tests.Browser;

public sealed class WebView2DomClientTests
{
    [Fact]
    public void Normalize_ClampsSnapshotBudgets()
    {
        var normalized = WebView2DomClient.Normalize(new SnapshotOptions
        {
            MaxNodes = int.MaxValue,
            MaxTextLength = 1,
            MaxDepth = 0
        });

        Assert.Equal(10_000, normalized.MaxNodes);
        Assert.Equal(256, normalized.MaxTextLength);
        Assert.Equal(1, normalized.MaxDepth);
    }

    [Fact]
    public void ValidateLocator_AcceptsCurrentVersionRef()
        => WebView2DomClient.ValidateLocator(new Locator
        {
            Kind = LocatorKind.Ref,
            Value = "v7-n12"
        }, pageVersion: 7);

    [Fact]
    public void ValidateLocator_RejectsStaleRefWithStableCode()
    {
        var exception = Assert.Throws<BrowserOperationException>(() =>
            WebView2DomClient.ValidateLocator(new Locator
            {
                Kind = LocatorKind.Ref,
                Value = "v6-n12"
            }, pageVersion: 7));

        Assert.Equal("stale_element_reference", exception.Code);
    }

    [Theory]
    [InlineData("https://example.test/tasks/42", "https://example.test/tasks/*", true)]
    [InlineData("https://example.test/tasks/42", "*/TASKS/?2", true)]
    [InlineData("https://example.test/tasks/42", "https://other.test/*", false)]
    public void WildcardMatch_UsesStableCaseInsensitiveSemantics(
        string input, string pattern, bool expected)
        => Assert.Equal(expected, WebView2DomClient.WildcardMatch(input, pattern));

    [Fact]
    public void ValidateLocator_RejectsFrameAndCompoundHasUntilSupported()
    {
        var exception = Assert.Throws<BrowserOperationException>(() =>
            WebView2DomClient.ValidateLocator(new Locator
            {
                Kind = LocatorKind.Css,
                Value = "button",
                Frame = new FrameSelector { Name = "payment" }
            }, pageVersion: 1));

        Assert.Equal("browser_operation_not_supported", exception.Code);
    }
}
