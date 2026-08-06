using Microsoft.EntityFrameworkCore;
using PuddingCode.Models;
using PuddingPlatform.Data;
using PuddingPlatform.Services.MessageFabric;

namespace PuddingPlatformTests.Services.MessageFabric;

[TestClass]
public sealed class MessageFabricStoreTests
{
    [TestMethod]
    public async Task PersistRouteAsync_Writes_OneRoomMessage_And_MultipleDeliveries()
    {
        using var temp = TemporaryDirectory.Create();
        var options = CreateOptions(temp.Path);

        await using (var db = new PlatformDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            var store = new MessageFabricStore(db);

            await store.PersistRouteAsync("default", RoutePlan(), CancellationToken.None);
            await store.PersistRouteAsync("default", RoutePlan(), CancellationToken.None);
        }

        await using var verifyDb = new PlatformDbContext(options);
        Assert.AreEqual(1, await verifyDb.RoomMessages.CountAsync());
        Assert.AreEqual(2, await verifyDb.MessageDeliveries.CountAsync());
        Assert.IsTrue(await verifyDb.MessageDeliveries.AllAsync(d => d.Status == MessageDeliveryStatuses.Queued));
    }

    [TestMethod]
    public async Task ListAsync_Returns_InboxItems_For_TargetEndpoint_And_Ack_MarksDelivered()
    {
        using var temp = TemporaryDirectory.Create();
        var options = CreateOptions(temp.Path);

        await using (var db = new PlatformDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            var store = new MessageFabricStore(db);
            await store.PersistRouteAsync("default", RoutePlan(), CancellationToken.None);

            var inbox = await store.ListAsync(new MessageInboxQuery
            {
                Endpoint = new MessageAddress { Kind = MessageEndpointKinds.Agent, Id = "assistant" },
                WorkspaceId = "default",
                RoomId = "room-default",
            });

            Assert.AreEqual(1, inbox.Count);
            Assert.AreEqual("assistant", inbox[0].Target.Id);
            Assert.AreEqual("hello all", inbox[0].Content);
            Assert.AreEqual(MessageDeliveryStatuses.Queued, inbox[0].Status);

            await store.AckAsync(inbox[0].DeliveryId);

            var pending = await store.ListAsync(new MessageInboxQuery
            {
                Endpoint = new MessageAddress { Kind = MessageEndpointKinds.Agent, Id = "assistant" },
                WorkspaceId = "default",
                RoomId = "room-default",
            });
            Assert.AreEqual(0, pending.Count);

            var delivered = await store.ListAsync(new MessageInboxQuery
            {
                Endpoint = new MessageAddress { Kind = MessageEndpointKinds.Agent, Id = "assistant" },
                WorkspaceId = "default",
                RoomId = "room-default",
                IncludeDelivered = true,
            });
            Assert.AreEqual(1, delivered.Count);
            Assert.AreEqual(MessageDeliveryStatuses.Delivered, delivered[0].Status);
            Assert.IsNotNull(delivered[0].AckAt);
        }
    }

    [TestMethod]
    public async Task ClaimNextAsync_RestoresGatewayConversationFactsAndMetadata()
    {
        using var temp = TemporaryDirectory.Create();
        var options = CreateOptions(temp.Path);

        await using var db = new PlatformDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var store = new MessageFabricStore(db);
        var route = RoutePlan();
        route = route with
        {
            RoomMessage = route.RoomMessage with
            {
                ConversationId = "conversation-1",
                ReplyToMessageId = "om_external",
                CorrelationId = "oc_chat",
                CausationId = "om_external",
                Metadata = new Dictionary<string, string>
                {
                    ["gateway_ingress"] = "true",
                    ["gateway_channel_type"] = "feishu",
                    ["gateway_connector_id"] = "feishu:assistant",
                },
            },
        };
        await store.PersistRouteAsync("default", route, CancellationToken.None);

        var claimed = await store.ClaimNextAsync(
            new MessageClaimRequest
            {
                Endpoint = new MessageAddress
                {
                    Kind = MessageEndpointKinds.Agent,
                    Id = "assistant",
                },
                WorkspaceId = "default",
                ExecutionId = "gateway-execution",
            },
            CancellationToken.None);

        Assert.IsNotNull(claimed);
        Assert.AreEqual("conversation-1", claimed!.ConversationId);
        Assert.AreEqual("om_external", claimed.ReplyToMessageId);
        Assert.AreEqual("oc_chat", claimed.CorrelationId);
        Assert.AreEqual("om_external", claimed.CausationId);
        Assert.AreEqual("true", claimed.Metadata["gateway_ingress"]);
        Assert.AreEqual("feishu", claimed.Metadata["gateway_channel_type"]);
    }

