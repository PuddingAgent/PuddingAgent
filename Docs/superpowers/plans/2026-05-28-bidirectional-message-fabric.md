# Bidirectional Message Fabric Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace patch-style chat fan-out with a bidirectional message fabric where users, agents, connectors, and system jobs are first-class room participants and message endpoints.

**Architecture:** Add core message contracts in `PuddingCore`, implement routing, inbox, and persistence in `PuddingPlatform`, bridge deliveries into `IInternalEventBus`, and let Runtime agents send messages through `send_message` or pull pending deliveries through `receive_messages`. Admin Chat becomes a room client that observes messages and deliveries; it is not required for message delivery.

**Tech Stack:** .NET 10, MSTest, EF Core SQLite, existing `PuddingCore`, `PuddingPlatform`, `PuddingAgent`, `PuddingRuntime`, React/TypeScript Admin frontend.

**Gateway/Connector refinement, 2026-05-28:** Chatroom is a Message Client, not a connector. HTTP/WebSocket/MQTT/CLI/Email are connectors. Gateway owns auth, workspace binding, normalization, rate limiting, backpressure, and ingress/egress correlation. Message Fabric owns route decisions. Event system owns delivery mechanics. Execution engine only consumes deliveries and replies through `send_message`.

---

## Implementation Snapshot

Checked on 2026-05-28:

- `Docs/07架构/46ADR-045双向消息系统与聊天室客户端ADR.md` exists and defines the target architecture.
- `Source/PuddingPlatform/Services/ChatRoomRouteResolver.cs` exists as a server-side `@agent/@all` spike.
- `Source/PuddingPlatform/Controllers/Api/ChatApiController.cs` contains secondary fan-out logic that must be retired, not expanded.
- `Source/PuddingPlatformAdmin/src/pages/chat/hooks/chatRouting.ts` and `useChatState.ts` contain frontend route/fan-out assumptions that must become client hints only.
- `Source/PuddingCore/Platform/MessageContracts.cs` currently models ingress into execution, not a general bidirectional message system.
- `Source/PuddingAgent/Services/Events/AgentEventHandler.cs` already advertises future `message.*` handling and can become the event-to-runtime bridge.
- ADR-045 defines Message Fabric as a higher-level domain wrapper over the existing event system. The event system remains responsible for priority, queueing, retry, dead-letter, and subscription mechanics.
- ADR-045 now defines the improved Gateway/Connector split: `Message Client -> Connector -> Gateway Boundary -> IMessageSystem -> IInternalEventBus -> Execution Engine`, with reverse egress through `send_message -> IMessageSystem -> GatewayEgress -> Connector.SendAsync`.
- Existing relevant tests:
  - `Source/PuddingPlatformTests/Services/ChatRoomRouteResolverTests.cs`
  - `Source/PuddingCoreTests/PuddingCoreTests.csproj`
  - `Source/PuddingPlatformTests/PuddingPlatformTests.csproj`

---

## File Map

- Create: `Source/PuddingCore/Models/MessageFabricModels.cs`
  - Owns `MessageAddress`, `RoomParticipant`, `MessageEnvelope`, route plans, delivery status constants, and payload records.
- Create: `Source/PuddingCore/Abstractions/IMessageSystem.cs`
  - Unified async message send interface.
- Create: `Source/PuddingCore/Abstractions/IMessageRouter.cs`
  - Pure routing contract independent of EF, HTTP, UI, and Runtime execution.
- Create: `Source/PuddingCore/Abstractions/IMessageInbox.cs`
  - Pull-based endpoint inbox contract for agents and clients.
- Create: `Source/PuddingCoreTests/MessageFabric/MessageFabricModelTests.cs`
  - Locks first-class user/agent participant and endpoint semantics.
- Create: `Source/PuddingPlatform/Services/MessageFabric/WorkspaceRoomParticipantResolver.cs`
  - Builds room participants from the current user plus enabled workspace agents.
- Create: `Source/PuddingPlatform/Services/MessageFabric/MessageRouter.cs`
  - Replaces `ChatRoomRouteResolver` as the authoritative room router.
- Create: `Source/PuddingPlatformTests/Services/MessageFabric/WorkspaceRoomParticipantResolverTests.cs`
- Create: `Source/PuddingPlatformTests/Services/MessageFabric/MessageRouterTests.cs`
- Create: `Source/PuddingPlatform/Data/Entities/RoomMessageEntity.cs`
- Create: `Source/PuddingPlatform/Data/Entities/MessageDeliveryEntity.cs`
- Create: `Source/PuddingPlatform/Data/Entities/RoomParticipantEntity.cs`
- Modify: `Source/PuddingPlatform/Data/PlatformDbContext.cs`
  - Adds DbSets and indexes.
- Create: `Source/PuddingPlatform/Services/MessageFabric/MessageFabricStore.cs`
  - Persists room messages, participants, delivery records, and inbox queries.
- Create: `Source/PuddingPlatform/Services/MessageFabric/MessageFabricSchemaBootstrapper.cs`
  - Idempotently creates message fabric tables in existing SQLite databases that predate this feature.
- Create: `Source/PuddingPlatform/Services/MessageFabric/WorkspaceRoomParticipantProvider.cs`
  - Loads current room participants for `MessageSystem` using workspace agents and caller identity.
- Create: `Source/PuddingPlatform/Services/MessageFabric/MessageSystem.cs`
  - Orchestrates routing, persistence, event publishing, and delivery status.
- Modify: `Source/PuddingAgent/Services/Events/AgentEventHandler.cs`
  - Handles `message.deliver` payloads explicitly.
- Create: `Source/PuddingRuntime/Services/Tools/SendMessageTool.cs`
  - Exposes `send_message` as an `IAgentSkill`.
- Create: `Source/PuddingRuntime/Services/Tools/ReceiveMessagesTool.cs`
  - Exposes `receive_messages` as an `IAgentSkill` backed by `IMessageInbox`.
- Modify: `Source/PuddingAgent/Program.cs`
  - Registers message fabric services and `SendMessageTool`.
- Modify: `Source/PuddingPlatform/Controllers/Api/ChatApiController.cs`
  - Submits user messages to `IMessageSystem`; removes direct secondary fan-out.
- Modify: `Source/PuddingPlatformAdmin/src/pages/chat/hooks/useChatState.ts`
  - Stops materializing fan-out turns from `fanout_index`.
- Modify: `Source/PuddingPlatformAdmin/src/pages/chat/hooks/chatRouting.ts`
  - Downgrades frontend routing to mention hinting only, or removes it after the room API lands.

---

## Task 1: Core Message Fabric Contracts

