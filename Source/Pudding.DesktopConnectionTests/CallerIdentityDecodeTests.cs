using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using Pudding.DesktopConnection;
using Proto = Pudding.Rpc.Protocol.V1;

namespace DesktopConnectionTests;

/// <summary>
/// §3.2 的 **Desktop 侧**身份校验：命令里的 caller 必须能解析、必须合法，
/// 且**浏览器自动化能力必须有身份**（缺即拒、不许冒充 User）。Core 侧预检不能替代这里。
/// </summary>
public sealed class CallerIdentityDecodeTests
{
    private static readonly DesktopInstanceId DesktopId = new("desk-1");

    [Fact]
    public void ATrustedAgentCallerIsDecodedOntoTheCallContext()
    {
        var frame = Frames.PageStateCommand("op-1");

        var decoded = CoreFrameMapping.Decode(frame.Command, DesktopId);

        Assert.True(decoded.IsSuccess, "decode should succeed for a trusted caller");
        Assert.NotNull(decoded.Value.Context.BrowserCaller);
        Assert.Equal("agent-1", decoded.Value.Context.BrowserCaller!.CallerId);
        Assert.Equal("run-1", decoded.Value.Context.BrowserCaller.TaskId);
    }

    [Fact]
    public void ABrowserCapabilityWithoutAnyCallerIsRefused()
    {
        var command = Frames.Command("op-2", "browser.contexts", deadline: DateTimeOffset.UtcNow.AddSeconds(30));
        command.Caller = null;

        var decoded = CoreFrameMapping.Decode(command, DesktopId);

        Assert.False(decoded.IsSuccess);
        Assert.Contains("no trusted browser caller", decoded.Error!.Message ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownCallerKindIsRefusedInsteadOfDefaultingToAKind()
    {
        var command = Frames.Command("op-3", "browser.contexts", deadline: DateTimeOffset.UtcNow.AddSeconds(30));
        command.Caller = new Proto.BrowserCallerIdentityMessage
        {
            Kind = "root",
            CallerId = "agent-1",
            TaskId = "run-1",
        };

        var decoded = CoreFrameMapping.Decode(command, DesktopId);

        Assert.False(decoded.IsSuccess);
        Assert.Contains("not a registered caller kind", decoded.Error!.Message ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void AUserCallerCannotBeUsedOnABrowserCapability()
    {
        var command = Frames.Command("op-4", "browser.contexts", deadline: DateTimeOffset.UtcNow.AddSeconds(30));
        command.Caller = new Proto.BrowserCallerIdentityMessage
        {
            Kind = "user",
            CallerId = "desktop-ui",
            TaskId = "run-1",
        };

        var decoded = CoreFrameMapping.Decode(command, DesktopId);

        Assert.False(decoded.IsSuccess);
        Assert.Contains("cannot be admitted as a User caller", decoded.Error!.Message ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void ACallerWithoutARunIdentityIsRefused()
    {
        var command = Frames.Command("op-5", "browser.snapshot", deadline: DateTimeOffset.UtcNow.AddSeconds(30));
        command.Caller = new Proto.BrowserCallerIdentityMessage
        {
            Kind = "agent",
            CallerId = "agent-1",
        };

        var decoded = CoreFrameMapping.Decode(command, DesktopId);

        Assert.False(decoded.IsSuccess);
        Assert.Contains("no run identity", decoded.Error!.Message ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void ASubAgentCallerWithoutItsParentIsRefused()
    {
        // 子代理身份必须带父任务（授权子集校验的前提）；类型层不允许，解码也必须拒绝。
        var command = Frames.Command("op-6", "browser.contexts", deadline: DateTimeOffset.UtcNow.AddSeconds(30));
        command.Caller = new Proto.BrowserCallerIdentityMessage
        {
            Kind = "sub_agent",
            CallerId = "agent-1",
            TaskId = "run-sub",
        };

        var decoded = CoreFrameMapping.Decode(command, DesktopId);

        Assert.False(decoded.IsSuccess);
        Assert.Contains("caller identity is invalid", decoded.Error!.Message ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void AShellCapabilityStillWorksWithoutACaller()
    {
        // §3.2：非浏览器（Shell）能力不要求 browser caller —— 不能把闸门扩大到整条通道。
        var command = Frames.NotificationCommand("op-7").Command;
        command.Caller = null;

        var decoded = CoreFrameMapping.Decode(command, DesktopId);

        Assert.True(decoded.IsSuccess, "decode should succeed for a trusted caller");
        Assert.Null(decoded.Value.Context.BrowserCaller);
    }
}
