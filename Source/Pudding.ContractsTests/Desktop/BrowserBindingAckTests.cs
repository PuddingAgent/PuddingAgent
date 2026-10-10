using Pudding.Contracts.Desktop;

namespace Pudding.ContractsTests;

/// <summary>控制面 ack（§4.1）：成功带 GrantId+Epoch；拒绝带**可区分**原因；ack 是**唯一**载荷分支。</summary>
public sealed class BrowserBindingAckTests
{
    [Fact]
    public void AnAcceptedAckCarriesTheGrantIdAndEpoch()
    {
        var grantId = new BrowserPageGrantId("grant-1");

        var response = DesktopCapabilityResponse.FromBindingAck(
            new BrowserBindingAck(true, grantId, Epoch: default, Reason: null));

        Assert.NotNull(response.BindingAck);
        Assert.True(response.BindingAck!.Accepted);
        Assert.Same(grantId, response.BindingAck.GrantId);
        Assert.Null(response.BindingAck.Reason);
        Assert.Null(response.Error);        // 不得同时出现错误分支
        Assert.Null(response.Snapshot);     // 也不得混入其它载荷
    }

    [Fact]
    public void ARejectedAckKeepsItsDistinguishableReason()
    {
        var response = DesktopCapabilityResponse.FromBindingAck(
            new BrowserBindingAck(
                false,
                GrantId: null,
                Epoch: default,
                Reason: "the binding target is not the page the user currently selected for the agent"));

        Assert.NotNull(response.BindingAck);
        Assert.False(response.BindingAck!.Accepted);
        Assert.Contains("not the page the user currently selected", response.BindingAck.Reason!, StringComparison.Ordinal);
        Assert.Null(response.BindingAck.GrantId);
    }
}