**Files:**
- Create: `Source/PuddingCore/Models/MessageFabricModels.cs`
- Create: `Source/PuddingCore/Abstractions/IMessageSystem.cs`
- Create: `Source/PuddingCore/Abstractions/IMessageRouter.cs`
- Create: `Source/PuddingCore/Abstractions/IMessageInbox.cs`
- Test: `Source/PuddingCoreTests/MessageFabric/MessageFabricModelTests.cs`

- [ ] **Step 1: Write model tests**

Create `Source/PuddingCoreTests/MessageFabric/MessageFabricModelTests.cs`:

```csharp
using PuddingCode.Models;

namespace PuddingCoreTests.MessageFabric;

[TestClass]
public sealed class MessageFabricModelTests
{
    [TestMethod]
    public void RoomParticipant_Allows_User_And_Agent_As_FirstClassParticipants()
    {
        var user = new RoomParticipant
        {
            ParticipantId = "p-user-owner",
            RoomId = "room-default",
            Kind = MessageEndpointKinds.User,
            EndpointId = "owner",
            DisplayName = "Owner",
        };
        var agent = new RoomParticipant
        {
            ParticipantId = "p-agent-assistant",
            RoomId = "room-default",
            Kind = MessageEndpointKinds.Agent,
            EndpointId = "agent.default",
            DisplayName = "Default Assistant",
        };

        Assert.AreEqual(MessageEndpointKinds.User, user.Kind);
        Assert.AreEqual(MessageEndpointKinds.Agent, agent.Kind);
        Assert.IsTrue(user.CanSend);
        Assert.IsTrue(agent.CanSend);
        Assert.IsTrue(user.CanReceive);
        Assert.IsTrue(agent.CanReceive);
    }

    [TestMethod]
    public void BroadcastRoute_Uses_OneRoomMessage_And_MultipleDeliveries()
    {
        var route = new MessageRoutePlan
        {
            MessageId = "m1",
            RoomMessage = new RoomMessageDraft
            {
                RoomId = "room-default",
                MessageId = "m1",
                From = new MessageAddress { Kind = MessageEndpointKinds.User, Id = "owner" },
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
                    Target = new MessageAddress { Kind = MessageEndpointKinds.Agent, Id = "a1" },
                    Priority = 0,
                },
                new MessageDeliveryDraft
                {
                    DeliveryId = "d2",
                    MessageId = "m1",
                    Target = new MessageAddress { Kind = MessageEndpointKinds.Agent, Id = "a2" },
                    Priority = 0,
                },
            ],
        };

        Assert.AreEqual("m1", route.RoomMessage.MessageId);
        Assert.AreEqual(2, route.Deliveries.Count);
    }

    [TestMethod]
    public void InboxItem_Represents_PullBased_Delivery_For_Agent()
    {
        var item = new MessageInboxItem
        {
            DeliveryId = "d1",
            MessageId = "m1",
            WorkspaceId = "default",
            RoomId = "room-default",
            From = new MessageAddress { Kind = MessageEndpointKinds.User, Id = "owner" },
            Target = new MessageAddress { Kind = MessageEndpointKinds.Agent, Id = "assistant" },
            Content = "有新任务",
            Status = MessageDeliveryStatuses.Queued,
            Priority = 5,
            CreatedAt = 100,
        };

        Assert.AreEqual("assistant", item.Target.Id);
        Assert.AreEqual(MessageDeliveryStatuses.Queued, item.Status);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Source\PuddingCoreTests\PuddingCoreTests.csproj --filter MessageFabricModelTests --no-restore --nologo`

Expected: FAIL because `RoomParticipant`, `MessageEndpointKinds`, route models, and inbox models do not exist.

- [ ] **Step 3: Add message fabric models**

Create `Source/PuddingCore/Models/MessageFabricModels.cs`:

```csharp
namespace PuddingCode.Models;

public static class MessageEndpointKinds
{
    public const string User = "user";
    public const string Agent = "agent";
    public const string Room = "room";
    public const string Connector = "connector";
    public const string System = "system";
}

public static class MessageAudiences
{
    public const string Direct = "direct";
    public const string Room = "room";
    public const string Broadcast = "broadcast";
}

public static class MessageVisibilities
{
    public const string Public = "public";
    public const string Private = "private";
    public const string System = "system";
}

public static class MessageDeliveryStatuses
{
    public const string Queued = "queued";
    public const string Delivering = "delivering";
    public const string Delivered = "delivered";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    public const string Expired = "expired";
}

public sealed record MessageAddress
{
    public required string Kind { get; init; }
    public required string Id { get; init; }
    public string? WorkspaceId { get; init; }
    public string? DisplayName { get; init; }
}

public sealed record RoomParticipant
{
    public required string ParticipantId { get; init; }
    public required string RoomId { get; init; }
    public required string Kind { get; init; }
    public required string EndpointId { get; init; }
    public string? DisplayName { get; init; }
    public string? AvatarUrl { get; init; }
    public bool CanSend { get; init; } = true;
    public bool CanReceive { get; init; } = true;
    public string Status { get; init; } = "available";
}

public sealed record MessageEnvelope
{
    public string MessageId { get; init; } = Guid.NewGuid().ToString("N");
    public required MessageAddress From { get; init; }
    public required IReadOnlyList<MessageAddress> To { get; init; }
    public string? RoomId { get; init; }
    public string? ConversationId { get; init; }
    public string? ReplyToMessageId { get; init; }
    public string? CorrelationId { get; init; }
    public string? CausationId { get; init; }
    public required string Audience { get; init; }
    public required string Visibility { get; init; }
    public string ContentType { get; init; } = "text";
    public required string Content { get; init; }
    public int Priority { get; init; }
    public long CreatedAt { get; init; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
}

public sealed record RoomMessageDraft
{
    public required string RoomId { get; init; }
    public required string MessageId { get; init; }
    public required MessageAddress From { get; init; }
    public required string Audience { get; init; }
    public required string Visibility { get; init; }
    public required string Content { get; init; }
    public required long CreatedAt { get; init; }
}

public sealed record MessageDeliveryDraft
{
    public required string DeliveryId { get; init; }
    public required string MessageId { get; init; }
    public required MessageAddress Target { get; init; }
    public int Priority { get; init; }
}

public sealed record MessageInboxQuery
{
    public required MessageAddress Endpoint { get; init; }
    public string? WorkspaceId { get; init; }
    public string? RoomId { get; init; }
    public int Limit { get; init; } = 20;
    public bool IncludeDelivered { get; init; }
}

public sealed record MessageInboxItem
{
    public required string DeliveryId { get; init; }
    public required string MessageId { get; init; }
    public required string WorkspaceId { get; init; }
    public string? RoomId { get; init; }
    public required MessageAddress From { get; init; }
    public required MessageAddress Target { get; init; }
    public required string Content { get; init; }
    public required string Status { get; init; }
    public int Priority { get; init; }
    public long CreatedAt { get; init; }
    public long? ReadAt { get; init; }
    public long? AckAt { get; init; }
}

public sealed record MessageRoutePlan
{
    public required string MessageId { get; init; }
    public required RoomMessageDraft RoomMessage { get; init; }
    public required IReadOnlyList<MessageDeliveryDraft> Deliveries { get; init; }
}

public sealed record MessageSendResult
{
    public required string MessageId { get; init; }
    public required string? RoomId { get; init; }
    public required IReadOnlyList<string> DeliveryIds { get; init; }
}

public sealed record MessageDeliverEventPayload
{
    public required string MessageId { get; init; }
    public required string DeliveryId { get; init; }
    public required string WorkspaceId { get; init; }
    public required string? RoomId { get; init; }
    public required MessageAddress From { get; init; }
    public required MessageAddress Target { get; init; }
    public required string Content { get; init; }
    public string? ReplyToMessageId { get; init; }
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
}
```

