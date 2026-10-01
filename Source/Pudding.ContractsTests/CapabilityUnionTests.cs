using Pudding.Contracts;
using Pudding.Contracts.Desktop;

namespace Pudding.ContractsTests;

public sealed class CapabilityUnionTests
{
    private static readonly DesktopPageTarget Target = new("ctx-1", "page-1");

    [Fact]
    public void Request_Factories_SetExactlyOneBranch()
    {
        var navigate = DesktopCapabilityRequest.ForNavigate(new NavigateRequest(Target, new Uri("https://example.com")));
        Assert.NotNull(navigate.Navigate);
        Assert.Null(navigate.Javascript);
        Assert.Null(navigate.Notification);
        Assert.Equal(Target, navigate.Target);

        var javascript = DesktopCapabilityRequest.ForJavascript(new JavascriptRequest(Target, "1"));
        Assert.Null(javascript.Navigate);
        Assert.NotNull(javascript.Javascript);
        Assert.Equal(Target, javascript.Target);

        var notification = DesktopCapabilityRequest.ForNotification(new DesktopNotificationRequest("t", "m"));
        Assert.NotNull(notification.Notification);
        Assert.Null(notification.Target);
        Assert.False(notification.ExpectedPageVersion.IsKnown);
    }

    [Fact]
    public void Request_ExposesExpectedPageVersion()
    {
        var navigate = DesktopCapabilityRequest.ForNavigate(
            new NavigateRequest(Target, new Uri("https://example.com"), DesktopPageVersion.Require(9)));
        Assert.Equal(9, navigate.ExpectedPageVersion.Value);

        var javascript = DesktopCapabilityRequest.ForJavascript(
            new JavascriptRequest(Target, "1", DesktopPageVersion.Require(4)));
        Assert.Equal(4, javascript.ExpectedPageVersion.Value);
    }

    [Fact]
    public void Request_RejectsNullPayloads()
    {
        Assert.Throws<ArgumentNullException>(() => DesktopCapabilityRequest.ForNavigate(null!));
        Assert.Throws<ArgumentNullException>(() => DesktopCapabilityRequest.ForJavascript(null!));
        Assert.Throws<ArgumentNullException>(() => DesktopCapabilityRequest.ForNotification(null!));
    }

    [Fact]
    public void Response_Factories_AreMutuallyExclusive()
    {
        var navigate = DesktopCapabilityResponse.FromNavigate(
            new NavigateResult(NavigateDisposition.Accepted, new Uri("https://example.com"), DesktopPageVersion.Require(1)));
        Assert.False(navigate.IsFailure);
        Assert.Null(navigate.Error);
        Assert.Null(navigate.Javascript);

        var javascript = DesktopCapabilityResponse.FromJavascript(new JavascriptResult(JavascriptValueKind.Number, "1", false));
        Assert.NotNull(javascript.Javascript);

        var notification = DesktopCapabilityResponse.FromNotification(new DesktopNotificationResult(true, "n-1"));
        Assert.NotNull(notification.Notification);

        var failure = DesktopCapabilityResponse.Failure(DesktopCapabilityError.Unauthorized("nope"));
        Assert.True(failure.IsFailure);
        Assert.Null(failure.Navigate);
        Assert.Null(failure.Javascript);
        Assert.Null(failure.Notification);
    }

    [Fact]
    public void Response_RejectsEmptyOrAmbiguousResults()
    {
        Assert.Throws<ArgumentNullException>(() => DesktopCapabilityResponse.Failure(null!));
        Assert.Throws<ArgumentNullException>(() => DesktopCapabilityResponse.FromNavigate(null!));
        Assert.Throws<ArgumentNullException>(() => DesktopCapabilityResponse.FromJavascript(null!));
        Assert.Throws<ArgumentNullException>(() => DesktopCapabilityResponse.FromNotification(null!));
    }

    [Fact]
    public void ToString_IsPayloadFree()
    {
        var response = DesktopCapabilityResponse.Failure(DesktopCapabilityError.Internal("boom"));
        Assert.Equal("failure(internal_error)", response.ToString());

        var script = "secret script body";
        var request = DesktopCapabilityRequest.ForJavascript(new JavascriptRequest(Target, script));
        Assert.DoesNotContain(script, request.ToString(), StringComparison.Ordinal);
        Assert.Contains($"{script.Length} chars", request.ToString(), StringComparison.Ordinal);
    }
}
