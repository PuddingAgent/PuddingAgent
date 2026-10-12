using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using Pudding.DesktopConnection;
using Proto = Pudding.Rpc.Protocol.V1;

namespace DesktopConnectionTests;

public sealed class BrowserDeliveryReviewConnection
{
    private static Proto.CapabilityCommand Binding(string caller = "agent-a", string page = "page-a")
    {
        var identity = new Proto.BrowserCallerIdentityMessage { Kind = "agent", CallerId = caller, TaskId = "run-" + caller };
        return new Proto.CapabilityCommand
        {
            OperationId = "review-op", Generation = 1, Capability = "browser.binding.apply", Caller = identity.Clone(),
            Deadline = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(1)),
            BindingApply = new Proto.BrowserBindingApplyCommand
            {
                Task = identity, Target = new Proto.CommandTarget { ContextId = "ctx", PageId = page },
                Scope = 1, ValidityMs = 300000, Source = "review"
            }
        };
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChildBindingMustPreserveParentRunOnApplyAndRevoke(bool revoke)
    {
        var command = Binding();
        command.Caller.Kind = command.BindingApply.Task.Kind = "sub_agent";
        command.Caller.ParentTaskId = command.BindingApply.Task.ParentTaskId = "parent-run";
        if (revoke)
        {
            var task = command.BindingApply.Task.Clone();
            var target = command.BindingApply.Target.Clone();
            command.Capability = "browser.binding.revoke";
            command.BindingRevoke = new Proto.BrowserBindingRevokeCommand { Task = task, Target = target };
        }
        var decoded = CoreFrameMapping.Decode(command, new DesktopInstanceId("desk"));
        Assert.True(decoded.IsSuccess, decoded.IsFailure ? decoded.Error.Message : null);
        var actor = revoke ? decoded.Value.Request.BindingRevoke!.Task : decoded.Value.Request.BindingApply!.Binding.Task;
        Assert.Equal("parent-run", actor.ParentTaskId);
    }

    [Theory]
    [InlineData("agent-b", "page-a")]
    [InlineData("agent-a", "page-b")]
    public void IdempotencyFingerprintMustIncludeCallerAndCompletePayload(string caller, string page)
    {
        var a = CoreFrameMapping.Decode(Binding(), new DesktopInstanceId("desk"));
        var b = CoreFrameMapping.Decode(Binding(caller, page), new DesktopInstanceId("desk"));
        Assert.True(a.IsSuccess);
        Assert.True(b.IsSuccess);
        Assert.NotEqual(a.Value.Fingerprint, b.Value.Fingerprint);
    }
}
