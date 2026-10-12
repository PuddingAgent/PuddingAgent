using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Observability;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingCode.Services;
using PuddingCode.SubAgents;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

[TestClass]
public sealed class SubAgentManagerMessageTests
{
    [TestMethod]
    public async Task SpawnAsync_SendsParentAgentMessage_WhenAsyncSubAgentCompletes()
    {
        var messageSystem = new RecordingMessageSystem();
        var dispatcher = new RecordingRuntimeAgentDispatcher();
        var services = new ServiceCollection()
            .AddSingleton<IMessageSystem>(messageSystem)
            .AddSingleton<IRuntimeAgentDispatcher>(dispatcher)
            .AddSingleton<IRuntimeExecutionConfigService>(new TestRuntimeExecutionConfigService())
            .BuildServiceProvider();

        var delegation = new RecordingBrowserDelegationLifecycle();
        var manager = new SubAgentManager(
            new RecordingSessionStateManager(),
            services,
            new RecordingInternalEventBus(),
            new RecordingSubAgentRunStore(),
            NullLogger<SubAgentManager>.Instance,
            new RecordingRuntimeActivitySink(),
            new RecordingRuntimeTraceAccessor(),
            services.GetRequiredService<IRuntimeExecutionConfigService>(),
            browserDelegation: delegation);

        var result = await manager.SpawnAsync(new SubAgentSpawnRequest
        {
            ParentSessionId = "parent-session",
            ParentAgentId = "agent-parent",
            WorkspaceId = "default",
            TaskDescription = "Return child result.",
            TemplateId = "workspace-task-agent",
            LlmConfig = CreateLlmConfig(),
            LlmProfile = CreateLlmProfile(),
        });

        Assert.IsTrue(result.Success);
        var envelope = await messageSystem.WaitForEnvelopeAsync();

        // R4 判别实验：完成已被观测之后，撤销应当已经发生（若仍为 0 ⇒ 覆盖缺口）。
        Assert.HasCount(1, delegation.RevokedRunIds, "revoked=" + string.Join(",", delegation.RevokedRunIds));
        Assert.AreEqual(result.RunId, delegation.RevokedRunIds[0]);

        Assert.AreEqual(MessageEndpointKinds.Agent, envelope.From.Kind);
        Assert.AreEqual(result.SubSessionId, envelope.From.Id);
        Assert.AreEqual(MessageAudiences.Direct, envelope.Audience);
        Assert.AreEqual(MessageVisibilities.System, envelope.Visibility);
        Assert.AreEqual("parent-session", envelope.ConversationId);
        Assert.HasCount(1, envelope.To);
        Assert.AreEqual(MessageEndpointKinds.Agent, envelope.To[0].Kind);
        Assert.AreEqual("agent-parent", envelope.To[0].Id);
        Assert.AreEqual("subagent_result", envelope.Metadata["intent"]);
        Assert.AreEqual("true", envelope.Metadata["requires_response"]);
        Assert.AreEqual("subagent_result", envelope.Metadata["message_type"]);
        Assert.AreEqual("1", envelope.Metadata["pudding_message_version"]);
        Assert.AreEqual(result.SubSessionId, envelope.Metadata["sub_agent_id"]);
        Assert.AreEqual("completed", envelope.Metadata["subagent_status"], envelope.Content);
        var rendered = AgentContextEnvelopeRenderer.TryParse(envelope.Content);
        Assert.IsNotNull(rendered);
        Assert.AreEqual(1, rendered!.Version);
        Assert.AreEqual("subagent_result", rendered.MessageType);
        Assert.AreEqual("text/markdown", rendered.Context.Format);
        StringAssert.Contains(rendered.Context.Text, "child ok");
        Assert.IsNotNull(dispatcher.LastRequest);
        Assert.AreEqual("test-provider", dispatcher.LastRequest!.LlmProfile?.ProviderId);
        Assert.AreEqual("subagent.conscious", dispatcher.LastRequest.LlmProfile?.ProfileId);
        Assert.AreEqual("test-model", dispatcher.LastRequest.LlmProfile?.ModelId);
        Assert.AreEqual("test-model", dispatcher.LastRequest.LlmConfig?.ModelId);
        Assert.AreEqual(SubAgentExecutionOptions.LargeTaskMaxRounds, dispatcher.LastRequest.MaxRounds);
        Assert.AreEqual(
            SubAgentExecutionOptions.LargeTaskMaxToolCallsTotal,
            dispatcher.LastRequest.MaxToolCallsTotal);
        Assert.AreEqual(
            SubAgentExecutionOptions.LargeTaskMaxTimeoutSeconds,
            dispatcher.LastRequest.MaxElapsedSeconds);
        Assert.AreEqual(
            SubAgentExecutionOptions.DefaultBudgetGraceRounds,
            dispatcher.LastRequest.BudgetGraceRounds);
        Assert.AreEqual(
            SubAgentExecutionOptions.DefaultBudgetGraceTimeoutSeconds,
            dispatcher.LastRequest.BudgetGraceTimeoutSeconds);
        Assert.IsFalse(dispatcher.LastRequest.IsResumedSubAgentRun);
    }

