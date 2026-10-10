using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using Pudding.DesktopConnection;
using Xunit;
using Proto = Pudding.Rpc.Protocol.V1;

namespace DesktopConnectionTests;

/// <summary>
/// §8.2 坐标操作线缆：`InteractCommand` 的 x/y 用 **proto3 optional** 保留 presence ⇒
/// **(0,0) 是合法坐标（页面左上角）必须原样到达**，而"未设置"必须保持 null —— 二者不得混同。
/// </summary>
public sealed class InteractPointerDecodeTests
{
    private static readonly DesktopInstanceId DesktopId = new("desk-1");

    private static Proto.CapabilityCommand Command(double? x, double? y, string observationId = "", string action = "pointer_click")
    {
        var interact = new Proto.InteractCommand
        {
            Target = new Proto.CommandTarget { ContextId = "ctx-1", PageId = "page-1" },
            ExpectedPageVersion = 1,
            Action = action,
            ObservationId = observationId,
        };

        if (x is { } px)
        {
            interact.X = px;
        }

        if (y is { } py)
        {
            interact.Y = py;
        }

        return new Proto.CapabilityCommand
        {
            Caller = new Proto.BrowserCallerIdentityMessage { Kind = "agent", CallerId = "agent-1", TaskId = "run-1" },
            OperationId = "op-pointer",
            Generation = 1,
            Capability = "browser.interact",
            Deadline = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(
                DateTimeOffset.UtcNow.AddSeconds(30)),
            Interact = interact,
        };
    }

    [Fact]
    public void CoordinatesAtTheOriginSurviveInsteadOfLookingUnset()
    {
        var decoded = CoreFrameMapping.Decode(Command(0, 0, "obs-1"), DesktopId);

        Assert.True(decoded.IsSuccess);
        var interact = decoded.Value.Request.Interact!;
        Assert.Equal(0, interact.X);
        Assert.Equal(0, interact.Y);
        Assert.Equal("obs-1", interact.ObservationId);
        Assert.Equal(DesktopInteractionAction.PointerClick, interact.Action);
    }

    [Fact]
    public void FractionalCoordinatesAndMoveActionTravel()
    {
        var decoded = CoreFrameMapping.Decode(Command(12.5, 44.25, action: "pointer_move"), DesktopId);

        Assert.True(decoded.IsSuccess);
        var interact = decoded.Value.Request.Interact!;
        Assert.Equal(12.5, interact.X);
        Assert.Equal(44.25, interact.Y);
        Assert.Equal(DesktopInteractionAction.PointerMove, interact.Action);
        Assert.Null(interact.ObservationId); // 空串 ⇒ 未设置
    }

}