- [ ] **Step 4: Add core abstractions**

Create `Source/PuddingCore/Abstractions/IMessageSystem.cs`:

```csharp
using PuddingCode.Models;

namespace PuddingCode.Abstractions;

public interface IMessageSystem
{
    Task<MessageSendResult> SendAsync(MessageEnvelope envelope, CancellationToken ct = default);
}
```

Create `Source/PuddingCore/Abstractions/IMessageRouter.cs`:

```csharp
using PuddingCode.Models;

namespace PuddingCode.Abstractions;

public interface IMessageRouter
{
    Task<MessageRoutePlan> RouteAsync(
        MessageEnvelope envelope,
        IReadOnlyList<RoomParticipant> participants,
        CancellationToken ct = default);
}
```

Create `Source/PuddingCore/Abstractions/IMessageInbox.cs`:

```csharp
using PuddingCode.Models;

namespace PuddingCode.Abstractions;

public interface IMessageInbox
{
    Task<IReadOnlyList<MessageInboxItem>> ListAsync(MessageInboxQuery query, CancellationToken ct = default);
    Task AckAsync(string deliveryId, CancellationToken ct = default);
}
```

- [ ] **Step 5: Run tests**

Run: `dotnet test Source\PuddingCoreTests\PuddingCoreTests.csproj --filter MessageFabricModelTests --no-restore --nologo`

Expected: PASS.

---

## Task 2: Room Participant Resolver

**Files:**
- Create: `Source/PuddingPlatform/Services/MessageFabric/WorkspaceRoomParticipantResolver.cs`
- Test: `Source/PuddingPlatformTests/Services/MessageFabric/WorkspaceRoomParticipantResolverTests.cs`

- [ ] **Step 1: Write participant resolver tests**

Create `Source/PuddingPlatformTests/Services/MessageFabric/WorkspaceRoomParticipantResolverTests.cs`:

```csharp
using PuddingCode.Models;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Services.MessageFabric;

namespace PuddingPlatformTests.Services.MessageFabric;

[TestClass]
public sealed class WorkspaceRoomParticipantResolverTests
{
    [TestMethod]
    public void Resolve_Includes_User_And_EnabledAgents_As_FirstClassParticipants()
    {
        var participants = WorkspaceRoomParticipantResolver.Resolve(
            workspaceId: "default",
            roomId: "room-default",
            userId: "owner",
            userDisplayName: "Owner",
            agents: Agents());

        Assert.AreEqual(3, participants.Count);
        Assert.IsTrue(participants.Any(p => p.Kind == MessageEndpointKinds.User && p.EndpointId == "owner"));
        Assert.IsTrue(participants.Any(p => p.Kind == MessageEndpointKinds.Agent && p.EndpointId == "assistant"));
        Assert.IsTrue(participants.Any(p => p.Kind == MessageEndpointKinds.Agent && p.EndpointId == "consultant"));
        Assert.IsFalse(participants.Any(p => p.EndpointId == "frozen"));
    }

    private static List<WorkspaceAgentDto> Agents() =>
    [
        Agent("assistant", "默认助手"),
        Agent("consultant", "咨询专家", displayName: "顾问"),
        Agent("frozen", "冻结助手", isFrozen: true),
    ];

    private static WorkspaceAgentDto Agent(
        string agentId,
        string name,
        string? displayName = null,
        bool isFrozen = false) => new(
            AgentId: agentId,
            Name: name,
            Description: null,
            DisplayName: displayName,
            AvatarId: null,
            AvatarUrl: null,
            SourceTemplateId: "global:general-assistant",
            SystemPromptOverride: null,
            PreferredProviderId: null,
            PreferredModelId: null,
            IsEnabled: true,
            IsFrozen: isFrozen,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Source\PuddingPlatformTests\PuddingPlatformTests.csproj --filter WorkspaceRoomParticipantResolverTests --no-restore --nologo`

Expected: FAIL because resolver does not exist.

- [ ] **Step 3: Implement resolver**

Create `Source/PuddingPlatform/Services/MessageFabric/WorkspaceRoomParticipantResolver.cs`:

```csharp
using PuddingCode.Models;
using PuddingPlatform.Data.Dtos;

namespace PuddingPlatform.Services.MessageFabric;

public static class WorkspaceRoomParticipantResolver
{
    public static IReadOnlyList<RoomParticipant> Resolve(
        string workspaceId,
        string roomId,
        string userId,
        string? userDisplayName,
        IReadOnlyList<WorkspaceAgentDto> agents)
    {
        var result = new List<RoomParticipant>
        {
            new()
            {
                ParticipantId = $"{roomId}:user:{userId}",
                RoomId = roomId,
                Kind = MessageEndpointKinds.User,
                EndpointId = userId,
                DisplayName = string.IsNullOrWhiteSpace(userDisplayName) ? userId : userDisplayName,
                Status = "available",
            },
        };

        result.AddRange(agents
            .Where(agent => agent.IsEnabled && !agent.IsFrozen)
            .Select(agent => new RoomParticipant
            {
                ParticipantId = $"{roomId}:agent:{agent.AgentId}",
                RoomId = roomId,
                Kind = MessageEndpointKinds.Agent,
                EndpointId = agent.AgentId,
                DisplayName = agent.DisplayName ?? agent.Name,
                AvatarUrl = agent.AvatarUrl,
                Status = "available",
            }));

        return result;
    }
}
```

- [ ] **Step 4: Run test**

Run: `dotnet test Source\PuddingPlatformTests\PuddingPlatformTests.csproj --filter WorkspaceRoomParticipantResolverTests --no-restore --nologo`

Expected: PASS.

---

## Task 3: Message Router

**Files:**
- Create: `Source/PuddingPlatform/Services/MessageFabric/MessageRouter.cs`
- Test: `Source/PuddingPlatformTests/Services/MessageFabric/MessageRouterTests.cs`

- [ ] **Step 1: Write router tests**