    [TestMethod]
    public async Task ExecuteSyncAsync_ReusedSessionPreservesIdentityAndStartsFreshBudgetWindow()
    {
        var dispatcher = new RecordingRuntimeAgentDispatcher();
        var manager = CreateManager(dispatcher);

        var result = await manager.ExecuteSyncAsync(new SubAgentSpawnRequest
        {
            ParentSessionId = "parent-session",
            ParentAgentId = "agent-parent",
            WorkspaceId = "default",
            TaskDescription = "Continue from the saved checkpoint.",
            TemplateId = "workspace-task-agent",
            LlmConfig = CreateLlmConfig(),
            LlmProfile = CreateLlmProfile(),
            ReuseSubSessionId = "sub-parent-session-resume",
        });

        Assert.AreEqual("sub-parent-session-resume", result.SubSessionId);
        Assert.IsNotNull(dispatcher.LastRequest);
        Assert.AreEqual("sub-parent-session-resume", dispatcher.LastRequest!.SessionId);
        Assert.IsTrue(dispatcher.LastRequest.IsResumedSubAgentRun);
        Assert.AreEqual(
            SubAgentExecutionOptions.LargeTaskMaxRounds,
            dispatcher.LastRequest.MaxRounds);
        Assert.AreEqual(
            SubAgentExecutionOptions.DefaultBudgetGraceTimeoutSeconds,
            dispatcher.LastRequest.BudgetGraceTimeoutSeconds);
    }

    [TestMethod]
    public async Task ExecuteSyncAsync_DefaultMaxRounds_ConvergesToLongRunLargeTaskBudget()
    {
        // N00：未显式请求预算时统一落长程默认 600 正常轮 / 2400 工具调用，
        // 不再收敛到旧的 25-40 WorkUnit 设计区间（用户已明确作废该口径）。
        Assert.AreEqual(600, SubAgentExecutionOptions.LargeTaskMaxRounds);
        Assert.AreEqual(2400, SubAgentExecutionOptions.LargeTaskMaxToolCallsTotal);

        var dispatcher = new RecordingRuntimeAgentDispatcher();
        var manager = CreateManager(dispatcher);

        var result = await manager.ExecuteSyncAsync(new SubAgentSpawnRequest
        {
            ParentSessionId = "parent-session",
            ParentAgentId = "agent-parent",
            WorkspaceId = "default",
            TaskDescription = "Default long-run budget probe.",
            TemplateId = "workspace-task-agent",
            LlmConfig = CreateLlmConfig(),
            LlmProfile = CreateLlmProfile(),
        });

        Assert.IsNotNull(result);
        Assert.IsNotNull(dispatcher.LastRequest);
        Assert.AreEqual(
            SubAgentExecutionOptions.LargeTaskMaxRounds,
            dispatcher.LastRequest.MaxRounds);
        Assert.AreEqual(
            SubAgentExecutionOptions.LargeTaskMaxToolCallsTotal,
            dispatcher.LastRequest.MaxToolCallsTotal);
    }

    [TestMethod]
    public async Task ExecuteSyncAsync_ExplicitSmallBudget_IsRespectedWithoutElevation()
    {
        // N00：显式小预算（如 50 轮 / 100 工具调用）必须原样生效，不得被抬到 600/2400。
        var dispatcher = new RecordingRuntimeAgentDispatcher();
        var manager = CreateManager(dispatcher);

        var result = await manager.ExecuteSyncAsync(new SubAgentSpawnRequest
        {
            ParentSessionId = "parent-session",
            ParentAgentId = "agent-parent",
            WorkspaceId = "default",
            TaskDescription = "Explicit small budget probe.",
            TemplateId = "workspace-task-agent",
            LlmConfig = CreateLlmConfig(),
            LlmProfile = CreateLlmProfile(),
            MaxRounds = 50,
            MaxToolCallsTotal = 100,
        });

        Assert.IsNotNull(result);
        Assert.IsNotNull(dispatcher.LastRequest);
        Assert.AreEqual(50, dispatcher.LastRequest.MaxRounds);
        Assert.AreEqual(100, dispatcher.LastRequest.MaxToolCallsTotal);
    }

    [TestMethod]
    public async Task ExecuteSyncAsync_ExplicitLargeTaskBudget_IsRespected()
    {
        // N00：显式请求 600 正常轮 / 2400 工具调用（含 managed WorkUnit）原样生效，
        // 不再被 Math.Min 压回 40 轮 / 120 工具调用。
        var dispatcher = new RecordingRuntimeAgentDispatcher();
        var manager = CreateManager(dispatcher);

        await manager.ExecuteSyncAsync(new SubAgentSpawnRequest
        {
            ParentSessionId = "parent-session",
            ParentAgentId = "agent-parent",
            WorkspaceId = "default",
            TaskDescription = "Run an authorized long-run child task.",
            TemplateId = "workspace-task-agent",
            LlmConfig = CreateLlmConfig(),
            LlmProfile = CreateLlmProfile(),
            MaxRounds = SubAgentExecutionOptions.LargeTaskMaxRounds,
            MaxToolCallsTotal = SubAgentExecutionOptions.LargeTaskMaxToolCallsTotal,
        });

        Assert.IsNotNull(dispatcher.LastRequest);
        Assert.AreEqual(
            SubAgentExecutionOptions.LargeTaskMaxRounds,
            dispatcher.LastRequest.MaxRounds);
        Assert.AreEqual(
            SubAgentExecutionOptions.LargeTaskMaxToolCallsTotal,
            dispatcher.LastRequest.MaxToolCallsTotal);
    }

    [TestMethod]
    public async Task SpawnAsync_MaxRoundsBeyondGuardrail_IsRejected()
    {
        var dispatcher = new RecordingRuntimeAgentDispatcher();
        var manager = CreateManager(dispatcher);

        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            manager.SpawnAsync(new SubAgentSpawnRequest
            {
                ParentSessionId = "parent-session",
                ParentAgentId = "agent-parent",
                WorkspaceId = "default",
                TaskDescription = "Must not dispatch.",
                TemplateId = "workspace-task-agent",
                LlmConfig = CreateLlmConfig(),
                LlmProfile = CreateLlmProfile(),
                MaxRounds = SubAgentExecutionOptions.LargeTaskMaxRounds + 1,
            }));

