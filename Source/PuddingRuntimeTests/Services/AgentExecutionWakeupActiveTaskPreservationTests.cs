using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingCode.Tasks;
using PuddingRuntime.Services;
using PuddingRuntime.Services.AgentLoop;
using PuddingRuntime.Services.Skills;

namespace PuddingRuntimeTests.Services;

/// <summary>
/// TB-07 B2 端到端测试：验证「task 派发 → WAIT → wakeup → CONTINUE+task_update 工具调用 → DONE」
/// 全程 <see cref="ActiveTaskRuntimeContext"/> 不丢失。
///
/// 断言点 = 工具调用边界 <see cref="ToolInvocationRequest.ActiveTask"/> 与原值相等，
/// 这正是线上 task_claim/task_update 返回 active_context_missing 的故障点。
/// 生产代码零改动。
/// </summary>
[TestClass]
public sealed class AgentExecutionWakeupActiveTaskPreservationTests
{
    // 脚本化 LLM 响应（按轮次依次出队）。
    private const string WaitJson =
        "{\"status\":\"WAIT\",\"message\":\"awaiting external event\",\"meta\":{\"reason\":\"file.ready\"}}";

    private const string ContinueToolJson =
        "{\"status\":\"CONTINUE\",\"message\":\"proceeding\",\"tool\":{\"name\":\"task_update\",\"args\":{\"task_id\":\"task-1\",\"assignment_id\":\"assign-1\",\"status\":\"in_progress\"}}}";

    private const string DoneJson =
        "{\"status\":\"DONE\",\"message\":\"finished\"}";

    // ── 测试 1：半链（link 2）── WAIT 落锚时快照 ActiveTask ──────────────
    [TestMethod]
    public async Task ExecuteAsync_Wait_SetsAnchorWithActiveTaskSnapshot()
    {
        var journal = new ExecutionJournal();
        var tools = new RecordingToolInvocationService();
        var llm = new ScriptedLlmClient(WaitJson);
        var service = CreateService(llm, tools, journal);
        var activeTask = CreateActiveTask();
        const string sessionId = "session-anchor";

        var result = await service.ExecuteAsync(CreateDispatchRequest(sessionId, activeTask));

        Assert.AreEqual(AgentExecutionState.WaitingEvent, result.ExecutionState,
            "A WAIT response must put execution into WaitingEvent.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.ResumeAnchorId),
            "WAIT must produce a ResumeAnchorId.");

        var anchor = journal.GetAnchor(sessionId);
        Assert.IsNotNull(anchor, "A ResumeAnchor must be recorded for the WAIT session.");
        AssertActiveTaskEqual(activeTask, anchor!.ActiveTask);
    }

    // ── 测试 2：全链（link 2+3，B2 核心回归）── wakeup 把 ActiveTask 回填进工具调用 ──
    [TestMethod]
    public async Task ExecuteWakeupAsync_RestoresActiveTask_IntoToolInvocation()
    {
        var journal = new ExecutionJournal();
        var tools = new RecordingToolInvocationService();
        var llm = new ScriptedLlmClient(WaitJson, ContinueToolJson, DoneJson);
        var service = CreateService(llm, tools, journal);
        var activeTask = CreateActiveTask();
        const string sessionId = "session-wakeup";

        var first = await service.ExecuteAsync(CreateDispatchRequest(sessionId, activeTask));
        Assert.AreEqual(AgentExecutionState.WaitingEvent, first.ExecutionState,
            "Precondition: first run must enter WAIT.");

        var wakeup = await service.ExecuteWakeupAsync(CreateWakeupRequest(sessionId));

        Assert.AreEqual(AgentExecutionState.Completed, wakeup.ExecutionState,
            "Wakeup should continue through the tool call and reach DONE.");
        Assert.AreEqual(1, tools.Requests.Count,
            "Exactly one task_update tool invocation is expected across WAIT→wakeup.");
        Assert.IsNotNull(tools.Requests[0].ActiveTask,
            "The tool invocation must carry the restored ActiveTask context.");
        AssertActiveTaskEqual(activeTask, tools.Requests[0].ActiveTask);
    }

