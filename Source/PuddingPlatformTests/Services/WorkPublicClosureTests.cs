using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pudding.Api.Contracts;
using PuddingAgentWork;
using PuddingAgentWork.Contracts;
using PuddingCode.Platform;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services;
using PuddingPlatform.Services.AgentWork;
using PuddingPlatformTests.Services;

namespace PuddingPlatformTests.Services;

/// <summary>
/// First public closure over real SQLite and the canonical acceptance schema: a management actor gets a borrowed Agent,
/// submits once (idempotent), observes the committed event page and cancels through the single execution-control
/// authority. No provider/LLM is registered in the container, so any attempt to start inference would fail loudly.
/// </summary>
[TestClass]
public sealed class WorkPublicClosureTests
{
    [TestMethod]
    public async Task SubmitObserveCancel_ReachesTheCanonicalAcceptanceBoundary()
    {
        await using var host = await ClosureHost.CreateAsync();
        var agent = new AgentRef("ws-1", "agent-1");
        var api = new AgentWorkApiFactory(host.Work).ForManagementUser(Principal("actor-1"), agent);

        var receipt = await api.SubmitAsync(agent, new AgentWorkRequest("op-1", "hello closure"), CancellationToken.None);
        Assert.AreEqual("op-1", receipt.OperationId);
        Assert.AreEqual("conv-1", receipt.ConversationId);
        Assert.AreEqual(0, host.ControlCommands.Count,
            "Submission must not run the provider or send execution control; only acceptance is written.");

        var replay = await api.SubmitAsync(agent, new AgentWorkRequest("op-1", "hello closure"), CancellationToken.None);
        Assert.AreEqual(receipt.WorkId, replay.WorkId);
        var conflict = await Assert.ThrowsExactlyAsync<AgentWorkException>(async () =>
            await api.SubmitAsync(agent, new AgentWorkRequest("op-1", "different"), CancellationToken.None));
        Assert.AreEqual("operation.conflict", conflict.Code);

        var snapshot = await api.GetAsync(agent, "op-1");
        Assert.AreEqual(AgentWorkStatus.Accepted, snapshot!.Status);

        var observe = await host.ObserveAsync("op-1", 0);
        Assert.AreEqual(1, observe.Events.Length);
        Assert.AreEqual(ConversationEventTypes.TurnAccepted, observe.Events[0].Kind);
        Assert.IsFalse(observe.Complete);

        var cancel = await host.CancelAsync("op-1", receipt.AcceptedSequence);
        Assert.AreEqual(CancelWorkEffect.Requested, cancel.Effect);
        Assert.AreEqual(1, host.Cancelled.Count);
        Assert.AreEqual(receipt.ConversationId, host.Cancelled[0].ConversationId);
        Assert.AreEqual(receipt.TurnId, host.Cancelled[0].TurnId);
        Assert.AreEqual(ControlMessageKind.CancelRequested, host.Cancelled[0].Kind);
    }