Create `Source/PuddingPlatformTests/Services/MessageFabric/MessageRouterTests.cs`:

```csharp
using PuddingCode.Models;
using PuddingPlatform.Services.MessageFabric;

namespace PuddingPlatformTests.Services.MessageFabric;

[TestClass]
public sealed class MessageRouterTests
{
    [TestMethod]
    public async Task RouteAsync_DirectToAgent_CreatesOneDelivery()
    {
        var router = new MessageRouter();
        var plan = await router.RouteAsync(new MessageEnvelope
        {
            MessageId = "m1",
            RoomId = "room-default",
            From = new MessageAddress { Kind = MessageEndpointKinds.User, Id = "owner" },
            To = [new MessageAddress { Kind = MessageEndpointKinds.Agent, Id = "assistant" }],
            Audience = MessageAudiences.Direct,
            Visibility = MessageVisibilities.Public,
            Content = "hello",
        }, Participants());

        Assert.AreEqual("m1", plan.MessageId);
        Assert.AreEqual("hello", plan.RoomMessage.Content);
        Assert.AreEqual(1, plan.Deliveries.Count);
        Assert.AreEqual("assistant", plan.Deliveries[0].Target.Id);
    }

    [TestMethod]
    public async Task RouteAsync_BroadcastToRoom_CreatesDeliveriesForReceivableAgentsOnly()
    {
        var router = new MessageRouter();
        var plan = await router.RouteAsync(new MessageEnvelope
        {
            MessageId = "m2",
            RoomId = "room-default",
            From = new MessageAddress { Kind = MessageEndpointKinds.User, Id = "owner" },
            To = [new MessageAddress { Kind = MessageEndpointKinds.Room, Id = "room-default" }],
            Audience = MessageAudiences.Broadcast,
            Visibility = MessageVisibilities.Public,
            Content = "hello all",
        }, Participants());

        CollectionAssert.AreEqual(new[] { "assistant", "consultant" }, plan.Deliveries.Select(d => d.Target.Id).ToArray());
    }

    private static IReadOnlyList<RoomParticipant> Participants() =>
    [
        new() { ParticipantId = "p-user", RoomId = "room-default", Kind = MessageEndpointKinds.User, EndpointId = "owner" },
        new() { ParticipantId = "p-agent-1", RoomId = "room-default", Kind = MessageEndpointKinds.Agent, EndpointId = "assistant" },
        new() { ParticipantId = "p-agent-2", RoomId = "room-default", Kind = MessageEndpointKinds.Agent, EndpointId = "consultant" },
        new() { ParticipantId = "p-agent-off", RoomId = "room-default", Kind = MessageEndpointKinds.Agent, EndpointId = "sleeping", CanReceive = false },
    ];
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Source\PuddingPlatformTests\PuddingPlatformTests.csproj --filter MessageRouterTests --no-restore --nologo`

Expected: FAIL because `MessageRouter` does not exist.

- [ ] **Step 3: Implement router**

Create `Source/PuddingPlatform/Services/MessageFabric/MessageRouter.cs`:

```csharp
using PuddingCode.Abstractions;
using PuddingCode.Models;

namespace PuddingPlatform.Services.MessageFabric;

public sealed class MessageRouter : IMessageRouter
{
    public Task<MessageRoutePlan> RouteAsync(
        MessageEnvelope envelope,
        IReadOnlyList<RoomParticipant> participants,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(envelope.RoomId))
            throw new InvalidOperationException("RoomId is required for room message routing.");

        var targets = ResolveTargets(envelope, participants);
        var deliveries = targets.Select(target => new MessageDeliveryDraft
        {
            DeliveryId = Guid.NewGuid().ToString("N"),
            MessageId = envelope.MessageId,
            Target = target,
            Priority = envelope.Priority,
        }).ToList();

        return Task.FromResult(new MessageRoutePlan
        {
            MessageId = envelope.MessageId,
            RoomMessage = new RoomMessageDraft
            {
                RoomId = envelope.RoomId,
                MessageId = envelope.MessageId,
                From = envelope.From,
                Audience = envelope.Audience,
                Visibility = envelope.Visibility,
                Content = envelope.Content,
                CreatedAt = envelope.CreatedAt,
            },
            Deliveries = deliveries,
        });
    }

    private static IReadOnlyList<MessageAddress> ResolveTargets(
        MessageEnvelope envelope,
        IReadOnlyList<RoomParticipant> participants)
    {
        if (envelope.Audience == MessageAudiences.Broadcast)
        {
            return participants
                .Where(p => p.Kind == MessageEndpointKinds.Agent && p.CanReceive && p.Status != "disabled")
                .Select(p => new MessageAddress
                {
                    Kind = p.Kind,
                    Id = p.EndpointId,
                    WorkspaceId = envelope.From.WorkspaceId,
                    DisplayName = p.DisplayName,
                })
                .ToList();
        }

        return envelope.To
            .Where(target => target.Kind != MessageEndpointKinds.Room)
            .DistinctBy(target => $"{target.Kind}:{target.Id}")
            .ToList();
    }
}
```

- [ ] **Step 4: Run test**

Run: `dotnet test Source\PuddingPlatformTests\PuddingPlatformTests.csproj --filter MessageRouterTests --no-restore --nologo`

Expected: PASS.

---

## Task 4: Persistence Store

**Files:**
- Create: `Source/PuddingPlatform/Data/Entities/RoomMessageEntity.cs`
- Create: `Source/PuddingPlatform/Data/Entities/MessageDeliveryEntity.cs`
- Create: `Source/PuddingPlatform/Data/Entities/RoomParticipantEntity.cs`
- Modify: `Source/PuddingPlatform/Data/PlatformDbContext.cs`
- Create: `Source/PuddingPlatform/Services/MessageFabric/MessageFabricStore.cs`
- Create: `Source/PuddingPlatform/Services/MessageFabric/MessageFabricSchemaBootstrapper.cs`
- Test: `Source/PuddingPlatformTests/Services/MessageFabric/MessageFabricStoreTests.cs`
- Test: `Source/PuddingPlatformTests/Services/MessageFabric/MessageFabricSchemaBootstrapperTests.cs`

Implementation note, 2026-05-28: `MessageFabricSchemaBootstrapper` was added and called after `Database.MigrateAsync()` during Agent startup so existing local SQLite databases get `room_messages`, `message_deliveries`, and `room_participants` without requiring a destructive reset. This keeps the message fabric deployable while formal EF migrations remain a separate follow-up.

- [ ] **Step 1: Create persistence test**

Create `Source/PuddingPlatformTests/Services/MessageFabric/MessageFabricStoreTests.cs` with an in-memory SQLite `PlatformDbContext`. Follow the existing `Source/PuddingPlatformTests/Services/*Tests.cs` pattern for constructing the context. The test must insert one `RoomMessageDraft` plus two `MessageDeliveryDraft` rows and assert one room message and two deliveries exist.