    // ── 测试 3：守卫 ── 无 ResumeAnchor 的 wakeup 必须失败 ─────────────────
    [TestMethod]
    public async Task ExecuteWakeupAsync_WithoutAnchor_ReturnsFailed()
    {
        var journal = new ExecutionJournal();
        var tools = new RecordingToolInvocationService();
        var llm = new ScriptedLlmClient(WaitJson, ContinueToolJson, DoneJson);
        var service = CreateService(llm, tools, journal);
        var activeTask = CreateActiveTask();
        const string sessionId = "session-no-anchor";

        // 建立 WAIT 锚点，再用一次成功的 wakeup 消费掉（ExecuteWakeupAsync 会 ClearAnchor）。
        await service.ExecuteAsync(CreateDispatchRequest(sessionId, activeTask));
        var firstWakeup = await service.ExecuteWakeupAsync(CreateWakeupRequest(sessionId));
        Assert.AreEqual(AgentExecutionState.Completed, firstWakeup.ExecutionState,
            "Precondition: first wakeup should complete and clear the anchor.");

        // 第二次 wakeup 已无锚点 → 失败。
        var secondWakeup = await service.ExecuteWakeupAsync(CreateWakeupRequest(sessionId));
        Assert.IsFalse(secondWakeup.IsSuccess);
        Assert.IsTrue(secondWakeup.ErrorMessage?.Contains("No ResumeAnchor found", StringComparison.Ordinal) == true,
            $"Expected 'No ResumeAnchor found' guard message, got: {secondWakeup.ErrorMessage}");
        Assert.AreEqual(AgentExecutionState.Failed, secondWakeup.ExecutionState);
    }

    // ── 测试 4：廉价守卫 ── ResumeAnchor.ActiveTask 可 JSON 往返 ───────────
    [TestMethod]
    public void ResumeAnchor_ActiveTaskJsonRoundTrip()
    {
        var anchor = new ResumeAnchor
        {
            AnchorId = "anchor-1",
            SessionId = "session-rt",
            CreatedAt = DateTimeOffset.UtcNow,
            WaitType = nameof(AgentExecutionState.WaitingEvent),
            WaitReason = "file.ready",
            LastRound = 0,
            ActiveTask = CreateActiveTask(),
        };

        var json = JsonSerializer.Serialize(anchor);
        var restored = JsonSerializer.Deserialize<ResumeAnchor>(json);

        Assert.IsNotNull(restored, "ResumeAnchor must deserialize back to a non-null instance.");
        Assert.AreEqual(anchor.AnchorId, restored!.AnchorId);
        Assert.AreEqual(anchor.SessionId, restored.SessionId);
        Assert.AreEqual(anchor.WaitType, restored.WaitType);
        Assert.AreEqual(anchor.LastRound, restored.LastRound);
        AssertActiveTaskEqual(anchor.ActiveTask, restored.ActiveTask);
    }

    // ── 测试 5：link 1 ── WorkspaceAgentInvocation 的 task metadata → ActiveTask 映射 ──
    [TestMethod]
    public async Task CreateForWorkspaceAgentAsync_MetadataTaskKeys_BuildActiveTask()
    {
        var profile = new AgentRuntimeProfile
        {
            WorkspaceId = "ws-1",
            AgentId = "agent-1",
            DisplayName = "Task Agent",
            MainSessionId = "session-1",
            SourceTemplateId = "workspace-task-agent",
            PreferredProviderId = "provider-1",
            PreferredModelId = "model-1",
            LlmConfig = CreateLlmConfig(),
        };
        var factory = new AgentInvocationDispatchFactory(
            new FakeAgentRuntimeProfileResolver(profile),
            NullLogger<AgentInvocationDispatchFactory>.Instance);

        var invocation = new WorkspaceAgentInvocation
        {
            WorkspaceId = "ws-1",
            AgentId = "agent-1",
            MessageId = "msg-1",
            MessageText = "please claim the task",
            Metadata = new Dictionary<string, string>
            {
                ["task_id"] = "task-1",
                ["assignment_id"] = "assign-1",
                ["origin"] = "task.manual",
                ["priority"] = "p1",
                ["execution_window"] = "anytime",
                ["expected_version"] = "3",
                ["policy_version"] = "v1",
                ["dispatch_idempotency_key"] = "idem-1",
            },
        };

        var dispatch = await factory.CreateForWorkspaceAgentAsync(invocation);

        Assert.IsFalse(dispatch.UsesStreamDispatch,
            "Ordinary task dispatch must use the buffered path (MainSessionId).");
        var activeTask = dispatch.Request.ActiveTask;
        Assert.IsNotNull(activeTask,
            "task_id + assignment_id metadata must build a non-null ActiveTask context.");
        AssertActiveTaskEqual(CreateActiveTask(), activeTask);
    }

