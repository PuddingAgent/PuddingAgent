using System.IO.Compression;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Pudding.Api.Contracts;
using Pudding.Api.Sdk;
using Pudding.ToolPlugins.Contracts;
using Pudding.ToolPlugins.Runtime;
using PluginEventDelivery = Pudding.ToolPlugins.Contracts.PluginEventDelivery;
using Pudding.ToolPlugins.Windows;
using PuddingAgentWork;
using PuddingAgentWork.Contracts;
using PuddingCode.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingCode.Tools;
using PuddingEventing;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services;
using PuddingPlatform.Services.AgentWork;
using PuddingPlatform.Services.Conversation;
using PuddingPlatform.Services.Eventing;
using PuddingPlatform.Services.Execution;
using PuddingPlatform.Services.Files;
using PuddingRuntime.Services;
using PuddingRuntime.Services.AgentLoop;
using PuddingRuntime.Services.Plugins;
using PuddingRuntime.Services.Messaging;
using PuddingRuntime.Services.Skills;
using PuddingRuntime.Services.Tools;
using ModelUsage = PuddingExecutionResources.Contracts.ModelUsage;

namespace PuddingToolPlugins.IntegrationTests;

/// <summary>
/// B first slice (foundation prototype): public SDK submits a Work, a scripted model drives the real AgentExecutionService
/// and a real tool plugin, the host artifact service commits a versioned artifact, the subscription observer requests the
/// archive, and the same WorkId resolves artifact/event/archive. The production dispatcher/Work command bridge and the
/// remaining B scenarios are not yet wired here (documented in the test).
/// </summary>
[TestClass]
public sealed class FoundationPrototypeTests
{
    [TestMethod]
    public async Task SdkSubmittedWork_ProducesVersionedArtifact_AndObserverArchive()
    {
        var workerPath = Environment.GetEnvironmentVariable("PUDDING_PLUGIN_WORKER");
        var fixturePath = Environment.GetEnvironmentVariable("PUDDING_PLUGIN_FIXTURE");
        if (!File.Exists(workerPath) || !File.Exists(fixturePath))
            Assert.Inconclusive("Set PUDDING_PLUGIN_WORKER and PUDDING_PLUGIN_FIXTURE (plugin acceptance environment).");
        var root = Path.Combine(Environment.GetEnvironmentVariable("PUDDING_PLUGIN_TEST_ROOT") ?? Path.GetTempPath(),
            "foundation-prototype-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var dbPath = Path.Combine(root, "prototype.db");
        var connection = new SqliteConnection("DataSource=" + dbPath);
        await connection.OpenAsync();
        try
        {
            var dbOptions = new DbContextOptionsBuilder<PlatformDbContext>().UseSqlite(connection).Options;
            var dbFactory = new DbFactory(dbOptions);
            using (var db = dbFactory.CreateDbContext())
            {
                await db.Database.EnsureCreatedAsync();
                await EventSubscriptionSchemaBootstrapper.EnsureCreatedAsync(db, null);
                await PluginArchiveSchemaBootstrapper.EnsureCreatedAsync(db, null);
                await WorkspaceArtifactSchemaBootstrapper.EnsureCreatedAsync(db, null);
            }

            // Real AgentWork API + acceptance store; the SDK consumer wraps the real management API.
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddDbContextFactory<PlatformDbContext>(o => o.UseSqlite(connection));
            services.AddSingleton<IAgentWorkTargetResolver, FixedTarget>();
            services.AddSingleton<ICommittedEventSignal, NoSignal>();
            services.AddSingleton(Mock.Of<IVisualArtifactLocalFileResolver>());
            services.AddScoped<IConversationAcceptanceStore, ConversationAcceptanceStore>();
            services.AddScoped<ISubmitTurnHandler, SubmitTurnHandler>();
            var controlSink = new ControlSink();
            services.AddSingleton<IExecutionControlService>(controlSink);
            services.AddAgentWorkApi();
            await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
            var agent = new AgentRef("ws", "agent");
            var principal = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, "prototype")], "test"));
            var sdk = new AgentWorkClient(provider.GetRequiredService<AgentWorkApiFactory>().ForManagementUser(principal, agent))
                .For(agent);
            var receipt = await sdk.SubmitAsync(new AgentWorkRequest("prototype-work", "请生成版本化产物并交给订阅插件归档。"));

            // Real plugin manager/worker: one tool plugin and one zero-tool workspace observer.
            await using var plugins = new ExecutablePluginService(PuddingDataPaths.FromRoot(root),
                new WindowsPluginWorkerFactory(workerPath!));
            var toolPackage = await ImportAsync(plugins.Packages, fixturePath!, ToolManifest());
            var toolScope = new PluginScope("ws", "agent", "session");
            var toolActivation = await plugins.Manager.LoadAsync("load-tool", toolPackage, toolScope,
                new PluginAuthorization(true, new HashSet<string> { "workspace.read" }, true));
            Assert.AreEqual("Active", toolActivation.State, toolActivation.Failure?.Message);
            var observerPackage = await ImportAsync(plugins.Packages, fixturePath!, ObserverManifest());
            var observerScope = new PluginScope("ws", "", "", "host-subscriber");
            var observer = await plugins.Manager.LoadAsync("load-observer", observerPackage, observerScope,
                new PluginAuthorization(false, new HashSet<string>(), false, SubscriptionsApproved: true));
            Assert.AreEqual("Active", observer.State, observer.Failure?.Message);

            var artifacts = new WorkspaceArtifactService(dbFactory, new EventOutboxUnitOfWork(dbFactory));
            var artifactTool = new ArtifactPublishTool(artifacts);
            var registry = new PuddingToolRegistry([artifactTool], contextualToolSources: [plugins]);
            var firewall = new Mock<IAgentFirewall>();
            firewall.Setup(f => f.EvaluateAsync(It.IsAny<FirewallContext>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(FirewallDecision.Allow());
            var executor = new PuddingToolExecutionService(registry, new SandboxExecutor(NullLogger<SandboxExecutor>.Instance),
                NullLogger<PuddingToolExecutionService>.Instance, firewall: firewall.Object);
            var (loop, _) = BuildLoop(new PrototypeScriptModel(plugins, receipt.WorkId), new ToolInvocationService(executor),
                new PuddingToolSchemaService(registry), [new ExecutablePluginHook(plugins)], root);
            var dispatch = new RuntimeDispatchRequest
            {
                SessionId = receipt.ConversationId,
                AgentInstanceId = agent.AgentId,
                WorkspaceId = agent.WorkspaceId,
                AgentTemplateId = "prototype",
                WorkingDirectory = root,
                MessageText = "请生成版本化产物并交给订阅插件归档。",
                MaxRounds = 5,
                SuppressContextAutoCompaction = true,
                CapabilityPolicy = new()
                {
                    DefaultToolNames = [artifactTool.Descriptor.ToolId],
                    AllowedToolNames = [artifactTool.Descriptor.ToolId],
                    AllowShellExecution = true,
                },
                LlmConfig = new() { ModelId = "scripted-model", MaxContextTokens = 16_384 },
            };
            var workDispatcher = new RuntimeAgentDispatcher(loop, new AgentExecutionStateRegistry());
            await workDispatcher.DispatchAsync(dispatch);

            // The real execution journal records the terminal, so the public SDK can read Completed.
            await FinishWorkAsync(provider, receipt, TurnTerminal.Success("产物已提交并归档。", null));

            // Link Work -> artifact (same WorkId as artifact id), then dispatch to the observer and archive.
            var stock = new SubscriptionStore(dbFactory);
            await new PluginSubscriptionSynchronizer(stock).SyncAsync(
                [new PluginSubscriptionCandidate("ws", "sample.observer", Math.Max(1, observer.Revision),
                    [PluginEvents.WorkspaceArtifactCreated])], ["ws"]);
            var subscription = (await stock.ListAsync("ws")).Single(s => !s.Cancelled);
            var archive = new PluginArchiveStore(dbFactory);
            var target = new ManagerTarget(plugins, archive);
            var deliveryStore = new EventDeliveryStore(dbFactory);
            var outboxLog = new OutboxCommittedEventLog(dbFactory);
            var dispatcher = new SubscriptionDispatcher(outboxLog, stock, deliveryStore, stock);
            var worker = new PluginEventDispatchWorker(new Workspaces(), stock, dispatcher, deliveryStore,
                new PluginManagerEventDeliverySink(target, stock));
            var dispatchResult = await worker.RunOnceAsync();
            Assert.AreEqual(1, dispatchResult.Acknowledged, "observer delivery must be acknowledged");

            var artifact = await artifacts.GetAsync("ws", receipt.WorkId, "1.0.0");
            Assert.IsNotNull(artifact, "artifact is readable by WorkId + version");
            var page = await outboxLog.ReadAsync(new("ws", 0, 100));
            var evt = page.Events.Single(e => e.BusinessRef == receipt.WorkId + "@1.0.0");
            var archived = await archive.ListAsync("ws", evt.EventId);
            Assert.AreEqual(1, archived.Count);
            var snapshot = await sdk.GetAsync("prototype-work");
            Assert.AreEqual(AgentWorkStatus.Completed, snapshot!.Status);

            // B scenario 5 (replay): re-enqueue the committed event; the plugin requests archive again but the host
            // archive replays the same OperationId and the observer still owns one logical record.
            await deliveryStore.EnqueueAsync(subscription.SubscriptionId, page.Events);
            var replay = await worker.RunOnceAsync();
            Assert.AreEqual(1, replay.Delivered, "replay is delivered to the observer");
            Assert.AreEqual(1, (await archive.ListAsync("ws", evt.EventId)).Count,
                "archive replays without a second logical record");

            // B scenario 6 (unload/reload): old handle cannot start a new side effect; reload acquires a fresh handle.
            var staleGeneration = Math.Max(1, observer.Revision);
            var staleEvent = new PluginWorkspaceEvent(evt.EventId, evt.Kind,
                new PluginScope("ws", "", "", "host"),
                JsonSerializer.SerializeToElement(new { payloadRef = evt.PayloadRef, payloadHash = evt.PayloadHash }),
                evt.OccurredAt);
            await plugins.Manager.UnloadAsync(observer.ActivationId, observerScope);
            await new PluginSubscriptionSynchronizer(stock).SyncAsync([], ["ws"]);
            Assert.AreEqual(0, (await plugins.Manager.PublishWorkspaceEventAsync(staleEvent, "sample.observer",
                staleGeneration)).Count, "unloaded handle must not deliver");
            var v2 = await artifacts.CommitAsync("ws", receipt.WorkId, "1.1.0", "ref://work/changed", "sha256-changed");
            var v2Event = (await outboxLog.ReadAsync(new("ws", 0, 100))).Events
                .Single(e => e.BusinessRef == receipt.WorkId + "@1.1.0");
            var afterUnload = await worker.RunOnceAsync();
            Assert.AreEqual(0, afterUnload.Delivered, "unloaded observer receives no new artifact");
            var unloadArchived = (await archive.ListAsync("ws", v2Event.EventId)).Count;
            Assert.AreEqual(0, unloadArchived);

            var reloaded = await plugins.Manager.LoadAsync("reload-observer", observerPackage, observerScope,
                new PluginAuthorization(false, new HashSet<string>(), false, SubscriptionsApproved: true));
            Assert.AreEqual("Active", reloaded.State, reloaded.Failure?.Message);
            await new PluginSubscriptionSynchronizer(stock).SyncAsync(
                [new PluginSubscriptionCandidate("ws", "sample.observer", Math.Max(1, reloaded.Revision),
                    [PluginEvents.WorkspaceArtifactCreated])], ["ws"]);
            await artifacts.CommitAsync("ws", receipt.WorkId, "1.2.0", "ref://work/reloaded", "sha256-reloaded");
            var v3Event = (await outboxLog.ReadAsync(new("ws", 0, 100))).Events
                .Single(e => e.BusinessRef == receipt.WorkId + "@1.2.0");
            var afterReload = await worker.RunOnceAsync();
            Assert.IsTrue(afterReload.Acknowledged >= 1, "reloaded observer processes pending/new artifacts");
            Assert.AreEqual(1, (await archive.ListAsync("ws", v3Event.EventId)).Count);

            // B scenario: cancel a controlled blocking Work; no artifact is committed, terminal is Cancelled.
            var cancelAgent = new AgentRef("ws2", "agent2");
            var cancelSdk = new AgentWorkClient(provider.GetRequiredService<AgentWorkApiFactory>()
                .ForManagementUser(principal, cancelAgent)).For(cancelAgent);
            var cancelReceipt = await cancelSdk.SubmitAsync(new("cancel-work", "执行阻塞工具并取消。"));
            var blocking = new BlockingScriptModel(plugins);
            var (cancelLoop, cancelControl) = BuildLoop(blocking, new ToolInvocationService(executor),
                new PuddingToolSchemaService(registry), [new ExecutablePluginHook(plugins)], root);
            var cancelDispatch = new RuntimeDispatchRequest
            {
                SessionId = cancelReceipt.ConversationId,
                AgentInstanceId = cancelAgent.AgentId,
                WorkspaceId = cancelAgent.WorkspaceId,
                AgentTemplateId = "prototype",
                WorkingDirectory = root,
                MessageText = "执行阻塞工具并取消。",
                MaxRounds = 3,
                SuppressContextAutoCompaction = true,
                CapabilityPolicy = new()
                {
                    DefaultToolNames = ["hang"],
                    AllowedToolNames = ["hang"],
                },
                LlmConfig = new() { ModelId = "scripted-model", MaxContextTokens = 16_384 },
            };
            var cancelWorkDispatcher = new RuntimeAgentDispatcher(cancelLoop, new AgentExecutionStateRegistry());
            var cancelRun = cancelWorkDispatcher.DispatchAsync(cancelDispatch);
            await blocking.Started.Task.WaitAsync(TimeSpan.FromSeconds(20));
            var workControl = new CanonicalAgentWorkControlBackend(provider.GetRequiredService<IServiceScopeFactory>(),
                provider.GetRequiredService<IAgentWorkBackend>());
            var cancelResult = await workControl.CancelAsync(new WorkCaller("prototype"), cancelAgent,
                "prototype", "cancel-work", cancelReceipt.AcceptedSequence, "user.cancelled", default);
            Assert.AreEqual("requested", cancelResult.Effect, "the canonical Work control authority accepts the cancel");
            Assert.AreEqual(ControlMessageKind.CancelRequested, controlSink.Commands.Single().Kind);
            cancelControl.Cancel(cancelReceipt.ConversationId);
            try { await cancelRun.WaitAsync(TimeSpan.FromSeconds(20)); }
            catch (OperationCanceledException) { }
            await FinishWorkAsync(provider, cancelReceipt, TurnTerminal.Cancelled, "ws2");
            var cancelSnapshot = await cancelSdk.GetAsync("cancel-work");
            Assert.AreEqual(AgentWorkStatus.Cancelled, cancelSnapshot!.Status);
            Assert.IsNull(await artifacts.GetAsync("ws2", cancelReceipt.WorkId, "1.0.0"),
                "cancelled Work must not commit an artifact");

            var output = JsonSerializer.Serialize(new
            {
                workId = receipt.WorkId,
                operationId = receipt.OperationId,
                artifactId = artifact!.ArtifactId,
                version = artifact.Version,
                contentRef = artifact.ContentRef,
                eventId = evt.EventId,
                archiveId = archived[0].ArchiveId,
                archiveOperationId = archived[0].OperationId,
                delivery = new { dispatchResult.Delivered, dispatchResult.Acknowledged },
                snapshot = snapshot.Status.ToString(),
                replay = new { replay.Delivered, archiveCount = (await archive.ListAsync("ws", evt.EventId)).Count },
                unload = new { afterUnload.Delivered, archived = unloadArchived },
                reload = new { afterReload.Acknowledged, archived = (await archive.ListAsync("ws", v3Event.EventId)).Count },
                cancel = new { status = cancelSnapshot.Status.ToString(),
                    artifact = (await artifacts.GetAsync("ws2", cancelReceipt.WorkId, "1.0.0")) is not null },
            });
            var receiptPath = Environment.GetEnvironmentVariable("PUDDING_PROTOTYPE_RECEIPT")
                ?? Path.Combine(root, "receipt.json");
            await File.WriteAllTextAsync(receiptPath, output);
            Console.WriteLine("PROTOTYPE_RECEIPT " + output);
        }
        finally
        {
            await connection.DisposeAsync();
            try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task FinishWorkAsync(IServiceProvider provider, AgentWorkReceipt receipt, TurnTerminal terminal, string workspaceId = "ws")
    {
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var command = await db.ChatExecutionCommands.SingleAsync(x => x.CommandId == receipt.CommandId);
        var runId = Guid.NewGuid().ToString("N");
        command.Status = "running";
        command.RunId = runId;
        var turn = await db.ConversationTurns.SingleAsync(x => x.TurnId == receipt.TurnId);
        turn.Status = "running";
        var run = new ExecutionRunEntity
        {
            RunId = runId, CommandId = command.CommandId, ConversationId = receipt.ConversationId,
            TurnId = receipt.TurnId, Attempt = 1, WorkerId = "prototype", Status = "running",
            LeaseUntil = DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds(),
        };
        db.ExecutionRuns.Add(run);
        await db.SaveChangesAsync();
        var lease = new ExecutionLease(command.CommandId, "prototype", workspaceId, receipt.ConversationId,
            receipt.TurnId, runId, run.FencingToken, DateTimeOffset.UtcNow.AddMinutes(1)) { TraceId = null };
        var journal = new SqliteExecutionJournal(provider.GetRequiredService<IServiceScopeFactory>(),
            new NoSignal(), NullLogger<SqliteExecutionJournal>.Instance);
        await journal.CommitTerminalAsync(lease, terminal, [], default);
    }

    private static async Task<PluginPackage> ImportAsync(PluginPackageStore store, string fixturePath,
        PluginManifest manifest)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("plugin.json").Open()))
                writer.Write(JsonSerializer.Serialize(manifest, PluginJson.Options));
            using var target = zip.CreateEntry(manifest.Entry.Assembly).Open();
            using var source = File.OpenRead(fixturePath);
            source.CopyTo(target);
        }
        stream.Position = 0;
        return await store.ImportAsync(stream, default);
    }

    private static PluginManifest ToolManifest() => new()
    {
        Id = "sample.fixture", Name = "Prototype tool", Version = "1.0.0",
        Entry = new("bin/Sample.PluginFixture.dll", "Sample.PluginFixture.FixturePlugin"),
        Hooks = [new("advice", "tool.result.advice")],
        Tools = new[] { "echo", "read_file", "hang", "flood", "crash", "child_hang" }
            .Select(id => new PluginToolDefinition
            {
                Id = id, Name = id, Description = "Prototype fixture " + id,
                InputSchema = PluginJson.Value(new { type = "object", properties = new { text = new { type = "string" } },
                    required = new[] { "text" }, additionalProperties = false }),
                OutputSchema = PluginJson.Value(new { type = "string" }),
            }).ToArray(),
    };

    private static PluginManifest ObserverManifest() => new()
    {
        Id = "sample.observer", Name = "Workspace observer", Version = "1.0.0",
        Entry = new("bin/Sample.PluginFixture.dll", "Sample.PluginFixture.WorkspaceObserverPlugin"),
        Limits = new(30, 1),
        Subscriptions = [new("archive", PluginEvents.WorkspaceArtifactCreated)],
    };

    private sealed class ArtifactPublishTool(WorkspaceArtifactService artifacts) : IPuddingTool
    {
        public ToolDescriptor Descriptor { get; } = new()
        {
            ToolId = "publish_artifact", Name = "publish_artifact", Description = "Commit a versioned artifact.",
            Category = ToolCategory.Execute, PermissionLevel = ToolPermissionLevel.High,
        };

        public async Task<ToolExecutionResult> ExecuteAsync(ToolExecutionRequest request, CancellationToken ct = default)
        {
            using var document = JsonDocument.Parse(request.ArgumentsJson);
            var root = document.RootElement;
            var artifactId = root.GetProperty("artifactId").GetString()!;
            var version = root.GetProperty("version").GetString()!;
            var contentRef = root.GetProperty("contentRef").GetString()!;
            var payloadHash = root.GetProperty("payloadHash").GetString()!;
            var record = await artifacts.CommitAsync("ws", artifactId, version, contentRef, payloadHash, ct);
            return new()
            {
                Success = true,
                Output = JsonSerializer.Serialize(record),
                ExitCode = 0,
            };
        }
    }

    private sealed class PrototypeScriptModel(ExecutablePluginService plugins, string workId) : IRuntimeLlmClient
    {
        private int _calls;
        private string? _echoName;

        private LlmResponse Next(IReadOnlyList<LlmToolDefinition>? tools)
        {
            var names = tools?.Select(t => t.Name).ToArray() ?? [];
            switch (++_calls)
            {
                case 1:
                    CollectionAssert.Contains(names, "publish_artifact");
                    return new(null, [new("artifact", "publish_artifact", JsonSerializer.Serialize(new
                    {
                        artifactId = workId, version = "1.0.0", contentRef = "ref://work/" + workId,
                        payloadHash = "sha256-" + workId,
                    }))]);
                case 2:
                    _echoName = plugins.Manager.List().Where(a => a.State == "Active")
                        .SelectMany(a => a.ToolNames).Single(n => n.EndsWith("_echo"));
                    CollectionAssert.Contains(names, _echoName);
                    return new(null, [new("echo", _echoName, "{\"text\":\"插件真实回执\"}")]);
                default:
                    return new("{\"status\":\"DONE\",\"message\":\"产物已提交并归档。\"}", null);
            }
        }

        public Task<LlmResponse> ChatAsync(string workspaceId, string sessionId, string agentTemplateId,
            IReadOnlyList<ChatMessage> messages, IReadOnlyList<LlmToolDefinition>? tools = null,
            LlmConfig? llmConfig = null, CancellationToken ct = default) => Task.FromResult(Next(tools));

        public async IAsyncEnumerable<StreamDelta> ChatStreamAsync(string workspaceId, string sessionId,
            string agentTemplateId, IReadOnlyList<ChatMessage> messages,
            IReadOnlyList<LlmToolDefinition>? tools = null, LlmConfig? llmConfig = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            var response = Next(tools);
            foreach (var call in response.ToolCalls ?? [])
                yield return new() { ToolCallIndex = 0, ToolCallId = call.Id, ToolCallNameDelta = call.Name,
                    ToolCallArgsDelta = call.ArgumentsJson };
            yield return new() { ContentDelta = response.Content,
                FinishReason = response.ToolCalls?.Count > 0 ? "tool_calls" : "stop" };
            await Task.CompletedTask;
        }
    }

    private sealed class ManagerTarget(ExecutablePluginService plugins, PluginArchiveStore archive)
        : IWorkspacePluginEventTarget
    {
        public async ValueTask<IReadOnlyList<PluginEventDelivery>> DeliverAsync(PluginWorkspaceEvent workspaceEvent,
            string pluginId, long generation, CancellationToken ct = default)
        {
            var payloadRef = workspaceEvent.Payload.TryGetProperty("payloadRef", out var refNode)
                ? refNode.GetString() ?? ""
                : "";
            var payloadHash = workspaceEvent.Payload.TryGetProperty("payloadHash", out var hashNode)
                ? hashNode.GetString() ?? ""
                : "";
            async Task<JsonElement> Callback(string method, JsonElement arguments, CancellationToken token)
            {
                if (method != "plugin.archive.record")
                    throw new PluginException("plugin.core_callback_denied", "core_callback", "denied");
                var requested = arguments.TryGetProperty("eventId", out var idNode) ? idNode.GetString() : null;
                if (requested != workspaceEvent.EventId)
                    throw new PluginException("plugin.core_callback_denied", "core_callback", "event mismatch");
                var receipt = await archive.RecordAsync(new PluginArchiveRecord(workspaceEvent.EventId,
                    workspaceEvent.EventId, workspaceEvent.Scope.WorkspaceId, pluginId, payloadRef, payloadHash),
                    token);
                return JsonSerializer.SerializeToElement(new { accepted = true, archiveId = receipt.ArchiveId });
            }
            return await plugins.Manager.PublishWorkspaceEventAsync(workspaceEvent, pluginId, generation, Callback, ct);
        }
    }

    private sealed class FixedTarget : IAgentWorkTargetResolver
    {
        public Task<string> ResolveConversationAsync(WorkKey key, CancellationToken ct) => Task.FromResult("conversation");
    }

    private sealed class NoSignal : ICommittedEventSignal
    {
        public ValueTask WaitForChangeAsync(string conversationId, long knownHead, CancellationToken ct)
            => ValueTask.CompletedTask;
        public void Signal(string conversationId, long committedThroughSequence) { }
    }

    private sealed class Workspaces : IEventDispatchWorkspaceSource
    {
        public ValueTask<System.Collections.Immutable.ImmutableArray<string>> ListAsync(CancellationToken ct = default)
            => ValueTask.FromResult(System.Collections.Immutable.ImmutableArray.Create("ws"));
    }

    private sealed class DbFactory(DbContextOptions<PlatformDbContext> options) : IDbContextFactory<PlatformDbContext>
    {
        public PlatformDbContext CreateDbContext() => new(options);
    }

    private sealed class ControlSink : IExecutionControlService
    {
        public List<ExecutionControlCommand> Commands { get; } = [];
        public Task<ControlReceipt> SubmitAsync(ExecutionControlCommand command, CancellationToken ct)
        {
            Commands.Add(command);
            return Task.FromResult(new ControlReceipt(Guid.NewGuid().ToString("N"), Commands.Count, Commands.Count));
        }
    }

    private sealed class BlockingScriptModel(ExecutablePluginService plugins) : IRuntimeLlmClient
    {
        private int _calls;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private LlmResponse Next(IReadOnlyList<LlmToolDefinition>? tools)
        {
            if (++_calls == 1)
            {
                var hang = plugins.Manager.List().Where(a => a.State == "Active")
                    .SelectMany(a => a.ToolNames).First(n => n.EndsWith("_hang"));
                Started.TrySetResult();
                return new(null, [new("hang", hang, "{\"text\":\"blocked\"}")]);
            }
            return new("{\"status\":\"DONE\"}", null);
        }

        public Task<LlmResponse> ChatAsync(string workspaceId, string sessionId, string agentTemplateId,
            IReadOnlyList<ChatMessage> messages, IReadOnlyList<LlmToolDefinition>? tools = null,
            LlmConfig? llmConfig = null, CancellationToken ct = default) => Task.FromResult(Next(tools));

        public async IAsyncEnumerable<StreamDelta> ChatStreamAsync(string workspaceId, string sessionId,
            string agentTemplateId, IReadOnlyList<ChatMessage> messages,
            IReadOnlyList<LlmToolDefinition>? tools = null, LlmConfig? llmConfig = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            var response = Next(tools);
            foreach (var call in response.ToolCalls ?? [])
                yield return new() { ToolCallIndex = 0, ToolCallId = call.Id, ToolCallNameDelta = call.Name,
                    ToolCallArgsDelta = call.ArgumentsJson };
            yield return new() { ContentDelta = response.Content,
                FinishReason = response.ToolCalls?.Count > 0 ? "tool_calls" : "stop" };
            await Task.CompletedTask;
        }
    }

    private static (AgentExecutionService Loop, ExecutionControlRegistry Control) BuildLoop(IRuntimeLlmClient llm, IToolInvocationService tools,
        PuddingToolSchemaService schemas, IAgentLoopHook[] hooks, string root)
    {
        var sessions = new AgentSessionManager(NullLogger<AgentSessionManager>.Instance);
        var store = new InMemoryRuntimeSessionStore();
        var journal = new ExecutionJournal();
        var control = new ExecutionControlRegistry();
        var memory = Mock.Of<IMemoryEngine>();
        var sandbox = new SandboxExecutor(NullLogger<SandboxExecutor>.Instance);
        var packages = new AgentSkillPackageRegistry();
        var skills = new SkillRuntime([], sandbox, NullLogger<SkillRuntime>.Instance);
        var paths = PuddingDataPaths.FromRoot(root);
        var prompt = new SystemPromptBuilder(memory, skills, packages, NullLogger<SystemPromptBuilder>.Instance,
            new StartupEnvironmentInfo(), dataPaths: paths);
        var environment = Mock.Of<IExecutionEnvironmentProvider>(e => e.OsDescription == "Windows"
            && e.OsArchitecture == "X64" && e.RuntimeVersion == "10.0" && e.DefaultShell == "pwsh"
            && e.AppBaseDirectory == root && e.PathSeparator == "\\" && e.EnvironmentFingerprint == "prototype");
        var pipeline = new ContextPipeline(memory, skills, packages, prompt, new MemoryCache(new MemoryCacheOptions()),
            new ContextAssemblyStore(), NullLogger<ContextPipeline>.Instance, environment, dataPaths: paths);
        var manager = new ContextWindowManager(sessions, store, control, journal, NullLogger<ContextWindowManager>.Instance);
        return (new(sessions, store, memory, sandbox, llm, skills, new AgentExecutionGuardrails(), control, journal,
            new CompletionPolicy(), packages, new SkillPackageDownloadService(Mock.Of<IHttpClientFactory>(),
                NullLogger<SkillPackageDownloadService>.Instance), hooks, pipeline, manager,
            NullLogger<AgentExecutionService>.Instance, new SessionExecutionGate(NullLogger<SessionExecutionGate>.Instance),
            toolInvocationService: tools, toolSchemaService: schemas), control);
    }
}