Core assertion body:

```csharp
Assert.AreEqual(1, await db.RoomMessages.CountAsync());
Assert.AreEqual(2, await db.MessageDeliveries.CountAsync());
Assert.IsTrue(await db.MessageDeliveries.AllAsync(d => d.Status == MessageDeliveryStatuses.Queued));
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Source\PuddingPlatformTests\PuddingPlatformTests.csproj --filter MessageFabricStoreTests --no-restore --nologo`

Expected: FAIL because entities, DbSets, and store do not exist.

- [ ] **Step 3: Add entities**

Create `RoomMessageEntity`, `MessageDeliveryEntity`, and `RoomParticipantEntity` with string primary IDs and indexed `WorkspaceId`, `RoomId`, `MessageId`, and `TargetId` fields. Use `long CreatedAt/UpdatedAt` to match existing timestamp style.

Minimum `MessageDeliveryEntity` shape:

```csharp
namespace PuddingPlatform.Data.Entities;

public sealed class MessageDeliveryEntity
{
    public int Id { get; set; }
    public string DeliveryId { get; set; } = "";
    public string MessageId { get; set; } = "";
    public string WorkspaceId { get; set; } = "default";
    public string? RoomId { get; set; }
    public string TargetKind { get; set; } = "";
    public string TargetId { get; set; } = "";
    public string Status { get; set; } = "queued";
    public int Priority { get; set; }
    public int AttemptCount { get; set; }
    public string? LastError { get; set; }
    public long CreatedAt { get; set; }
    public long UpdatedAt { get; set; }
}
```

- [ ] **Step 4: Register DbSets and indexes**

Modify `Source/PuddingPlatform/Data/PlatformDbContext.cs`:

```csharp
public DbSet<RoomMessageEntity> RoomMessages => Set<RoomMessageEntity>();
public DbSet<MessageDeliveryEntity> MessageDeliveries => Set<MessageDeliveryEntity>();
public DbSet<RoomParticipantEntity> RoomParticipants => Set<RoomParticipantEntity>();
```

Add indexes in `OnModelCreating`:

```csharp
modelBuilder.Entity<RoomMessageEntity>()
    .HasIndex(x => new { x.WorkspaceId, x.RoomId, x.CreatedAt });
modelBuilder.Entity<RoomMessageEntity>()
    .HasIndex(x => x.MessageId)
    .IsUnique();
modelBuilder.Entity<MessageDeliveryEntity>()
    .HasIndex(x => x.DeliveryId)
    .IsUnique();
modelBuilder.Entity<MessageDeliveryEntity>()
    .HasIndex(x => new { x.WorkspaceId, x.TargetKind, x.TargetId, x.Status });
modelBuilder.Entity<RoomParticipantEntity>()
    .HasIndex(x => new { x.WorkspaceId, x.RoomId, x.Kind, x.EndpointId })
    .IsUnique();
```

- [ ] **Step 5: Implement store**

Create `Source/PuddingPlatform/Services/MessageFabric/MessageFabricStore.cs` with:

```csharp
public async Task PersistRouteAsync(
    string workspaceId,
    MessageRoutePlan plan,
    CancellationToken ct)
```

It inserts one room message and all deliveries in one `SaveChangesAsync(ct)` call. If `MessageId` already exists, return without duplicating rows.

Make `MessageFabricStore` implement `IMessageInbox` and add:

```csharp
public Task<IReadOnlyList<MessageInboxItem>> ListAsync(
    MessageInboxQuery query,
    CancellationToken ct)

public Task AckAsync(string deliveryId, CancellationToken ct)
```

`ListAsync` filters `MessageDeliveryEntity` by `TargetKind`, `TargetId`, optional `WorkspaceId`, optional `RoomId`, and excludes delivered/acked rows unless `IncludeDelivered` is true. `AckAsync` sets `Status = MessageDeliveryStatuses.Delivered`, increments `UpdatedAt`, and sets `AckAt` if the entity includes that field.

- [ ] **Step 6: Run test**

Run: `dotnet test Source\PuddingPlatformTests\PuddingPlatformTests.csproj --filter MessageFabricStoreTests --no-restore --nologo`

Expected: PASS.

---

## Task 5: MessageSystem and Event Bridge

**Files:**
- Create: `Source/PuddingPlatform/Services/MessageFabric/WorkspaceRoomParticipantProvider.cs`
- Create: `Source/PuddingPlatform/Services/MessageFabric/MessageSystem.cs`
- Test: `Source/PuddingPlatformTests/Services/MessageFabric/MessageSystemTests.cs`

- [ ] **Step 1: Write event publishing test**

Create a recording `IInternalEventBus` in the test file. Send a broadcast envelope with two agent deliveries. Assert:

```csharp
Assert.AreEqual(2, bus.Published.Count);
Assert.IsTrue(bus.Published.All(e => e.Type == "message.deliver"));
CollectionAssert.AreEqual(new[] { "assistant", "consultant" }, bus.Published.Select(e => e.AgentId).ToArray());
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Source\PuddingPlatformTests\PuddingPlatformTests.csproj --filter MessageSystemTests --no-restore --nologo`

Expected: FAIL because `MessageSystem` does not exist.

- [ ] **Step 3: Implement participant provider**

Create `Source/PuddingPlatform/Services/MessageFabric/WorkspaceRoomParticipantProvider.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using PuddingCode.Models;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Dtos;

namespace PuddingPlatform.Services.MessageFabric;

public sealed class WorkspaceRoomParticipantProvider
{
    private readonly PlatformDbContext _db;

    public WorkspaceRoomParticipantProvider(PlatformDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<RoomParticipant>> GetParticipantsAsync(
        string workspaceId,
        string roomId,
        string userId = "system",
        string? userDisplayName = null,
        CancellationToken ct = default)
    {
        var workspacePk = await _db.Workspaces
            .Where(w => w.WorkspaceId == workspaceId)
            .Select(w => w.Id)
            .FirstOrDefaultAsync(ct);

        var agents = workspacePk == 0
            ? new List<WorkspaceAgentDto>()
            : await _db.WorkspaceAgents.AsNoTracking()
                .Where(a => a.WorkspaceEntityId == workspacePk)
                .Select(a => new WorkspaceAgentDto(
                    a.AgentId,
                    a.Name,
                    a.Description,
                    a.DisplayName,
                    a.AvatarId,
                    a.AvatarUrl,
                    a.SourceTemplateId,
                    a.SystemPromptOverride,
                    a.PreferredProviderId,
                    a.PreferredModelId,
                    a.IsEnabled,
                    a.IsFrozen,
                    a.CreatedAt,
                    a.UpdatedAt))
                .ToListAsync(ct);

        return WorkspaceRoomParticipantResolver.Resolve(
            workspaceId,
            roomId,
            userId,
            userDisplayName,
            agents);
    }
}
```