    [TestMethod]
    public async Task ClaimNextAsync_MarksDeliveryDelivering_WithLeaseAndExecutionId()
    {
        using var temp = TemporaryDirectory.Create();
        var options = CreateOptions(temp.Path);

        await using var db = new PlatformDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var store = new MessageFabricStore(db);
        await store.PersistRouteAsync("default", RoutePlan(), CancellationToken.None);

        var claimed = await store.ClaimNextAsync(new MessageClaimRequest
        {
            Endpoint = new MessageAddress { Kind = MessageEndpointKinds.Agent, Id = "assistant" },
            WorkspaceId = "default",
            RoomId = "room-default",
            ExecutionId = "exec-1",
            LeaseDuration = TimeSpan.FromMinutes(5),
        }, CancellationToken.None);

        Assert.IsNotNull(claimed);
        Assert.AreEqual("d1", claimed!.DeliveryId);
        Assert.AreEqual(MessageDeliveryStatuses.Delivering, claimed.Status);
        Assert.AreEqual("exec-1", claimed.ClaimedByExecutionId);
        Assert.IsNotNull(claimed.LeaseUntil);

        var second = await store.ClaimNextAsync(new MessageClaimRequest
        {
            Endpoint = new MessageAddress { Kind = MessageEndpointKinds.Agent, Id = "assistant" },
            WorkspaceId = "default",
            ExecutionId = "exec-2",
        }, CancellationToken.None);

        Assert.IsNull(second);
    }

    [TestMethod]
    public async Task ListPendingTargetsAsync_ReturnsDistinctQueuedAndRetryingAgentScopes()
    {
        using var temp = TemporaryDirectory.Create();
        var options = CreateOptions(temp.Path);

        await using var db = new PlatformDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var store = new MessageFabricStore(db);
        await store.PersistRouteAsync("default", RoutePlan(), CancellationToken.None);

        await store.AckAsync("d2", CancellationToken.None);
        var targets = await store.ListPendingTargetsAsync(
            MessageEndpointKinds.Agent,
            CancellationToken.None);

        Assert.HasCount(1, targets);
        Assert.AreEqual("default", targets[0].WorkspaceId);
        Assert.AreEqual("room-default", targets[0].RoomId);
        Assert.AreEqual(MessageEndpointKinds.Agent, targets[0].TargetKind);
        Assert.AreEqual("assistant", targets[0].TargetId);
    }

    [TestMethod]
    public async Task RetryAsync_RequeuesDeliveryAfterAvailableAt_AndDeadLetterAsyncStopsClaim()
    {
        using var temp = TemporaryDirectory.Create();
        var options = CreateOptions(temp.Path);

        await using var db = new PlatformDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var store = new MessageFabricStore(db);
        await store.PersistRouteAsync("default", RoutePlan(), CancellationToken.None);

        var claimed = await store.ClaimNextAsync(new MessageClaimRequest
        {
            Endpoint = new MessageAddress { Kind = MessageEndpointKinds.Agent, Id = "assistant" },
            WorkspaceId = "default",
            ExecutionId = "exec-1",
        }, CancellationToken.None);

        Assert.IsNotNull(claimed);
        await store.RetryAsync(
            claimed!.DeliveryId,
            "exec-1",
            "transient",
            DateTimeOffset.UtcNow.AddMilliseconds(-1),
            CancellationToken.None);

        var retry = await store.ClaimNextAsync(new MessageClaimRequest
        {
            Endpoint = new MessageAddress { Kind = MessageEndpointKinds.Agent, Id = "assistant" },
            WorkspaceId = "default",
            ExecutionId = "exec-2",
        }, CancellationToken.None);

        Assert.IsNotNull(retry);
        Assert.AreEqual(2, retry!.AttemptCount);

        await store.DeadLetterAsync(retry.DeliveryId, "exec-2", "terminal", CancellationToken.None);

        var afterDeadLetter = await store.ClaimNextAsync(new MessageClaimRequest
        {
            Endpoint = new MessageAddress { Kind = MessageEndpointKinds.Agent, Id = "assistant" },
            WorkspaceId = "default",
            ExecutionId = "exec-3",
        }, CancellationToken.None);

        Assert.IsNull(afterDeadLetter);
    }