    private static System.Security.Claims.ClaimsPrincipal Principal(string actor)
        => new(new System.Security.Claims.ClaimsIdentity(
            [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, actor)], "Bearer"));

    private sealed class ClosureHost(SqliteConnection connection, ServiceProvider provider,
        CanonicalAgentWorkBackend work, CanonicalAgentWorkControlBackend control,
        FakeController controller) : IAsyncDisposable
    {
        public CanonicalAgentWorkBackend Work { get; } = work;
        public IReadOnlyList<ExecutionControlCommand> ControlCommands => controller.Commands;
        public IReadOnlyList<ExecutionControlCommand> Cancelled { get; } = controller.Commands;

        public Task<WorkObservation> ObserveAsync(string operationId, long after)
            => control.ObserveAsync(new WorkCaller("actor-1"), new AgentRef("ws-1", "agent-1"), operationId, after, 100,
                CancellationToken.None);

        public async Task<CancelWorkReceipt> CancelAsync(string operationId, long revision)
        {
            var result = await control.CancelAsync(new WorkCaller("actor-1"), new AgentRef("ws-1", "agent-1"),
                "op-cancel", operationId, revision, "user.cancelled", CancellationToken.None);
            var effect = result.Effect switch
            {
                "terminal" => CancelWorkEffect.AlreadyTerminal,
                "not_found" => CancelWorkEffect.NotFound,
                "forbidden" => CancelWorkEffect.Forbidden,
                _ => CancelWorkEffect.Requested,
            };
            return new("op-cancel", operationId, effect, result.Revision, result.CurrentStatus);
        }

        public static async Task<ClosureHost> CreateAsync()
        {
            var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync();
            var services = new ServiceCollection();
            services.AddDbContext<PlatformDbContext>(o => o.UseSqlite(connection));
            services.AddScoped<IAgentWorkTargetResolver>(_ => new FakeResolver());
            services.AddScoped<ISubmitTurnHandler>(sp => new FakeSubmit(sp.GetRequiredService<PlatformDbContext>()));
            var controller = new FakeController();
            services.AddSingleton<IExecutionControlService>(controller);
            services.AddSingleton<IAgentWorkBackend, CanonicalAgentWorkBackend>();
            var provider = services.BuildServiceProvider();
            using (var scope = provider.CreateScope())
                await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().Database.EnsureCreatedAsync();
            var work = provider.GetRequiredService<IAgentWorkBackend>() as CanonicalAgentWorkBackend
                ?? throw new InvalidOperationException();
            var control = new CanonicalAgentWorkControlBackend(provider.GetRequiredService<IServiceScopeFactory>(), work);
            return new(connection, provider, work, control, controller);
        }

        public async ValueTask DisposeAsync()
        {
            await provider.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class FakeResolver : IAgentWorkTargetResolver
    {
        public Task<string> ResolveConversationAsync(WorkKey key, CancellationToken ct) => Task.FromResult("conv-1");
    }

    private sealed class FakeSubmit(PlatformDbContext db) : ISubmitTurnHandler
    {
        public async Task<AcceptanceResult> HandleAsync(SubmitTurnCommand command, CancellationToken ct)
        {
            const long acceptedSequence = 1;
            const string messageId = "msg-1";
            const string turnId = "turn-1";
            const string commandId = "cmd-1";
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            db.AcceptanceBatches.Add(new AcceptanceBatchEntity
            {
                BatchId = "batch-1", WorkspaceId = command.WorkspaceId, ClientRequestId = command.ClientRequestId,
                ConversationId = command.ConversationId, MessageId = messageId, UserId = command.UserId,
                TurnCount = 1, AcceptedSequence = acceptedSequence, CreatedAt = now,
            });
            db.ChatExecutionCommands.Add(new ChatExecutionCommandEntity
            {
                CommandId = commandId, BatchId = "batch-1", ClientRequestId = command.ClientRequestId,
                WorkspaceId = command.WorkspaceId, SessionId = command.ConversationId, MessageId = messageId,
                UserMessageId = messageId, TurnId = turnId, AgentInstanceId = "agent-1", UserId = command.UserId,
                Status = "pending", CreatedAt = now,
            });
            db.ChatMessages.Add(new ChatMessageEntity
            {
                MessageId = messageId, SessionId = command.ConversationId, WorkspaceId = command.WorkspaceId,
                AgentInstanceId = "agent-1", Role = "user", Content = Text(command), UserId = command.UserId,
                TurnId = turnId, CommandId = commandId, CreatedAt = now,
                MetadataJson = JsonSerializer.Serialize(command.Metadata),
            });
            db.ConversationEvents.Add(new ConversationEventEntity
            {
                ConversationId = command.ConversationId, Sequence = acceptedSequence, EventId = "evt-accepted",
                WorkspaceId = command.WorkspaceId, TurnId = turnId, CommandId = commandId, MessageId = messageId,
                Type = ConversationEventTypes.TurnAccepted, Payload = "{}",
                OccurredAt = DateTimeOffset.UtcNow.ToString("O"), CommittedAt = DateTimeOffset.UtcNow.ToString("O"),
            });
            db.ConversationTurns.Add(new ConversationTurnEntity
            {
                ConversationId = command.ConversationId, TurnId = turnId, CommandId = commandId,
                WorkspaceId = command.WorkspaceId, UserId = command.UserId, Status = "accepted",
                AcceptedSequence = acceptedSequence, CreatedAt = now,
            });
            await db.SaveChangesAsync(ct);
            return new AcceptanceResult
            {
                ConversationId = command.ConversationId, MessageId = messageId, TurnIds = [turnId],
                CommandIds = [commandId], AcceptedSequence = acceptedSequence,
            };
        }

        private static string Text(SubmitTurnCommand command)
            => string.Concat(command.Content.Select(c => c.Text));
    }

    private sealed class FakeController : IExecutionControlService
    {
        public List<ExecutionControlCommand> Commands { get; } = [];

        public Task<ControlReceipt> SubmitAsync(ExecutionControlCommand command, CancellationToken ct)
        {
            Commands.Add(command);
            return Task.FromResult(new ControlReceipt("control-" + Commands.Count, Commands.Count, Commands.Count));
        }
    }
}