    // ── Fixture 工厂 ────────────────────────────────────────────────────────
    private static AgentExecutionService CreateService(
        ScriptedLlmClient llm,
        RecordingToolInvocationService tools,
        ExecutionJournal journal)
    {
        var sessionManager = new AgentSessionManager(NullLogger<AgentSessionManager>.Instance);
        var runtimeSessionStore = new InMemoryRuntimeSessionStore();
        var memory = new FakeMemoryEngine();
        var sandbox = new SandboxExecutor(NullLogger<SandboxExecutor>.Instance);
        var guardrails = new AgentExecutionGuardrails();
        var controlRegistry = new ExecutionControlRegistry();
        var completionPolicy = new CompletionPolicy();
        var skillPackageRegistry = new AgentSkillPackageRegistry();
        var skillRuntime = new SkillRuntime(
            Array.Empty<IAgentSkill>(), sandbox, NullLogger<SkillRuntime>.Instance);
        var promptBuilder = new SystemPromptBuilder(
            memory, skillRuntime, skillPackageRegistry,
            NullLogger<SystemPromptBuilder>.Instance,
            new StartupEnvironmentInfo());
        var contextPipeline = new ContextPipeline(
            memory,
            skillRuntime,
            skillPackageRegistry,
            promptBuilder,
            new MemoryCache(new MemoryCacheOptions()),
            new ContextAssemblyStore(),
            NullLogger<ContextPipeline>.Instance,
            new FakeExecutionEnvironmentProvider());
        var contextManager = new ContextWindowManager(
            sessionManager,
            runtimeSessionStore,
            controlRegistry,
            journal,
            NullLogger<ContextWindowManager>.Instance);
        var sessionExecutionGate = new SessionExecutionGate(NullLogger<SessionExecutionGate>.Instance);
        var skillPackageDownloader = new SkillPackageDownloadService(
            new FakeHttpClientFactory(), NullLogger<SkillPackageDownloadService>.Instance);

        return new AgentExecutionService(
            sessionManager,
            runtimeSessionStore,
            memory,
            sandbox,
            llm,
            skillRuntime,
            guardrails,
            controlRegistry,
            journal,
            completionPolicy,
            skillPackageRegistry,
            skillPackageDownloader,
            Array.Empty<IAgentLoopHook>(),
            contextPipeline,
            contextManager,
            NullLogger<AgentExecutionService>.Instance,
            sessionExecutionGate,
            toolInvocationService: tools);
    }

    private static ActiveTaskRuntimeContext CreateActiveTask() => new()
    {
        WorkspaceId = "ws-1",
        TaskId = "task-1",
        AssignmentId = "assign-1",
        AgentId = "agent-1",
        Origin = "task.manual",
        Priority = "p1",
        ExecutionWindow = "anytime",
        ExpectedVersion = 3,
        PolicyVersion = "v1",
        DispatchIdempotencyKey = "idem-1",
    };

    private static LlmConfig CreateLlmConfig() => new()
    {
        ModelId = "test-model",
        MaxContextTokens = 8192,
    };

    private static RuntimeDispatchRequest CreateDispatchRequest(
        string sessionId,
        ActiveTaskRuntimeContext? activeTask) => new()
    {
        SessionId = sessionId,
        AgentTemplateId = "workspace-task-agent",
        MessageText = "please claim the task",
        WorkspaceId = "ws-1",
        AgentInstanceId = "agent-1",
        LlmConfig = CreateLlmConfig(),
        SuppressContextAutoCompaction = true,
        MaxRounds = 5,
        ActiveTask = activeTask,
    };

    private static DispatchWakeupRequest CreateWakeupRequest(string sessionId) => new()
    {
        SessionId = sessionId,
        AgentTemplateId = "workspace-task-agent",
        WorkspaceId = "ws-1",
        EventType = "file.ready",
        EventData = "the file is ready",
        LlmConfig = CreateLlmConfig(),
    };

