using Pudding.Contracts.Desktop;

namespace Pudding.ContractsTests;

/// <summary>控制面载荷（§4.1）：请求把**任务绑定**原样带过去；撤销只带 (任务, 目标)。</summary>
public sealed class BrowserControlPlaneRequestsTests
{
    private static BrowserTaskBinding Binding() => new(
        new BrowserCallerIdentity(BrowserCallerKind.Agent, "agent-1", "run-1"),
        new DesktopPageTarget("ctx-1", "page-1"),
        BrowserPageGrantScope.Read,
        TimeSpan.FromMinutes(5),
        "user-goal:g-1");

    [Fact]
    public void TheApplyRequestCarriesTheWholeBinding()
    {
        var binding = Binding();

        var request = DesktopCapabilityRequest.ForBindingApply(binding);

        Assert.NotNull(request.BindingApply);
        Assert.Same(binding, request.BindingApply!.Binding);
        Assert.Null(request.BindingRevoke);
    }

    [Fact]
    public void TheRevokeRequestCarriesTaskAndTargetOnly()
    {
        var task = new BrowserCallerIdentity(BrowserCallerKind.Agent, "agent-1", "run-1");
        var target = new DesktopPageTarget("ctx-1", "page-1");

        var request = DesktopCapabilityRequest.ForBindingRevoke(task, target);

        Assert.NotNull(request.BindingRevoke);
        Assert.Same(task, request.BindingRevoke!.Task);
        Assert.Same(target, request.BindingRevoke.Target);
        Assert.Null(request.BindingApply);
    }
}