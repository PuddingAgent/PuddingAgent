using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using Pudding.DesktopConnection;
using Proto = Pudding.Rpc.Protocol.V1;

namespace DesktopConnectionTests;

/// <summary>控制面解码（§4.1）：载荷与能力必须匹配；结构非法（范围空/期限非正/来源空）⇒ 类型化失败。</summary>
public sealed class BrowserBindingDecodeTests
{
    private static readonly DesktopInstanceId DesktopId = new("desk-1");

    private static Proto.BrowserCallerIdentityMessage Caller() => new()
    {
        Kind = "agent",
        CallerId = "agent-1",
        TaskId = "run-1",
    };

    private static Proto.CoreFrame ApplyFrame(
        uint scope = 1,
        long validityMs = 300000,
        string source = "user-goal:g-1") => new()
    {
        Command = new Proto.CapabilityCommand
        {
            Caller = Caller(),
            OperationId = "op-apply",
            Generation = 1,
            Capability = "browser.binding.apply",
            Deadline = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(
                DateTimeOffset.UtcNow.AddSeconds(30)),
            BindingApply = new Proto.BrowserBindingApplyCommand
            {
                Task = Caller(),
                Target = new Proto.CommandTarget { ContextId = "ctx-1", PageId = "page-1" },
                Scope = scope,
                ValidityMs = validityMs,
                Source = source,
            },
        },
    };

    [Fact]
    public void Decode_BindingApply_ProducesTheTaskBinding()
    {
        var decoded = CoreFrameMapping.Decode(ApplyFrame().Command, DesktopId);

        Assert.True(decoded.IsSuccess);
        var binding = decoded.Value.Request.BindingApply!.Binding;
        Assert.Equal("run-1", binding.Task.TaskId);
        Assert.Equal(BrowserCallerKind.Agent, binding.Task.Kind);
        Assert.Equal("ctx-1/page-1", binding.Target.Key);
        Assert.Equal(BrowserPageGrantScope.Read, binding.Scope);
        Assert.Equal(TimeSpan.FromMinutes(5), binding.Validity);
        Assert.Equal("user-goal:g-1", binding.Source);
    }

    [Fact]
    public void Decode_BindingApply_WithAMismatchedPayloadIsRefused()
    {
        var frame = ApplyFrame();
        frame.Command.BindingApply = null;
        frame.Command.Navigate = new Proto.NavigateCommand
        {
            Target = new Proto.CommandTarget { ContextId = "ctx-1", PageId = "page-1" },
            Url = "https://example.com/",
        };

        var decoded = CoreFrameMapping.Decode(frame.Command, DesktopId);

        Assert.True(decoded.IsFailure);
    }

    [Theory]
    [InlineData(0u, 300000L)]      // 范围为空 ⇒ 不签发"没有能力的授权"
    [InlineData(1u, 0L)]           // 期限非正
    [InlineData(1u, -1L)]
    public void Decode_BindingApply_WithAnInvalidStructureIsRefused(uint scope, long validityMs)
    {
        var decoded = CoreFrameMapping.Decode(ApplyFrame(scope, validityMs).Command, DesktopId);

        Assert.True(decoded.IsFailure);
    }

    [Fact]
    public void Decode_BindingRevoke_ProducesTaskAndTarget()
    {
        var frame = new Proto.CoreFrame
        {
            Command = new Proto.CapabilityCommand
            {
                Caller = Caller(),
                OperationId = "op-revoke",
                Generation = 1,
                Capability = "browser.binding.revoke",
                Deadline = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(
                    DateTimeOffset.UtcNow.AddSeconds(30)),
                BindingRevoke = new Proto.BrowserBindingRevokeCommand
                {
                    Task = Caller(),
                    Target = new Proto.CommandTarget { ContextId = "ctx-1", PageId = "page-1" },
                },
            },
        };

        var decoded = CoreFrameMapping.Decode(frame.Command, DesktopId);

        Assert.True(decoded.IsSuccess);
        var revoke = decoded.Value.Request.BindingRevoke!;
        Assert.Equal("run-1", revoke.Task.TaskId);
        Assert.Equal("ctx-1/page-1", revoke.Target.Key);
    }
}