    [TestMethod]
    public async Task AckAsync_ClearsPreviousLastError()
    {
        using var temp = TemporaryDirectory.Create();
        var options = CreateOptions(temp.Path);

        await using var db = new PlatformDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var store = new MessageFabricStore(db);
        await store.PersistRouteAsync("default", RoutePlan(), CancellationToken.None);

        var firstClaim = await store.ClaimNextAsync(new MessageClaimRequest
        {
            Endpoint = new MessageAddress { Kind = MessageEndpointKinds.Agent, Id = "assistant" },
            WorkspaceId = "default",
            ExecutionId = "exec-1",
        }, CancellationToken.None);
        Assert.IsNotNull(firstClaim);

        await store.RetryAsync(
            firstClaim!.DeliveryId,
            "exec-1",
            "transient failure",
            DateTimeOffset.UtcNow.AddMilliseconds(-1),
            CancellationToken.None);

        var secondClaim = await store.ClaimNextAsync(new MessageClaimRequest
        {
            Endpoint = new MessageAddress { Kind = MessageEndpointKinds.Agent, Id = "assistant" },
            WorkspaceId = "default",
            ExecutionId = "exec-2",
        }, CancellationToken.None);
        Assert.IsNotNull(secondClaim);

        await store.AckAsync(secondClaim!.DeliveryId, "exec-2", CancellationToken.None);

        var delivered = await db.MessageDeliveries.SingleAsync(delivery => delivery.DeliveryId == secondClaim.DeliveryId);
        Assert.AreEqual(MessageDeliveryStatuses.Delivered, delivered.Status);
        Assert.IsNull(delivered.LastError);
    }