- [ ] **Step 4: Implement MessageSystem**

Create `Source/PuddingPlatform/Services/MessageFabric/MessageSystem.cs`:

```csharp
using PuddingCode.Abstractions;
using PuddingCode.Models;

namespace PuddingPlatform.Services.MessageFabric;

public sealed class MessageSystem : IMessageSystem
{
    private readonly IMessageRouter _router;
    private readonly MessageFabricStore _store;
    private readonly IInternalEventBus _eventBus;
    private readonly WorkspaceRoomParticipantProvider _participants;

    public MessageSystem(
        IMessageRouter router,
        MessageFabricStore store,
        IInternalEventBus eventBus,
        WorkspaceRoomParticipantProvider participants)
    {
        _router = router;
        _store = store;
        _eventBus = eventBus;
        _participants = participants;
    }

    public async Task<MessageSendResult> SendAsync(MessageEnvelope envelope, CancellationToken ct = default)
    {
        var workspaceId = envelope.From.WorkspaceId ?? "default";
        var participants = await _participants.GetParticipantsAsync(workspaceId, envelope.RoomId ?? "default", ct);
        var plan = await _router.RouteAsync(envelope, participants, ct);
        await _store.PersistRouteAsync(workspaceId, plan, ct);

        foreach (var delivery in plan.Deliveries)
        {
            await _eventBus.PublishAsync(new InternalEvent
            {
                Type = "message.deliver",
                WorkspaceId = workspaceId,
                AgentId = delivery.Target.Kind == MessageEndpointKinds.Agent ? delivery.Target.Id : null,
                SessionId = envelope.ConversationId,
                Priority = envelope.Priority >= 10 ? EventPriorityLevel.Urgent :
                    envelope.Priority >= 5 ? EventPriorityLevel.Important : EventPriorityLevel.Normal,
                Source = new EventSource
                {
                    SourceType = "message",
                    SourceId = envelope.MessageId,
                },
                Payload = new MessageDeliverEventPayload
                {
                    MessageId = envelope.MessageId,
                    DeliveryId = delivery.DeliveryId,
                    WorkspaceId = workspaceId,
                    RoomId = envelope.RoomId,
                    From = envelope.From,
                    Target = delivery.Target,
                    Content = envelope.Content,
                    ReplyToMessageId = envelope.ReplyToMessageId,
                    Metadata = envelope.Metadata,
                },
                CorrelationId = envelope.CorrelationId,
                CausationId = envelope.CausationId,
            }, ct);
        }

        return new MessageSendResult
        {
            MessageId = envelope.MessageId,
            RoomId = envelope.RoomId,
            DeliveryIds = plan.Deliveries.Select(d => d.DeliveryId).ToList(),
        };
    }
}
```

- [ ] **Step 5: Run test**

Run: `dotnet test Source\PuddingPlatformTests\PuddingPlatformTests.csproj --filter MessageSystemTests --no-restore --nologo`

Expected: PASS.

---

## Task 6: AgentEventHandler Consumes message.deliver

**Files:**
- Modify: `Source/PuddingAgent/Services/Events/AgentEventHandler.cs`
- Test: add focused test project or use existing Runtime/Agent test project if one already covers event handlers.

- [ ] **Step 1: Extract payload parser**

Add a small internal helper near `BuildRequest`:

```csharp
private static MessageDeliverEventPayload? TryReadMessageDeliverPayload(InternalEvent evt)
{
    if (evt.Payload is MessageDeliverEventPayload payload)
        return payload;
    if (evt.Payload is JsonElement json && json.ValueKind == JsonValueKind.Object)
        return JsonSerializer.Deserialize<MessageDeliverEventPayload>(json.GetRawText());
    return null;
}
```

- [ ] **Step 2: Route message.deliver explicitly**

In `HandleAsync`, before generic `BuildRequest(evt)`:

```csharp
if (evt.Type == "message.deliver")
{
    var payload = TryReadMessageDeliverPayload(evt);
    if (payload is null)
    {
        _logger.LogWarning("[AgentEventHandler] message.deliver missing payload event={EventId}", evt.EventId);
        return true;
    }

    var request = new RuntimeDispatchRequest
    {
        SessionId = evt.SessionId ?? $"msg-{payload.MessageId}",
        WorkspaceId = payload.WorkspaceId,
        AgentTemplateId = payload.Target.Id,
        MessageText = payload.Content,
        MessageId = payload.MessageId,
    };

    return await ExecuteMainEventStreamAsync(evt, request, CancellationToken.None);
}
```

- [ ] **Step 3: Verify build**

Run: `dotnet build Source\PuddingAgent\PuddingAgent.csproj --no-restore --nologo`

Expected: build succeeds.

---

## Task 7: Runtime Message Tools

**Files:**
- Create: `Source/PuddingRuntime/Services/Tools/SendMessageTool.cs`
- Create: `Source/PuddingRuntime/Services/Tools/ReceiveMessagesTool.cs`
- Modify: `Source/PuddingAgent/Program.cs`

Implementation note, 2026-05-28: `SkillInvokeRequest` currently exposes `Input` and string-only `Parameters`, not `ArgumentsJson`. The implemented tools therefore parse `to`, `content`, `room_id`, `priority`, `ack`, and related values from `Parameters`. Because `SkillRuntime` is singleton while `MessageSystem` / `IMessageInbox` are scoped through `PlatformDbContext`, production registration creates singleton tools with `IServiceScopeFactory` and resolves message services per invocation.

- [x] **Step 1: Add tool implementation**

Create `Source/PuddingRuntime/Services/Tools/SendMessageTool.cs`:

