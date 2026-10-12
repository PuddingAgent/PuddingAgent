using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using Pudding.DesktopConnection;
using Xunit;

namespace DesktopConnectionTests;

/// <summary>
/// Bridge 传输的**可信上下文适配**用例：判据必须与能力通道一致，且**缺身份即拒**（不得补默认身份）。
/// </summary>
public sealed class BridgeCallerContextTests
{
    private static readonly DesktopInstanceId DesktopId = new("desk-1");

    [Fact]
    public void MissingCallerIdentity_IsRejected()
    {
        // 没有任何身份来源 ⇒ 拒绝（浏览器自动化能力在 Desktop 侧必须有可信身份）。
        var result = BridgeCallerContext.Create(
            DesktopId, new OperationId(Guid.NewGuid().ToString("N")), DateTimeOffset.UtcNow.AddSeconds(30), null,
            null, null, null, null);

        Assert.True(result.IsFailure);
    }

    [Fact]
    public void UnregisteredCallerKind_IsRejected()
    {
        var result = BridgeCallerContext.Create(
            DesktopId, new OperationId(Guid.NewGuid().ToString("N")), DateTimeOffset.UtcNow.AddSeconds(30), null,
            "not-a-kind", "agent-1", "run-1", null);

        Assert.True(result.IsFailure);
    }
    [Fact]
    public void SubAgentWithoutParentTask_IsRejected()
    {
        // "子代理只取父授权的子集" 靠父任务存在来保证 ⇒ 缺父任务即拒（判据来自 BrowserCallerIdentity 自身）。
        var result = BridgeCallerContext.Create(
            DesktopId, new OperationId(Guid.NewGuid().ToString("N")), DateTimeOffset.UtcNow.AddSeconds(30), null,
            BrowserCallerKindWire.NameOf(BrowserCallerKind.SubAgent), "agent-1", "run-1", null);

        Assert.True(result.IsFailure);
    }

    [Fact]
    public void ValidAgentCaller_IsAccepted()
    {
        var result = BridgeCallerContext.Create(
            DesktopId, new OperationId(Guid.NewGuid().ToString("N")), DateTimeOffset.UtcNow.AddSeconds(30), null,
            BrowserCallerKindWire.NameOf(BrowserCallerKind.Agent), "agent-1", "run-1", null);

        Assert.False(result.IsFailure);
        Assert.True(result.Value.TryGetBrowserCaller(out var caller, out _));
        Assert.Equal("agent-1", caller!.CallerId);
    }
}