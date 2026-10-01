using Pudding.Contracts;
using Pudding.Contracts.Desktop;

namespace Pudding.ContractsTests;

public sealed class ContractValueTests
{
    [Fact]
    public void Identifiers_RejectIllegalValues()
    {
        Assert.Equal("desk-1", new DesktopInstanceId("desk-1").Value);
        Assert.Throws<ArgumentException>(() => new DesktopInstanceId(string.Empty));
        Assert.Throws<ArgumentException>(() => new DesktopInstanceId("has space"));
        Assert.Throws<ArgumentException>(() => new DesktopInstanceId("中文"));
        Assert.Throws<ArgumentException>(() => new DesktopInstanceId(new string('a', DesktopInstanceId.MaxLength + 1)));
        Assert.True(DesktopInstanceId.IsValid(new string('a', DesktopInstanceId.MaxLength)));
        Assert.False(DesktopInstanceId.IsValid(null));

        Assert.Throws<ArgumentException>(() => new OperationId("bad/id"));
        Assert.True(OperationId.IsValid(OperationId.NewId().Value));
        Assert.Throws<ArgumentException>(() => new DesktopCorrelationId("bad correlation"));
        Assert.True(DesktopCorrelationId.IsValid("chat:42-abc"));
    }

    [Fact]
    public void ConnectionGeneration_RequiresPositiveValue()
    {
        Assert.False(ConnectionGeneration.None.IsLive);
        Assert.True(ConnectionGeneration.Require(3).IsLive);
        Assert.Equal("3", ConnectionGeneration.Require(3).ToString());
        Assert.Throws<ArgumentOutOfRangeException>(() => ConnectionGeneration.Require(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ConnectionGeneration.Require(-1));
    }

    [Fact]
    public void CallContext_RequiresUtcDeadlineAndValidIdentities()
    {
        var deadline = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        var context = new DesktopCallContext(
            new DesktopInstanceId("desk-1"), new OperationId("op-1"), deadline, new DesktopCorrelationId("call-9"));

        Assert.Equal(deadline, context.DeadlineUtc);
        Assert.Equal("call-9", context.CorrelationId!.Value);
        Assert.False(context.IsExpiredAt(deadline.AddMinutes(-1)));
        Assert.True(context.IsExpiredAt(deadline));
        Assert.Equal(TimeSpan.FromMinutes(-1), context.RemainingAt(deadline.AddMinutes(1)));

        var localOffset = new DateTimeOffset(2026, 10, 1, 20, 0, 0, TimeSpan.FromHours(8));
        Assert.Throws<ArgumentException>(
            () => new DesktopCallContext(new DesktopInstanceId("desk-1"), new OperationId("op-1"), localOffset));

        Assert.Throws<ArgumentNullException>(
            () => new DesktopCallContext(null!, new OperationId("op-1"), deadline));
        Assert.Throws<ArgumentNullException>(
            () => new DesktopCallContext(new DesktopInstanceId("desk-1"), null!, deadline));
    }

    [Fact]
    public void PageTarget_RequiresExplicitContextAndPage()
    {
        var target = new DesktopPageTarget("ctx-1", "page-2");

        Assert.Equal("ctx-1/page-2", target.Key);
        Assert.Equal(target.Key, target.ToString());
        Assert.Throws<ArgumentException>(() => new DesktopPageTarget(string.Empty, "page-2"));
        Assert.Throws<ArgumentException>(() => new DesktopPageTarget("ctx-1", "page 2"));
        Assert.Throws<ArgumentException>(() => new DesktopPageTarget("ctx-1", new string('p', 129)));
    }

    [Fact]
    public void PageVersion_UnknownIsZeroAndRequireRejectsNonPositive()
    {
        Assert.False(DesktopPageVersion.Unknown.IsKnown);
        Assert.True(DesktopPageVersion.Require(7).IsKnown);
        Assert.Equal("7", DesktopPageVersion.Require(7).ToString());
        Assert.Throws<ArgumentOutOfRangeException>(() => DesktopPageVersion.Require(0));
    }

    [Fact]
    public void NavigateRequest_RequiresAbsoluteUrl()
    {
        var target = new DesktopPageTarget("ctx-1", "page-1");
        var request = new NavigateRequest(target, new Uri("https://example.com/a"), DesktopPageVersion.Require(2));

        Assert.Equal("https://example.com/a", request.Url.AbsoluteUri);
        Assert.Equal(2, request.ExpectedPageVersion.Value);
        Assert.False(new NavigateRequest(target, new Uri("about:blank")).ExpectedPageVersion.IsKnown);

        Assert.Throws<ArgumentException>(() => new NavigateRequest(target, new Uri("relative/path", UriKind.Relative)));
        Assert.Throws<ArgumentNullException>(() => new NavigateRequest(null!, new Uri("https://example.com")));
    }

    [Fact]
    public void JavascriptRequest_BoundsScriptAndResultSize()
    {
        var target = new DesktopPageTarget("ctx-1", "page-1");
        var request = new JavascriptRequest(target, "return 1;");

        Assert.Equal(JavascriptRequest.DefaultMaxResultBytes, request.MaxResultBytes);
        Assert.Throws<ArgumentException>(() => new JavascriptRequest(target, "   "));
        Assert.Throws<ArgumentOutOfRangeException>(() => new JavascriptRequest(target, "1", default, 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new JavascriptRequest(target, "1", default, JavascriptRequest.MaxResultBytesLimit + 1));
        Assert.Equal(
            JavascriptValueKind.String,
            new JavascriptResult(JavascriptValueKind.String, "\"hi\"", Truncated: false).Kind);
    }

    [Fact]
    public void NotificationRequest_AllowsCjkButStripsControlCharacters()
    {
        var request = new DesktopNotificationRequest("已打开文件", "标题\t第二行", DesktopNotificationPriority.High);

        Assert.Equal("已打开文件", request.Title);
        Assert.Equal("标题 第二行", request.Message);
        Assert.Equal(DesktopNotificationPriority.High, request.Priority);
        Assert.Throws<ArgumentException>(() => new DesktopNotificationRequest("  ", "body"));
        Assert.Throws<ArgumentException>(() => new DesktopNotificationRequest("t", string.Empty));
        Assert.Throws<ArgumentException>(
            () => new DesktopNotificationRequest(new string('t', DesktopNotificationRequest.MaxTitleLength + 1), "body"));
    }
}