    [TestMethod]
    public async Task RecoverExpiredLeasesAsync_RequeuesExpiredDeliveringDeliveryWithoutIncrementingAttempt()
    {
        using var temp = TemporaryDirectory.Create();
        var options = CreateOptions(temp.Path);

        await using var db = new PlatformDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var store = new MessageFabricStore(db);
        await store.PersistRouteAsync("default", RoutePlan(), CancellationToken.None);

        var claimed = await store.ClaimNextAsync(new MessageClaimRequest
        {
            Endpoint = new MessageAddress { Kind = MessageEndpointKinds.Agent, Id = "assistant" },
            WorkspaceId = "default",
            ExecutionId = "exec-1",
            LeaseDuration = TimeSpan.FromMilliseconds(-1),
        }, CancellationToken.None);

        Assert.IsNotNull(claimed);
        var recovered = await store.RecoverExpiredLeasesAsync(DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.AreEqual(1, recovered);

        var pending = await store.ListAsync(new MessageInboxQuery
        {
            Endpoint = new MessageAddress { Kind = MessageEndpointKinds.Agent, Id = "assistant" },
            WorkspaceId = "default",
            IncludeDelivered = true,
        }, CancellationToken.None);

        Assert.AreEqual(MessageDeliveryStatuses.Retrying, pending[0].Status);
        Assert.AreEqual(1, pending[0].AttemptCount);
        Assert.IsNull(pending[0].ClaimedByExecutionId);
        Assert.IsNull(pending[0].LeaseUntil);
        Assert.IsNotNull(pending[0].AvailableAt);

        var reclaimed = await store.ClaimNextAsync(new MessageClaimRequest
        {
            Endpoint = new MessageAddress { Kind = MessageEndpointKinds.Agent, Id = "assistant" },
            WorkspaceId = "default",
            ExecutionId = "exec-2",
        }, CancellationToken.None);

        Assert.IsNotNull(reclaimed);
        Assert.AreEqual(2, reclaimed!.AttemptCount);
    }

    [TestMethod]
    public async Task RenewLeaseAsync_ExtendsOnlyTheCurrentExecutionOwner()
    {
        using var temp = TemporaryDirectory.Create();
        var options = CreateOptions(temp.Path);

        await using var db = new PlatformDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var store = new MessageFabricStore(db);
        await store.PersistRouteAsync("default", RoutePlan(), CancellationToken.None);

        var claimed = await store.ClaimNextAsync(new MessageClaimRequest
        {
            Endpoint = new MessageAddress { Kind = MessageEndpointKinds.Agent, Id = "assistant" },
            WorkspaceId = "default",
            ExecutionId = "exec-owner",
            LeaseDuration = TimeSpan.FromMinutes(1),
        }, CancellationToken.None);

        Assert.IsNotNull(claimed);
        var originalLease = claimed!.LeaseUntil;
        Assert.IsFalse(await store.RenewLeaseAsync(
            claimed.DeliveryId,
            "exec-stale",
            TimeSpan.FromMinutes(5),
            CancellationToken.None));
        Assert.IsTrue(await store.RenewLeaseAsync(
            claimed.DeliveryId,
            "exec-owner",
            TimeSpan.FromMinutes(5),
            CancellationToken.None));

        db.ChangeTracker.Clear();
        var renewed = await db.MessageDeliveries.AsNoTracking()
            .SingleAsync(delivery => delivery.DeliveryId == claimed.DeliveryId);
        Assert.IsGreaterThan(originalLease!.Value, renewed.LeaseUntil!.Value);
        Assert.AreEqual("exec-owner", renewed.ClaimedByExecutionId);
    }

    [TestMethod]
    public async Task StaleExecutionCannotMutateDeliveryAfterRecoveryAndReclaim()
    {
        using var temp = TemporaryDirectory.Create();
        var options = CreateOptions(temp.Path);

        await using var db = new PlatformDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var store = new MessageFabricStore(db);
        await store.PersistRouteAsync("default", RoutePlan(), CancellationToken.None);

        var first = await store.ClaimNextAsync(new MessageClaimRequest
        {
            Endpoint = new MessageAddress { Kind = MessageEndpointKinds.Agent, Id = "assistant" },
            WorkspaceId = "default",
            ExecutionId = "exec-stale",
            LeaseDuration = TimeSpan.FromMilliseconds(-1),
        }, CancellationToken.None);
        Assert.IsNotNull(first);
        Assert.AreEqual(1, await store.RecoverExpiredLeasesAsync(DateTimeOffset.UtcNow, CancellationToken.None));

        var current = await store.ClaimNextAsync(new MessageClaimRequest
        {
            Endpoint = new MessageAddress { Kind = MessageEndpointKinds.Agent, Id = "assistant" },
            WorkspaceId = "default",
            ExecutionId = "exec-current",
            LeaseDuration = TimeSpan.FromMinutes(5),
        }, CancellationToken.None);
        Assert.IsNotNull(current);

        await store.AckAsync(current!.DeliveryId, "exec-stale", CancellationToken.None);
        await store.RetryAsync(
            current.DeliveryId,
            "exec-stale",
            "stale retry",
            DateTimeOffset.UtcNow,
            CancellationToken.None);
        await store.DeadLetterAsync(
            current.DeliveryId,
            "exec-stale",
            "stale dead letter",
            CancellationToken.None);

        db.ChangeTracker.Clear();
        var afterStaleMutations = await db.MessageDeliveries.AsNoTracking()
            .SingleAsync(delivery => delivery.DeliveryId == current.DeliveryId);
        Assert.AreEqual(MessageDeliveryStatuses.Delivering, afterStaleMutations.Status);
        Assert.AreEqual("exec-current", afterStaleMutations.ClaimedByExecutionId);
        Assert.IsNull(afterStaleMutations.AckAt);
        Assert.IsNull(afterStaleMutations.LastError);

        await store.AckAsync(current.DeliveryId, "exec-current", CancellationToken.None);
        db.ChangeTracker.Clear();
        Assert.AreEqual(
            MessageDeliveryStatuses.Delivered,
            (await db.MessageDeliveries.AsNoTracking()
                .SingleAsync(delivery => delivery.DeliveryId == current.DeliveryId)).Status);
    }

    private static DbContextOptions<PlatformDbContext> CreateOptions(string root)
    {
        var dbPath = Path.Combine(root, "platform.db");
        return new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;
    }

    private static MessageRoutePlan RoutePlan() => new()
    {
        MessageId = "m1",
        RoomMessage = new RoomMessageDraft
        {
            RoomId = "room-default",
            MessageId = "m1",
            From = new MessageAddress
            {
                Kind = MessageEndpointKinds.User,
                Id = "owner",
                WorkspaceId = "default",
                DisplayName = "Owner",
            },
            Audience = MessageAudiences.Broadcast,
            Visibility = MessageVisibilities.Public,
            Content = "hello all",
            CreatedAt = 100,
        },
        Deliveries =
        [
            new MessageDeliveryDraft
            {
                DeliveryId = "d1",
                MessageId = "m1",
                Target = new MessageAddress
                {
                    Kind = MessageEndpointKinds.Agent,
                    Id = "assistant",
                    WorkspaceId = "default",
                    DisplayName = "Default Assistant",
                },
                Priority = 5,
            },
            new MessageDeliveryDraft
            {
                DeliveryId = "d2",
                MessageId = "m1",
                Target = new MessageAddress
                {
                    Kind = MessageEndpointKinds.Agent,
                    Id = "consultant",
                    WorkspaceId = "default",
                    DisplayName = "Consultant",
                },
                Priority = 5,
            },
        ],
    };

    private sealed class TemporaryDirectory : IDisposable
    {
        private TemporaryDirectory(string path) => Path = path;

        public string Path { get; }

        public static TemporaryDirectory Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "pudding-platform-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TemporaryDirectory(path);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // Best-effort test cleanup.
            }
        }
    }
}