    private static void AssertActiveTaskEqual(
        ActiveTaskRuntimeContext? expected,
        ActiveTaskRuntimeContext? actual)
    {
        Assert.IsNotNull(expected);
        Assert.IsNotNull(actual);
        if (expected is null || actual is null)
            return;

        Assert.AreEqual(expected.WorkspaceId, actual.WorkspaceId, nameof(ActiveTaskRuntimeContext.WorkspaceId));
        Assert.AreEqual(expected.TaskId, actual.TaskId, nameof(ActiveTaskRuntimeContext.TaskId));
        Assert.AreEqual(expected.AssignmentId, actual.AssignmentId, nameof(ActiveTaskRuntimeContext.AssignmentId));
        Assert.AreEqual(expected.AgentId, actual.AgentId, nameof(ActiveTaskRuntimeContext.AgentId));
        Assert.AreEqual(expected.Origin, actual.Origin, nameof(ActiveTaskRuntimeContext.Origin));
        Assert.AreEqual(expected.Priority, actual.Priority, nameof(ActiveTaskRuntimeContext.Priority));
        Assert.AreEqual(expected.ExecutionWindow, actual.ExecutionWindow, nameof(ActiveTaskRuntimeContext.ExecutionWindow));
        Assert.AreEqual(expected.ExpectedVersion, actual.ExpectedVersion, nameof(ActiveTaskRuntimeContext.ExpectedVersion));
        Assert.AreEqual(expected.PolicyVersion, actual.PolicyVersion, nameof(ActiveTaskRuntimeContext.PolicyVersion));
        Assert.AreEqual(expected.DispatchIdempotencyKey, actual.DispatchIdempotencyKey, nameof(ActiveTaskRuntimeContext.DispatchIdempotencyKey));
        Assert.AreEqual(expected.DeliveryId, actual.DeliveryId, nameof(ActiveTaskRuntimeContext.DeliveryId));
        Assert.AreEqual(expected.ReservationFencingToken, actual.ReservationFencingToken, nameof(ActiveTaskRuntimeContext.ReservationFencingToken));
    }

    // ── 测试替身 ────────────────────────────────────────────────────────────
    private sealed class ScriptedLlmClient : IRuntimeLlmClient
    {
        private readonly Queue<LlmResponse> _responses;

        public ScriptedLlmClient(params string[] contents)
            => _responses = new Queue<LlmResponse>(contents.Select(c => new LlmResponse(c, null)));

        public Task<LlmResponse> ChatAsync(
            string workspaceId,
            string sessionId,
            string agentTemplateId,
            IReadOnlyList<ChatMessage> messages,
            IReadOnlyList<LlmToolDefinition>? tools = null,
            LlmConfig? llmConfig = null,
            CancellationToken ct = default)
        {
            if (_responses.Count == 0)
                throw new InvalidOperationException(
                    "ScriptedLlmClient exhausted: more LLM rounds than scripted responses.");
            return Task.FromResult(_responses.Dequeue());
        }

        public IAsyncEnumerable<StreamDelta> ChatStreamAsync(
            string workspaceId,
            string sessionId,
            string agentTemplateId,
            IReadOnlyList<ChatMessage> messages,
            IReadOnlyList<LlmToolDefinition>? tools = null,
            LlmConfig? llmConfig = null,
            CancellationToken ct = default)
            => throw new NotSupportedException("Buffered path only in these tests.");
    }

    private sealed class RecordingToolInvocationService : IToolInvocationService
    {
        public List<ToolInvocationRequest> Requests { get; } = [];

        public Task<ToolInvocationResult> InvokeAsync(ToolInvocationRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            return Task.FromResult(new ToolInvocationResult
            {
                Success = true,
                ToolCallId = request.ToolCallId,
                ToolName = request.ToolName,
            });
        }
    }

    private sealed class FakeMemoryEngine : IMemoryEngine
    {
        public string? BuildMemoryContext(
            string sessionId, string? workspaceId, string? agentId, string? parentSessionId = null)
            => null;

        public Task<string?> RecallWithIntentAsync(
            string userMessage, string workspaceId, string agentId,
            string? sessionId = null, int maxTokens = 2000, CancellationToken ct = default)
            => Task.FromResult<string?>(null);

        public void WriteBack(
            string llmReply, string sessionId, string? workspaceId, string source,
            string? agentId = null, string? parentSessionId = null) { }

        public void ClearSession(string sessionId) { }
    }

    private sealed class FakeExecutionEnvironmentProvider : IExecutionEnvironmentProvider
    {
        public string OsDescription => "TestOS";
        public string OsArchitecture => "X64";
        public string RuntimeVersion => "10.0";
        public string AppBaseDirectory => "E:\\app";
        public string PathSeparator => "\\";
        public bool IsContainer => false;
        public string DefaultShell => "powershell";
        public string EnvironmentFingerprint => "test-env";
        public string? GetWorkspaceRoot(string workspaceId) => $"E:\\workspaces\\{workspaceId}";
    }

    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class FakeAgentRuntimeProfileResolver(AgentRuntimeProfile profile) : IAgentRuntimeProfileResolver
    {
        public Task<AgentRuntimeProfile> ResolveAsync(
            string workspaceId, string agentId, CancellationToken ct = default)
            => Task.FromResult(profile);
    }
}