```csharp
using System.Text.Json;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingRuntime.Services.Skills;

namespace PuddingRuntime.Services.Tools;

public sealed class SendMessageTool : IAgentSkill
{
    private readonly IMessageSystem _messageSystem;

    public string SkillId => "send_message";
    public string DisplayName => "Send Message";
    public string Description => "Send a message to a user, agent, room, connector, or system endpoint through the message fabric.";

    public SendMessageTool(IMessageSystem messageSystem)
    {
        _messageSystem = messageSystem;
    }

    public async Task<SkillResult> ExecuteAsync(SkillInvokeRequest request, CancellationToken ct = default)
    {
        var args = JsonSerializer.Deserialize<SendMessageArgs>(request.ArgumentsJson);
        if (args is null || args.To.Count == 0 || string.IsNullOrWhiteSpace(args.Content))
            return SkillResult.Fail("send_message requires to[] and content.");

        var envelope = new MessageEnvelope
        {
            From = new MessageAddress
            {
                Kind = MessageEndpointKinds.Agent,
                Id = request.AgentId ?? "agent",
                WorkspaceId = request.WorkspaceId,
            },
            To = args.To.Select(ParseAddress).ToList(),
            RoomId = args.RoomId,
            ConversationId = request.SessionId,
            ReplyToMessageId = args.ReplyToMessageId,
            Audience = string.IsNullOrWhiteSpace(args.Audience) ? MessageAudiences.Direct : args.Audience,
            Visibility = string.IsNullOrWhiteSpace(args.Visibility) ? MessageVisibilities.Private : args.Visibility,
            Content = args.Content,
            Priority = args.Priority,
        };

        var result = await _messageSystem.SendAsync(envelope, ct);
        return SkillResult.Ok(JsonSerializer.Serialize(result));
    }

    private static MessageAddress ParseAddress(string raw)
    {
        var parts = raw.Split(':', 2);
        return parts.Length == 2
            ? new MessageAddress { Kind = parts[0], Id = parts[1] }
            : new MessageAddress { Kind = MessageEndpointKinds.Agent, Id = raw };
    }

    private sealed record SendMessageArgs
    {
        public List<string> To { get; init; } = [];
        public string Content { get; init; } = "";
        public string? Audience { get; init; }
        public string? Visibility { get; init; }
        public int Priority { get; init; }
        public string? RoomId { get; init; }
        public string? ReplyToMessageId { get; init; }
    }
}
```

- [x] **Step 2: Add receive_messages tool implementation**

Create `Source/PuddingRuntime/Services/Tools/ReceiveMessagesTool.cs`:

```csharp
using System.Text.Json;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingRuntime.Services.Skills;

namespace PuddingRuntime.Services.Tools;

public sealed class ReceiveMessagesTool : IAgentSkill
{
    private readonly IMessageInbox _inbox;

    public string SkillId => "receive_messages";
    public string DisplayName => "Receive Messages";
    public string Description => "Check the current agent inbox for queued or unread messages from the message fabric.";

    public ReceiveMessagesTool(IMessageInbox inbox)
    {
        _inbox = inbox;
    }

    public async Task<SkillResult> ExecuteAsync(SkillInvokeRequest request, CancellationToken ct = default)
    {
        var args = JsonSerializer.Deserialize<ReceiveMessagesArgs>(request.ArgumentsJson) ?? new ReceiveMessagesArgs();
        var query = new MessageInboxQuery
        {
            Endpoint = new MessageAddress
            {
                Kind = MessageEndpointKinds.Agent,
                Id = string.IsNullOrWhiteSpace(args.EndpointId) ? request.AgentId ?? "agent" : args.EndpointId,
                WorkspaceId = request.WorkspaceId,
            },
            WorkspaceId = request.WorkspaceId,
            RoomId = args.RoomId,
            Limit = args.Limit <= 0 ? 20 : args.Limit,
            IncludeDelivered = args.IncludeDelivered,
        };

        var messages = await _inbox.ListAsync(query, ct);
        return SkillResult.Ok(JsonSerializer.Serialize(messages));
    }

    private sealed record ReceiveMessagesArgs
    {
        public string? EndpointId { get; init; }
        public string? RoomId { get; init; }
        public int Limit { get; init; } = 20;
        public bool IncludeDelivered { get; init; }
    }
}
```

- [x] **Step 3: Register skills**

Modify `Source/PuddingAgent/Program.cs` near other `IAgentSkill` registrations:

```csharp
builder.Services.AddSingleton<SendMessageTool>();
builder.Services.AddSingleton<IAgentSkill>(sp => sp.GetRequiredService<SendMessageTool>());
builder.Services.AddSingleton<ReceiveMessagesTool>();
builder.Services.AddSingleton<IAgentSkill>(sp => sp.GetRequiredService<ReceiveMessagesTool>());
```

- [x] **Step 4: Verify build**

Run: `dotnet build Source\PuddingAgent\PuddingAgent.csproj --no-restore --nologo`

Expected: build succeeds.

---

## Task 8: Chat API Migration Away From Direct Fan-Out

**Files:**
- Modify: `Source/PuddingPlatform/Controllers/Api/ChatApiController.cs`
- Modify: `Source/PuddingPlatform/Data/Dtos/PlatformDtos.cs`
- Test: add or update `Source/PuddingPlatformTests` Chat API/service tests.

Architecture guardrail: this task must move Web Chat ingress toward `GatewayIngress -> IMessageSystem`. `ChatApiController` may remain as the HTTP boundary during migration, but it must behave like a WebChat gateway adapter, not as the routing authority. It must not expand `@all`, create secondary Agent execution requests, or decide final Agent target sets after `IMessageRouter` is available.

- [ ] **Step 1: Remove secondary fan-out startup**

Delete the code that starts `RunSecondaryChatFanoutAsync` from `ChatApiController`. The replacement path is:

```csharp
var envelope = new MessageEnvelope
{
    From = new MessageAddress
    {
        Kind = MessageEndpointKinds.User,
        Id = userExternalId,
        WorkspaceId = workspaceId,
    },
    To = request.Audience == "all"
        ? [new MessageAddress { Kind = MessageEndpointKinds.Room, Id = roomId, WorkspaceId = workspaceId }]
        : [new MessageAddress { Kind = MessageEndpointKinds.Agent, Id = request.AgentId ?? defaultAgentId, WorkspaceId = workspaceId }],
    RoomId = roomId,
    ConversationId = request.SessionId,
    Audience = request.Audience == "all" ? MessageAudiences.Broadcast : MessageAudiences.Direct,
    Visibility = MessageVisibilities.Public,
    Content = request.MessageText,
};
var result = await messageSystem.SendAsync(envelope, ct);
```

- [ ] **Step 2: Preserve API compatibility**

Return existing `AdminChatResponse` shape using `result.MessageId` and `request.SessionId` or room conversation id. Do not expose `fanout_index`.

- [ ] **Step 3: Run focused backend tests**

Run:

```powershell
dotnet test Source\PuddingPlatformTests\PuddingPlatformTests.csproj --filter "MessageRouterTests|MessageSystemTests|ChatRoomRouteResolverTests" --no-restore --nologo
dotnet build Source\PuddingPlatform\PuddingPlatform.csproj --no-restore --nologo
```

Expected: tests and build pass.

---

## Task 8.5: Gateway / Connector Message Ingress and Egress

**Files:**
- Create: `Source/PuddingCore/Abstractions/IMessageGatewayIngress.cs`
- Create: `Source/PuddingCore/Abstractions/IMessageGatewayEgress.cs`
- Create: `Source/PuddingPlatform/Services/MessageFabric/MessageGatewayIngress.cs`
- Create: `Source/PuddingPlatform/Services/MessageFabric/MessageGatewayEgressDispatcher.cs`
- Modify: `Source/PuddingAgent/Program.cs`
- Test: `Source/PuddingPlatformTests/Services/MessageFabric/MessageGatewayIngressTests.cs`
- Test: `Source/PuddingPlatformTests/Services/MessageFabric/MessageGatewayEgressDispatcherTests.cs`

