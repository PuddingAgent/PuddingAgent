using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Observability;
using PuddingRuntime.Services.Hooks;

namespace PuddingRuntimeTests.Services;

[TestClass]
public sealed class HookPublisherTests
{
    [TestMethod]
    public void HookEventNames_ShouldExpose_SessionCompressed()
    {
        Assert.AreEqual("session.compressed", HookEventNames.SessionCompressed.Value);
    }

    [TestMethod]
    public void SessionCompressedHookPayload_ShouldCarry_CompactionIdentity()
    {
        var payload = new SessionCompressedHookPayload
        {
            WorkspaceId = "ws-1",
            OriginalSessionId = "s-old",
            NewSessionId = "s-new",
            AgentId = "agent-1",
            AgentTemplateId = "tpl-1",
            CompactionId = "cmp-1",
            Mode = "Manual",
            Level = "Full",
            Reason = "manual compact",
        };

        Assert.AreEqual("ws-1", payload.WorkspaceId);
        Assert.AreEqual("s-old", payload.OriginalSessionId);
        Assert.AreEqual("cmp-1", payload.CompactionId);
    }

    [TestMethod]
    public async Task PublishAsync_ShouldMapHookToInternalEventAndAuditActivity()
    {
        var eventBus = new RecordingInternalEventBus();
        var activitySink = new RecordingRuntimeActivitySink();
        var publisher = new HookPublisher(eventBus, activitySink);
        var payload = new SessionCompressedHookPayload
        {
            WorkspaceId = "ws-1",
            OriginalSessionId = "s-old",
            NewSessionId = "s-new",
            CompactionId = "cmp-1",
            Mode = "Manual",
            Level = "Full",
            Reason = "manual compact",
        };

        var eventId = await publisher.PublishAsync(
            HookEventNames.SessionCompressed,
            payload,
            new HookPublishOptions
            {
                WorkspaceId = "ws-1",
                SessionId = "s-new",
                AgentId = "agent-1",
                SourceId = "context_compaction",
                IdempotencyKey = "hook:cmp-1",
                CausationId = "cause-1",
                Priority = EventPriorityLevel.Important,
            });

        Assert.AreEqual(eventBus.Published.Single().EventId, eventId);

        var evt = eventBus.Published.Single();
        Assert.AreEqual("session.compressed", evt.Type);
        Assert.AreEqual("ws-1", evt.WorkspaceId);
        Assert.AreEqual("s-new", evt.SessionId);
        Assert.AreEqual("agent-1", evt.AgentId);
        Assert.AreEqual(EventPriorityLevel.Important, evt.Priority);
        Assert.AreEqual("framework", evt.Source.SourceType);
        Assert.AreEqual("context_compaction", evt.Source.SourceId);
        Assert.AreEqual("cause-1", evt.CausationId);
        Assert.IsInstanceOfType<SessionCompressedHookPayload>(evt.Payload);
        Assert.AreEqual("hook:cmp-1", evt.Metadata?["idempotency_key"]);

        var activity = activitySink.Activities.Single();
        Assert.AreEqual(RuntimeActivityComponents.HookSystem, activity.Component);
        Assert.AreEqual("hook.publish", activity.Operation);
        Assert.AreEqual(RuntimeActivityStatuses.Succeeded, activity.Status);
        Assert.AreEqual("session.compressed", activity.Metadata?["hook_event"]);
        Assert.AreEqual(eventId, activity.Metadata?["event_id"]);
    }

    private sealed class RecordingInternalEventBus : IInternalEventBus
    {
        public List<InternalEvent> Published { get; } = [];

        public Task PublishAsync(InternalEvent evt, CancellationToken ct = default)
        {
            Published.Add(evt);
            return Task.CompletedTask;
        }

        public Task<IEventSubscriptionHandle> SubscribeAsync(
            string eventTypePattern,
            Func<InternalEvent, Task> handler,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task UnsubscribeAsync(IEventSubscriptionHandle handle)
            => throw new NotSupportedException();
    }

    private sealed class RecordingRuntimeActivitySink : IRuntimeActivitySink
    {
        public List<RuntimeActivity> Activities { get; } = [];

        public Task RecordAsync(RuntimeActivity activity, CancellationToken ct = default)
        {
            Activities.Add(activity);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<RuntimeActivity>> QueryAsync(RuntimeActivityQuery query, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RuntimeActivity>>(Activities);
    }
}