        StringAssert.Contains(error.Message, "exceeds configured maxRounds");
        Assert.IsNull(dispatcher.LastRequest);
    }

    [TestMethod]
    public async Task SpawnAsync_PropagatesToolFailureDiagnostics_ToParentAgentMessage()
    {
        var messageSystem = new RecordingMessageSystem();
        var services = new ServiceCollection()
            .AddSingleton<IMessageSystem>(messageSystem)
            .AddSingleton<IRuntimeAgentDispatcher>(new RecordingRuntimeAgentDispatcher(new RuntimeDispatchResult
            {
                SessionId = "will-be-overwritten-by-dispatcher",
                AgentInstanceId = "workspace-task-agent",
                IsSuccess = false,
                ErrorMessage = "shell: Command timed out after 30 seconds.",
                ExecutionState = AgentExecutionState.Failed,
                ToolFailureCount = 1,
                ToolOutputTruncatedCount = 1,
                ToolOutputChars = 100_088,
                ToolFailureSummary = "shell: Command timed out after 30 seconds.",
            }))
            .AddSingleton<IRuntimeExecutionConfigService>(new TestRuntimeExecutionConfigService())
            .BuildServiceProvider();

        var delegation = new RecordingBrowserDelegationLifecycle();
        var manager = new SubAgentManager(
            new RecordingSessionStateManager(),
            services,
            new RecordingInternalEventBus(),
            new RecordingSubAgentRunStore(),
            NullLogger<SubAgentManager>.Instance,
            new RecordingRuntimeActivitySink(),
            new RecordingRuntimeTraceAccessor(),
            services.GetRequiredService<IRuntimeExecutionConfigService>(),
            browserDelegation: delegation);

        var result = await manager.SpawnAsync(new SubAgentSpawnRequest
        {
            ParentSessionId = "parent-session",
            ParentAgentId = "agent-parent",
            WorkspaceId = "default",
            TaskDescription = "Return child result.",
            TemplateId = "workspace-task-agent",
            LlmConfig = CreateLlmConfig(),
            LlmProfile = CreateLlmProfile(),
        });

        Assert.IsTrue(result.Success);
        var envelope = await messageSystem.WaitForEnvelopeAsync();

        // R4 ⑦：**失败**收尾同样必须撤销（统一撤销四点之二）—— 完成已被观测之后再断言。
        Assert.HasCount(1, delegation.RevokedRunIds, "revoked=" + string.Join(",", delegation.RevokedRunIds));
        Assert.AreEqual(result.RunId, delegation.RevokedRunIds[0]);

        Assert.AreEqual("subagent_result", envelope.Metadata["intent"]);
        Assert.AreEqual("failed", envelope.Metadata["subagent_status"]);
        Assert.AreEqual("1", envelope.Metadata["tool_failure_count"], envelope.Content);
        Assert.AreEqual("1", envelope.Metadata["tool_output_truncated_count"]);
        Assert.AreEqual("100088", envelope.Metadata["tool_output_chars"]);
        Assert.AreEqual("shell: Command timed out after 30 seconds.", envelope.Metadata["tool_failure_summary"]);
        var rendered = AgentContextEnvelopeRenderer.TryParse(envelope.Content);
        Assert.IsNotNull(rendered);
        Assert.AreEqual("failed", rendered!.Metadata["subagent_status"]);
        Assert.AreEqual("1", rendered.Metadata["tool_failure_count"]);
        StringAssert.Contains(rendered.Context.Text, "Command timed out after 30 seconds.");
    }

    [TestMethod]
    public async Task ExecuteSyncAsync_RejectsMismatchedLlmRoute_BeforeDispatch()
    {
        var dispatcher = new RecordingRuntimeAgentDispatcher();
        var manager = CreateManager(dispatcher);

        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            manager.ExecuteSyncAsync(new SubAgentSpawnRequest
            {
                ParentSessionId = "parent-session",
                ParentAgentId = "agent-parent",
                WorkspaceId = "default",
                TaskDescription = "Must not dispatch.",
                TemplateId = "workspace-task-agent",
                LlmConfig = CreateLlmConfig() with { ModelId = "other-model" },
                LlmProfile = CreateLlmProfile(),
            }));

        StringAssert.Contains(error.Message, "does not match config model");
        Assert.IsNull(dispatcher.LastRequest);
    }

    [TestMethod]
    public async Task ExecuteSyncAsync_PreservesTimeoutTerminalStateAndConfigurationOwner()
    {
        var dispatcher = new RecordingRuntimeAgentDispatcher(new RuntimeDispatchResult
        {
            SessionId = "overwritten",
            AgentInstanceId = "overwritten",
            IsSuccess = false,
            ErrorMessage = "Execution timed out.",
            StopReason = "MaxElapsedReached",
            ExecutionState = AgentExecutionState.Failed,
        });
        var manager = CreateManager(dispatcher);

        var result = await manager.ExecuteSyncAsync(new SubAgentSpawnRequest
        {
            ParentSessionId = "parent-session",
            ParentAgentId = "ephemeral-planner",
            ConfigurationAgentInstanceId = "persistent-root-agent",
            WorkspaceId = "default",
            TaskDescription = "Explore missing evidence.",
            TemplateId = "workspace-task-agent",
            LlmConfig = CreateLlmConfig(),
            LlmProfile = CreateLlmProfile(),
        });

        Assert.IsFalse(result.Success);
        Assert.AreEqual("timed_out", result.Status);
        Assert.IsNotNull(dispatcher.LastRequest);
        Assert.AreEqual(
            "persistent-root-agent",
            dispatcher.LastRequest!.ConfigurationAgentInstanceId);
    }

    [TestMethod]
    public async Task ExecuteSyncAsync_ClampsChildDeadlineToParentDeadline()
    {
        var dispatcher = new RecordingRuntimeAgentDispatcher();
        var manager = CreateManager(dispatcher);
        var parentDeadlineUtc = DateTimeOffset.UtcNow.AddMinutes(10);

        var result = await manager.ExecuteSyncAsync(new SubAgentSpawnRequest
        {
            ParentSessionId = "parent-session",
            ParentAgentId = "agent-parent",
            WorkspaceId = "default",
            TaskDescription = "Respect the parent deadline.",
            TemplateId = "workspace-task-agent",
            LlmConfig = CreateLlmConfig(),
            LlmProfile = CreateLlmProfile(),
            TimeoutSeconds = 1800,
            ParentExecutionDeadlineUtc = parentDeadlineUtc,
        });

        Assert.IsTrue(result.Success);
        Assert.IsNotNull(dispatcher.LastRequest);
        Assert.AreEqual(parentDeadlineUtc.AddSeconds(-120), dispatcher.LastRequest!.ExecutionDeadlineUtc);
        Assert.IsGreaterThanOrEqualTo(475, dispatcher.LastRequest.MaxElapsedSeconds);
        Assert.IsLessThanOrEqualTo(480, dispatcher.LastRequest.MaxElapsedSeconds);
    }

    [TestMethod]
    public async Task ExecuteSyncAsync_RejectsWhenOnlyParentFinalizationReserveRemains()
    {
        var dispatcher = new RecordingRuntimeAgentDispatcher();
        var manager = CreateManager(dispatcher);

        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            manager.ExecuteSyncAsync(new SubAgentSpawnRequest
            {
                ParentSessionId = "parent-session",
                ParentAgentId = "agent-parent",
                WorkspaceId = "default",
                TaskDescription = "Must leave time for the parent response.",
                TemplateId = "workspace-task-agent",
                LlmConfig = CreateLlmConfig(),
                LlmProfile = CreateLlmProfile(),
                TimeoutSeconds = 600,
                ParentExecutionDeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(90),
            }));

        StringAssert.Contains(error.Message, "insufficient_execution_budget");
        StringAssert.Contains(error.Message, "reserving 120s");
        Assert.IsNull(dispatcher.LastRequest);
    }

    [TestMethod]
    public async Task ExecuteSyncAsync_RejectsMissingLlmProfile_BeforeDispatch()
    {
        var dispatcher = new RecordingRuntimeAgentDispatcher();
        var manager = CreateManager(dispatcher);

        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            manager.ExecuteSyncAsync(new SubAgentSpawnRequest
            {
                ParentSessionId = "parent-session",
                ParentAgentId = "agent-parent",
                WorkspaceId = "default",
                TaskDescription = "Must not dispatch.",
                TemplateId = "workspace-task-agent",
                LlmConfig = CreateLlmConfig(),
                LlmProfile = null!,
            }));

        StringAssert.Contains(error.Message, "missing its LLM profile snapshot");
        Assert.IsNull(dispatcher.LastRequest);
    }

    // ── R4（§6.2）子委派申请决策：复用本类的私有假件，只额外注入委派端口（第 9 个末位参数） ──
    private sealed class RecordingBrowserDelegationLifecycle : IBrowserDelegationLifecycle
    {
        public int RequestCount { get; private set; }

        public ValueTask<BrowserDelegationGrantResult> RequestChildGrantAsync(
            BrowserDelegationGrantRequest request, CancellationToken cancellationToken)
        {
            RequestCount++;
            LastRequest = request;
            return ValueTask.FromResult(new BrowserDelegationGrantResult(
                "grant-1", request.ScopeName, DateTimeOffset.UnixEpoch.AddMinutes(5)));
        }

        public List<string> RevokedRunIds { get; } = [];

        public BrowserDelegationGrantRequest? LastRequest { get; private set; }

        public ValueTask RevokeForRunAsync(string runId, string reason, CancellationToken cancellationToken)
        {
            RevokedRunIds.Add(runId);
            return ValueTask.CompletedTask;
        }

        public ValueTask RevokeForTaskAsync(
            string callerKind, string callerId, string runId, string? parentRunId, string reason,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask OnConnectionLostAsync(string reason, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;
    }

    private static readonly System.Reflection.MethodInfo DelegationDecision =
        typeof(SubAgentManager).GetMethod(
            "TryRequestBrowserDelegationAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

    private static SubAgentManager CreateManagerWithDelegation(
        RecordingRuntimeAgentDispatcher dispatcher,
        IBrowserDelegationLifecycle? delegation)
    {
        var services = new ServiceCollection()
            .AddSingleton<IRuntimeAgentDispatcher>(dispatcher)
            .BuildServiceProvider();
        return new SubAgentManager(
            new RecordingSessionStateManager(),
            services,
            new RecordingInternalEventBus(),
            new RecordingSubAgentRunStore(),
            NullLogger<SubAgentManager>.Instance,
            new RecordingRuntimeActivitySink(),
            new RecordingRuntimeTraceAccessor(),
            new TestRuntimeExecutionConfigService(),
            browserDelegation: delegation);
    }

    private static SubAgentSpawnRequest DelegationRequest(
        string? contextId = null, string? pageId = null, string? scope = null) => new()
    {
        ParentSessionId = "session-1",
        WorkspaceId = "ws-1",
        TaskDescription = "delegated task",
        LlmConfig = CreateLlmConfig(),
        LlmProfile = CreateLlmProfile(),
        BrowserDelegationContextId = contextId,
        BrowserDelegationPageId = pageId,
        BrowserDelegationScopeName = scope,
    };

    private static async Task<string?> DecideDelegationAsync(
        SubAgentManager manager, SubAgentSpawnRequest request)
    {
        var task = (Task<string?>)DelegationDecision.Invoke(
            manager, [request, "run-child", CancellationToken.None])!;
        return await task;
    }

    [TestMethod]
    public async Task BrowserDelegation_NoPortMeansNoRequest()
    {
        var manager = CreateManagerWithDelegation(new RecordingRuntimeAgentDispatcher(), delegation: null);

        var failure = await DecideDelegationAsync(manager, DelegationRequest("ctx-1", "page-1", "read"));

        Assert.IsNull(failure);
    }

    [TestMethod]
    public async Task BrowserDelegation_UndeclaredMakesNoRequest()
    {
        var delegation = new RecordingBrowserDelegationLifecycle();
        var manager = CreateManagerWithDelegation(new RecordingRuntimeAgentDispatcher(), delegation);

        var failure = await DecideDelegationAsync(manager, DelegationRequest());

        Assert.IsNull(failure);
        Assert.AreEqual(0, delegation.RequestCount);
    }

    [TestMethod]
    public async Task BrowserDelegation_DeclaredButIncompleteIsRejected()
    {
        var delegation = new RecordingBrowserDelegationLifecycle();
        var manager = CreateManagerWithDelegation(new RecordingRuntimeAgentDispatcher(), delegation);

        var failure = await DecideDelegationAsync(manager, DelegationRequest(contextId: "ctx-1", pageId: "page-1"));

        Assert.IsNotNull(failure);
        StringAssert.Contains(failure!, "incomplete");
        Assert.AreEqual(0, delegation.RequestCount);
    }

    [TestMethod]
    public async Task BrowserDelegation_MissingTrustedParentRunIdIsRejected()
    {
        var delegation = new RecordingBrowserDelegationLifecycle();
        var manager = CreateManagerWithDelegation(new RecordingRuntimeAgentDispatcher(), delegation);

        var failure = await DecideDelegationAsync(manager, DelegationRequest("ctx-1", "page-1", "read"));

        Assert.IsNotNull(failure);
        StringAssert.Contains(failure!, "parent run id");
        Assert.AreEqual(0, delegation.RequestCount);
    }
    [TestMethod]
    public async Task BrowserDelegation_RevokesWhenStartProjectionFails()
    {
        var services = new ServiceCollection()
            .AddSingleton<IMessageSystem>(new RecordingMessageSystem())
            .AddSingleton<IRuntimeAgentDispatcher>(new RecordingRuntimeAgentDispatcher())
            .AddSingleton<IRuntimeExecutionConfigService>(new TestRuntimeExecutionConfigService())
            .BuildServiceProvider();

        var delegation = new RecordingBrowserDelegationLifecycle();
        var sessionState = new RecordingSessionStateManager
        {
            StartProjectionFailure = new InvalidOperationException("start projection failed"),
        };
        var manager = new SubAgentManager(
            sessionState,
            services,
            new RecordingInternalEventBus(),
            new RecordingSubAgentRunStore(),
            NullLogger<SubAgentManager>.Instance,
            new RecordingRuntimeActivitySink(),
            new RecordingRuntimeTraceAccessor(),
            services.GetRequiredService<IRuntimeExecutionConfigService>(),
            browserDelegation: delegation);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => manager.SpawnAsync(new SubAgentSpawnRequest
        {
            ParentSessionId = "parent-session",
            ParentAgentId = "agent-parent",
            WorkspaceId = "default",
            TaskDescription = "Should fail while projecting the start.",
            TemplateId = "workspace-task-agent",
            LlmConfig = CreateLlmConfig(),
            LlmProfile = CreateLlmProfile(),
        }));

        // R4 7: the startup-failure path must revoke too (4th site) -- it happens synchronously
        // inside the catch before the rethrow, so no completion wait is needed.
        Assert.HasCount(1, delegation.RevokedRunIds, "revoked=" + string.Join(",", delegation.RevokedRunIds));
    }
    [TestMethod]
    public async Task BrowserDelegation_GrantsForTheRealChildRunThenRevokes()
    {
        var messageSystem = new RecordingMessageSystem();
        var services = new ServiceCollection()
            .AddSingleton<IMessageSystem>(messageSystem)
            .AddSingleton<IRuntimeAgentDispatcher>(new RecordingRuntimeAgentDispatcher())
            .AddSingleton<IRuntimeExecutionConfigService>(new TestRuntimeExecutionConfigService())
            .BuildServiceProvider();

        var delegation = new RecordingBrowserDelegationLifecycle();
        var manager = new SubAgentManager(
            new RecordingSessionStateManager(),
            services,
            new RecordingInternalEventBus(),
            new RecordingSubAgentRunStore(),
            NullLogger<SubAgentManager>.Instance,
            new RecordingRuntimeActivitySink(),
            new RecordingRuntimeTraceAccessor(),
            services.GetRequiredService<IRuntimeExecutionConfigService>(),
            browserDelegation: delegation);

        var result = await manager.SpawnAsync(new SubAgentSpawnRequest
        {
            ParentSessionId = "parent-session",
            ParentAgentId = "agent-parent",
            WorkspaceId = "default",
            TaskDescription = "Return child result.",
            TemplateId = "workspace-task-agent",
            LlmConfig = CreateLlmConfig(),
            LlmProfile = CreateLlmProfile(),
            BrowserDelegationContextId = "ctx-1",
            BrowserDelegationPageId = "page-1",
            BrowserDelegationScopeName = "read",
            ParentExecutionIdentity = new RuntimeExecutionIdentity
            {
                Kind = RuntimeExecutionKind.ConversationTurn,
                ConversationId = "parent-session",
                TurnId = "parent-turn",
                RunId = "parent-run",
                TraceId = null,
                ToolCallId = "parent-tool-call",
            },
        });

        await messageSystem.WaitForEnvelopeAsync();

        // R4 legitimate-authorization success case: the grant is requested for the REAL child run
        // (never the template/parent id), with the trusted parent identity and the declared target/scope.
        Assert.IsTrue(result.Success, "the delegated spawn should succeed");
        Assert.AreEqual(1, delegation.RequestCount);
        Assert.IsNotNull(delegation.LastRequest);
        Assert.AreEqual(result.RunId, delegation.LastRequest!.ChildRunId);
        Assert.AreEqual("parent-run", delegation.LastRequest.ParentRunId);
        Assert.AreEqual("ctx-1", delegation.LastRequest.ContextId);
        Assert.AreEqual("page-1", delegation.LastRequest.PageId);
        Assert.AreEqual("read", delegation.LastRequest.ScopeName);

        // 修复后：该路径的**收尾也必须撤销**（此前 out _ 丢弃 runId ⇒ 授权会泄漏）。
        Assert.HasCount(1, delegation.RevokedRunIds, "revoked=" + string.Join(",", delegation.RevokedRunIds));
        Assert.AreEqual(delegation.LastRequest!.ChildRunId, delegation.RevokedRunIds[0]);

        // ...and the completion still revokes that same run.
        Assert.HasCount(1, delegation.RevokedRunIds);
        Assert.AreEqual(result.RunId, delegation.RevokedRunIds[0]);
    }
    [TestMethod]
    public async Task BrowserDelegation_RevokesOnCancellation()
    {
        var dispatcher = new RecordingRuntimeAgentDispatcher(suspend: true);
        var services = new ServiceCollection()
            .AddSingleton<IMessageSystem>(new RecordingMessageSystem())
            .AddSingleton<IRuntimeAgentDispatcher>(dispatcher)
            .AddSingleton<IRuntimeExecutionConfigService>(new TestRuntimeExecutionConfigService())
            .BuildServiceProvider();

        var delegation = new RecordingBrowserDelegationLifecycle();
        var manager = new SubAgentManager(
            new RecordingSessionStateManager(),
            services,
            new RecordingInternalEventBus(),
            new RecordingSubAgentRunStore(),
            NullLogger<SubAgentManager>.Instance,
            new RecordingRuntimeActivitySink(),
            new RecordingRuntimeTraceAccessor(),
            services.GetRequiredService<IRuntimeExecutionConfigService>(),
            browserDelegation: delegation);

        var spawn = manager.SpawnAsync(new SubAgentSpawnRequest
        {
            ParentSessionId = "parent-session",
            ParentAgentId = "agent-parent",
            WorkspaceId = "default",
            TaskDescription = "Stays in flight until cancelled.",
            TemplateId = "workspace-task-agent",
            LlmConfig = CreateLlmConfig(),
            LlmProfile = CreateLlmProfile(),
        });

        var cancelled = 0;
        for (var attempt = 0; attempt < 100 && cancelled == 0; attempt++)
        {
            cancelled = await manager.CancelAllAsync("parent-session");
            if (cancelled == 0) { await Task.Delay(10); }
        }

        dispatcher.Release();
        try { await spawn; } catch { }

        Assert.IsTrue(cancelled >= 1, "CancelAllAsync must find the in-flight run; cancelled=" + cancelled);
        Assert.HasCount(1, delegation.RevokedRunIds, "revoked=" + string.Join(",", delegation.RevokedRunIds));
    }
    [TestMethod]
    public async Task BrowserDelegation_IsRequestedOnTheExecuteSyncPathToo()
    {
        // R4: BOTH entry points (SpawnAsync and ExecuteSyncAsync) must funnel through the same
        // CreateTrackedRunAsync => the same single delegation request call site (parity evidence).
        var messageSystem = new RecordingMessageSystem();
        var services = new ServiceCollection()
            .AddSingleton<IMessageSystem>(messageSystem)
            .AddSingleton<IRuntimeAgentDispatcher>(new RecordingRuntimeAgentDispatcher())
            .AddSingleton<IRuntimeExecutionConfigService>(new TestRuntimeExecutionConfigService())
            .BuildServiceProvider();

        var delegation = new RecordingBrowserDelegationLifecycle();
        var manager = new SubAgentManager(
            new RecordingSessionStateManager(),
            services,
            new RecordingInternalEventBus(),
            new RecordingSubAgentRunStore(),
            NullLogger<SubAgentManager>.Instance,
            new RecordingRuntimeActivitySink(),
            new RecordingRuntimeTraceAccessor(),
            services.GetRequiredService<IRuntimeExecutionConfigService>(),
            browserDelegation: delegation);

        await manager.ExecuteSyncAsync(new SubAgentSpawnRequest
        {
            ParentSessionId = "parent-session",
            ParentAgentId = "agent-parent",
            WorkspaceId = "default",
            TaskDescription = "Return child result.",
            TemplateId = "workspace-task-agent",
            LlmConfig = CreateLlmConfig(),
            LlmProfile = CreateLlmProfile(),
            BrowserDelegationContextId = "ctx-1",
            BrowserDelegationPageId = "page-1",
            BrowserDelegationScopeName = "read",
            ParentExecutionIdentity = new RuntimeExecutionIdentity
            {
                Kind = RuntimeExecutionKind.ConversationTurn,
                ConversationId = "parent-session",
                TurnId = "parent-turn",
                RunId = "parent-run",
                TraceId = null,
                ToolCallId = "parent-tool-call",
            },
        });

        Assert.AreEqual(1, delegation.RequestCount, "the sync path must request the delegation exactly once");
        Assert.IsNotNull(delegation.LastRequest);
        Assert.IsFalse(string.IsNullOrWhiteSpace(delegation.LastRequest!.ChildRunId));
        Assert.AreEqual("parent-run", delegation.LastRequest.ParentRunId);
        Assert.AreEqual("ctx-1", delegation.LastRequest.ContextId);
        Assert.AreEqual("read", delegation.LastRequest.ScopeName);

        // 修复后：该路径的**收尾也必须撤销**（此前 out _ 丢弃 runId ⇒ 授权会泄漏）。
        Assert.HasCount(1, delegation.RevokedRunIds, "revoked=" + string.Join(",", delegation.RevokedRunIds));
        Assert.AreEqual(delegation.LastRequest!.ChildRunId, delegation.RevokedRunIds[0]);
    }
    private static SubAgentManager CreateManager(RecordingRuntimeAgentDispatcher dispatcher)
    {
        var services = new ServiceCollection()
            .AddSingleton<IRuntimeAgentDispatcher>(dispatcher)
            .BuildServiceProvider();
        return new SubAgentManager(
            new RecordingSessionStateManager(),
            services,
            new RecordingInternalEventBus(),
            new RecordingSubAgentRunStore(),
            NullLogger<SubAgentManager>.Instance,
            new RecordingRuntimeActivitySink(),
            new RecordingRuntimeTraceAccessor(),
            new TestRuntimeExecutionConfigService());
    }

    private static LlmConfig CreateLlmConfig() => new()
    {
        Endpoint = "https://example.invalid/v1",
        ModelId = "test-model",
    };

    private static LlmInvocationProfile CreateLlmProfile() => new()
    {
        ProviderId = "test-provider",
        ProfileId = "subagent.conscious",
        ModelId = "test-model",
    };

    private sealed class TestRuntimeExecutionConfigService : IRuntimeExecutionConfigService
    {
        public RuntimeExecutionOptions GetOptions() => new();
    }

    private sealed class RecordingMessageSystem : IMessageSystem
    {
        private readonly TaskCompletionSource<MessageEnvelope> _sent =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<MessageSendResult> SendAsync(MessageEnvelope envelope, CancellationToken ct = default)
        {
            _sent.TrySetResult(envelope);
            return Task.FromResult(new MessageSendResult
            {
                MessageId = envelope.MessageId,
                RoomId = envelope.RoomId,
                DeliveryIds = ["delivery-1"],
            });
        }

        public async Task<MessageEnvelope> WaitForEnvelopeAsync()
            => await _sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class RecordingRuntimeAgentDispatcher : IRuntimeAgentDispatcher
    {
        private readonly RuntimeDispatchResult? _result;
        private readonly TaskCompletionSource<RuntimeDispatchResult>? _pending;
        public RuntimeDispatchRequest? LastRequest { get; private set; }

        public RecordingRuntimeAgentDispatcher(RuntimeDispatchResult? result = null, bool suspend = false)
        {
            _result = result;
            if (suspend) { _pending = new TaskCompletionSource<RuntimeDispatchResult>(TaskCreationOptions.RunContinuationsAsynchronously); }
        }

        public void Release()
        {
            var request = LastRequest;
            if (_pending is null || request is null) { return; }
            _pending.TrySetResult(_result ?? new RuntimeDispatchResult
            {
                SessionId = request.SessionId,
                AgentInstanceId = request.AgentTemplateId,
                IsSuccess = true,
                ReplyText = "child ok",
                ExecutionState = AgentExecutionState.Completed,
            });
        }

        public Task<RuntimeDispatchResult> DispatchAsync(RuntimeDispatchRequest request, CancellationToken ct = default)
        {
            LastRequest = request;
            if (_pending is not null) { return _pending.Task; }
            var result = _result ?? new RuntimeDispatchResult
            {
                SessionId = request.SessionId,
                AgentInstanceId = request.AgentTemplateId,
                IsSuccess = true,
                ReplyText = "child ok",
                ExecutionState = AgentExecutionState.Completed,
            };

            return Task.FromResult(result with
            {
                SessionId = request.SessionId,
                AgentInstanceId = request.AgentTemplateId,
            });
        }

        public async IAsyncEnumerable<ServerSentEventFrame> DispatchStreamAsync(
            RuntimeDispatchRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return ServerSentEventFrame.Json("done", new { ok = true });
            await Task.CompletedTask;
        }
    }

    private sealed class RecordingSessionStateManager : ISessionStateManager
    {
        public Task<long> AppendAsync(
            string sessionId,
            string workspaceId,
            ServerSentEventFrame frame,
            CancellationToken ct = default,
            RuntimeTraceContext? trace = null,
            string? component = null,
            string? operation = null) => Task.FromResult(1L);

        public ChannelReader<ServerSentEventFrame>? Subscribe(string sessionId) => null;
        public void Unsubscribe(string sessionId, ChannelReader<ServerSentEventFrame> reader) { }
        public ChannelReader<SessionNotification> SubscribeWorkspace(string workspaceId) =>
            Channel.CreateUnbounded<SessionNotification>().Reader;
        public void UnsubscribeWorkspace(string workspaceId, ChannelReader<SessionNotification> reader) { }
        public Task<SessionState> GetSessionStateAsync(string sessionId, CancellationToken ct = default) =>
            Task.FromResult(SessionState.Completed);
        /// <summary>R4 7: set non-null to make the start projection fail (covers the 4th revoke site).</summary>
        public Exception? StartProjectionFailure { get; set; }

        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, System.Collections.Generic.List<SubAgentStatus>> _subAgents =
            new(StringComparer.Ordinal);

        public Task TrackSubAgentStartAsync(string parentSessionId, SubAgentSpawnInfo info, CancellationToken ct = default)
        {
            if (StartProjectionFailure is not null)
            {
                return Task.FromException(StartProjectionFailure);
            }

            var list = _subAgents.GetOrAdd(parentSessionId, _ => []);
            lock (list)
            {
                list.RemoveAll(s => s.SubSessionId == info.SubSessionId);
                list.Add(new SubAgentStatus
                {
                    RunId = info.SubSessionId,
                    ParentSessionId = parentSessionId,
                    SubSessionId = info.SubSessionId,
                    Status = "running",
                    TaskSummary = info.TaskSummary ?? string.Empty,
                    SpawnedAt = info.SpawnedAt,
                });
            }

            return Task.CompletedTask;
        }
        public Task TrackSubAgentCompleteAsync(string subSessionId, SubAgentResult result, CancellationToken ct = default) =>
            Task.CompletedTask;
        public Task<IReadOnlyList<SubAgentStatus>> GetSubAgentsAsync(string sessionId, CancellationToken ct = default)
        {
            if (!_subAgents.TryGetValue(sessionId, out var list))
            {
                return Task.FromResult<IReadOnlyList<SubAgentStatus>>([]);
            }

            lock (list)
            {
                return Task.FromResult<IReadOnlyList<SubAgentStatus>>(list.ToList());
            }
        }
        public Task<int> GetRunningSubAgentCountAsync(string parentSessionId, CancellationToken ct = default) =>
            Task.FromResult(0);
        
        public Task MarkStreamCompleteAsync(string sessionId, CancellationToken ct = default) => Task.CompletedTask;
        public Task MarkSessionClosedAsync(string sessionId, CancellationToken ct = default) => Task.CompletedTask;
        
        public Task<SessionTraceReport> GetTraceReportAsync(string sessionId, bool includeSubAgents = false, CancellationToken ct = default) =>
            Task.FromResult(new SessionTraceReport
            {
                SessionId = sessionId,
                TraceIds = [],
                ComponentTimeline = [],
                LlmCalls = [],
                ToolCalls = [],
                SubAgents = [],
            });
        public void Restore(string sessionId, SessionState state) { }
    }

    private sealed class RecordingInternalEventBus : IInternalEventBus
    {
        public Task PublishAsync(InternalEvent evt, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IEventSubscriptionHandle> SubscribeAsync(
            string eventTypePattern,
            Func<InternalEvent, Task> handler,
            CancellationToken ct = default) =>
            Task.FromResult<IEventSubscriptionHandle>(new RecordingSubscriptionHandle(eventTypePattern));

        public Task UnsubscribeAsync(IEventSubscriptionHandle handle) => Task.CompletedTask;
    }

    private sealed class RecordingSubscriptionHandle(string eventTypePattern) : IEventSubscriptionHandle
    {
        public string SubscriptionId { get; } = "sub-1";
        public string EventTypePattern { get; } = eventTypePattern;
        public bool IsActive { get; private set; } = true;
        public void Dispose() => IsActive = false;
    }

    private sealed class RecordingSubAgentRunStore : ISubAgentRunStore
    {
        public SubAgentRunCreateRequest? LastCreateRequest { get; private set; }

        public Task<SubAgentRunHandle> CreateRunAsync(SubAgentRunCreateRequest request, CancellationToken ct = default)
        {
            LastCreateRequest = request;
            return Task.FromResult(new SubAgentRunHandle
            {
                RunId = "run-1",
                ArchivePath = "archive",
            });
        }

        public Task AppendEventAsync(string runId, string eventType, object payload, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task AppendToolAuditAsync(string runId, SubAgentToolAuditEntry entry, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<SubAgentRunTerminalWriteResult> CompleteRunAsync(
            string runId,
            SubAgentRunCompletion completion,
            CancellationToken ct = default) =>
            Task.FromResult(SubAgentRunTerminalWriteResult.Applied);

        public Task<SubAgentRunArchive?> GetRunArchiveAsync(string runId, CancellationToken ct = default) =>
            Task.FromResult<SubAgentRunArchive?>(null);

        public Task<int> RecoverInterruptedRunsAsync(
            DateTimeOffset startedBeforeUtc,
            int maxRuns,
            CancellationToken ct = default) =>
            Task.FromResult(0);

        public Task<int> ReplayPendingConversationEventsAsync(
            int maxRuns,
            CancellationToken ct = default) =>
            Task.FromResult(0);

                public Task<int> GetRunningCountByParentTurnAsync(string? parentTurnId, CancellationToken ct = default) =>
            Task.FromResult(0);

        public Task<bool> DeleteRunAsync(string runId, CancellationToken ct = default) =>
            Task.FromResult(false);
    }

    private sealed class RecordingRuntimeActivitySink : IRuntimeActivitySink
    {
        public Task RecordAsync(RuntimeActivity activity, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<RuntimeActivity>> QueryAsync(RuntimeActivityQuery query, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<RuntimeActivity>>([]);
    }

    private sealed class RecordingRuntimeTraceAccessor : IRuntimeTraceAccessor
    {
        public RuntimeTraceContext? Current { get; set; }
    }
}