- [ ] **Step 1: Introduce Gateway ingress contract**

Create a small contract that converts `PuddingIngressEnvelope` into a `MessageEnvelope` and submits it to `IMessageSystem`.

Required behavior:

- Preserve `EnvelopeId`, `CorrelationId`, `channelType`, `channelId`, `userExternalId`, and metadata.
- Map WebChat/WebSocket user messages to `from=user`.
- Map MQTT/Webhook/HTTP device-origin messages to `from=connector` unless authenticated user binding says otherwise.
- Treat `@agent/@all` as hints or content only; final route remains `IMessageRouter` responsibility.

- [ ] **Step 2: Route ConnectorHost ingress through Message Fabric**

Replace direct `IInternalEventBus.PublishAsync(connector.*)` for chat-like connector messages with `IMessageGatewayIngress.SubmitAsync`.

Do not remove generic connector events entirely: non-chat telemetry, diagnostics, or management events may still publish `connector.*` events. The distinction is:

- user/device says something to an Agent or room -> Message Fabric.
- connector lifecycle/diagnostic/system telemetry -> Event System.

- [ ] **Step 3: Introduce Gateway egress dispatcher**

Create a dispatcher that consumes connector/user delivery facts and calls the right connector:

- `target=connector:*` -> `IPuddingConnector.SendAsync` or `OperateAsync`.
- `target=user:*` with live WebSocket binding -> `WebSocketConnector.SendAsync`.
- offline user -> leave delivery queued/readable from inbox and room transcript.

The dispatcher must not decide business route; it only maps already-routed delivery to a protocol endpoint.

- [ ] **Step 4: Add egress tests**

Use a fake connector and fake message delivery to assert:

- `connector:mqtt.living-room` results in exactly one `SendAsync` call.
- `user:owner` without a live binding does not fail delivery creation and remains recoverable.
- `agent:*` deliveries are not sent through Gateway egress; they stay on event delivery path.

---

## Task 9: Admin Chat Becomes Room Client

**Files:**
- Modify: `Source/PuddingPlatformAdmin/src/pages/chat/hooks/useChatState.ts`
- Modify: `Source/PuddingPlatformAdmin/src/pages/chat/hooks/chatRouting.ts`
- Modify: `Source/PuddingPlatformAdmin/src/services/platform/api.ts`
- Test: update `Source/PuddingPlatformAdmin/src/pages/chat/hooks/useChatState.recovery.test.ts`

- [ ] **Step 1: Remove fanout turn materialization**

Delete the `fanout_index > 0` branch in `applySessionEvent`. The frontend must not create extra turns because backend fan-out happened. It should render messages and deliveries from room/session state.

- [ ] **Step 2: Keep mention parsing as UI hint only**

Change `resolveChatRoute` callers so the request can include the original text and optional hint, but frontend does not compute authoritative target set for `@all`.

Request shape:

```ts
{
  messageText: text,
  originalMessageText: text,
  sessionId: sessionIdRef.current,
  agentId,
  audienceHint: route.audience,
  forceNewSession: forceNewSessionRef.current,
}
```

- [ ] **Step 3: Add frontend regression test**

In `useChatState.recovery.test.ts`, add a test that a metadata event with `fanout_index: "1"` does not create a new turn. Expected: turn count stays unchanged.

- [ ] **Step 4: Run frontend tests**

Run:

```powershell
npm test -- --runTestsByPath src/pages/chat/hooks/useChatState.recovery.test.ts src/pages/chat/components/ChatMain.test.tsx --runInBand
```

Expected: PASS.

---

## Task 10: Final Verification

**Files:**
- All modified files above.
- ADR: `Docs/07架构/46ADR-045双向消息系统与聊天室客户端ADR.md`

- [ ] **Step 1: Backend focused test suite**

Run:

```powershell
dotnet test Source\PuddingCoreTests\PuddingCoreTests.csproj --filter MessageFabricModelTests --no-restore --nologo
dotnet test Source\PuddingPlatformTests\PuddingPlatformTests.csproj --filter "WorkspaceRoomParticipantResolverTests|MessageRouterTests|MessageFabricStoreTests|MessageSystemTests|MessageFabricSchemaBootstrapperTests" --no-restore --nologo
dotnet test Source\PuddingRuntimeTests\PuddingRuntimeTests.csproj --filter MessageToolsTests --no-restore --nologo
dotnet build Source\PuddingAgent\PuddingAgent.csproj --no-restore --nologo -p:OutputPath=E:\github\AgentNetworkPlan\PuddingAgent\temp\build\PuddingAgent\
```

Expected: all pass.

- [ ] **Step 2: Frontend focused tests**

Run from `Source\PuddingPlatformAdmin`:

```powershell
npm test -- --runTestsByPath src/pages/chat/hooks/useChatState.recovery.test.ts src/pages/chat/components/ChatMain.test.tsx --runInBand
```

Expected: all pass.

- [ ] **Step 3: Architecture cleanup search**

Run:

```powershell
rg -n "RunSecondaryChatFanoutAsync|fanout_index|fanout_count|secondaryFanoutStarted" Source
```

Expected: no active production references. Test fixtures may mention removed behavior only if they assert compatibility is gone.

- [ ] **Step 4: Manual behavior check**

Start the app using the repository's normal dev command and verify:

1. User sends direct message to one Agent.
2. User sends `@all` and backend creates one room message with multiple deliveries.
3. Agent sends `send_message` to a user or room without depending on the browser.
4. Agent calls `receive_messages` and sees queued inbox deliveries.
5. Agent subscribed to `message.*` is invoked through the event system for the same delivery model.
6. Refresh Admin Chat and verify room timeline is recovered from persisted state.

---

## Self-Review

- ADR-045 coverage: first-class user/agent participants are covered by Task 1 and Task 2.
- Message system vs event system boundary: Message Fabric owns endpoint/inbox/delivery semantics; `IInternalEventBus` remains the priority/subscription/retry pipe in Tasks 5 and 6.
- Bidirectional message flow: user-to-agent, agent-to-user, agent-to-agent, connector direction, pull inbox, and event subscription are covered by Tasks 5 through 7.
- Anti-patch migration: direct `ChatApiController` fan-out and frontend fanout turn materialization are retired in Tasks 8 and 9.
- Testing: each layer has focused tests before implementation, plus final backend/frontend verification.
- Open risk: persistence entity names and exact `PlatformDbContext` location should be checked during implementation against current source before editing; do not overwrite unrelated dirty work.
