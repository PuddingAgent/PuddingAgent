using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingRuntime.Services;
using PuddingRuntime.Services.AgentLoop;
using PuddingRuntime.Services.Skills;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Tools;

/// <summary>
/// 前端改进 #1：展示投影（presentation）在**事件发射侧**的接线与 fail-open 证明。
/// <para>
/// 契约：<c>tool_call</c> / <c>tool_result</c> 帧 payload 必须携带该工具自己声明的 presentation
/// （<c>{"kind":...,"meta":...}</c>，前端从 <c>data.payload</c> 展开读取）；
/// 无声明 / 投影抛异常 ⇒ 降级 <c>generic</c>，且**事件仍产出、工具调用仍执行**。
/// </para>
/// </summary>
[TestClass]
public sealed class ToolCallPresentationEmissionTests
{
    private const string TerminalStartArgs =
        """{"command":"dotnet build Source/PuddingRuntime/PuddingRuntime.csproj","cwd":"E:\\repo"}""";

    private const string TerminalResultJson =
        """{"job":{"job_id":"j-1","process_id":"j-1","session_id":"s-1","command":"dotnet build Source/PuddingRuntime/PuddingRuntime.csproj","cwd":"E:\\repo","status":"Exited","exit_code":0},"output":null,"next_action":"done"}""";

    [TestMethod]
    public async Task ToolCallAndResultFrames_CarryToolOwnedTerminalPresentation()
    {
        var tools = new StubToolInvocationService(TerminalResultJson);
        var service = CreateService(
            new ToolCallingLlmClient(
                new LlmResponse(null, [new ToolCall("call-1", "terminal_start", TerminalStartArgs)]),
                new LlmResponse("finished", null)),
            tools);

        var frames = await DrainAsync(service.ExecuteStreamAsync(
            CreateDispatchRequest("session-presentation-terminal")));

        var call = FindFrame(frames, SseEventTypes.ToolCall);
        Assert.AreEqual("terminal", PresentationKind(call), call.Data);
        Assert.AreEqual(
            "dotnet build Source/PuddingRuntime/PuddingRuntime.csproj",
            PresentationMetaString(call, "command"),
            call.Data);

        var result = FindFrame(frames, SseEventTypes.ToolResult);
        Assert.AreEqual("terminal", PresentationKind(result), result.Data);
        Assert.AreEqual("j-1", PresentationMetaString(result, "job_id"), result.Data);
        Assert.AreEqual(0, PresentationMetaInt(result, "exit_code"), result.Data);

        Assert.AreEqual(1, tools.Requests.Count, "展示投影不得改变工具执行次数。");
    }

    [TestMethod]
    public async Task ToolWithoutPresenter_FallsBackToGeneric_AndStillExecutes()
    {
        var tools = new StubToolInvocationService("listed");
        var service = CreateService(
            new ToolCallingLlmClient(
                new LlmResponse(null, [new ToolCall("call-2", "list_dir", """{"path":"."}""")]),
                new LlmResponse("finished", null)),
            tools);

        var frames = await DrainAsync(service.ExecuteStreamAsync(
            CreateDispatchRequest("session-presentation-generic")));

        Assert.AreEqual("generic", PresentationKind(FindFrame(frames, SseEventTypes.ToolCall)));
        Assert.AreEqual("generic", PresentationKind(FindFrame(frames, SseEventTypes.ToolResult)));
        Assert.AreEqual(1, tools.Requests.Count);
    }

    [TestMethod]
    public async Task ProjectorThrowing_FailsOpen_EventStillEmittedAndToolStillRuns()
    {
        var tools = new StubToolInvocationService("output");
        var service = CreateService(
            new ToolCallingLlmClient(
                new LlmResponse(null, [new ToolCall("call-3", "terminal_start", TerminalStartArgs)]),
                new LlmResponse("finished", null)),
            tools,
            new ThrowingProjector());

        var frames = await DrainAsync(service.ExecuteStreamAsync(
            CreateDispatchRequest("session-presentation-fail-open")));

        Assert.AreEqual(
            "generic",
            PresentationKind(FindFrame(frames, SseEventTypes.ToolCall)),
            "投射器抛异常必须降级 generic，且事件仍产出（fail-open）。");
        Assert.AreEqual("generic", PresentationKind(FindFrame(frames, SseEventTypes.ToolResult)));
        Assert.AreEqual(1, tools.Requests.Count, "展示失败绝不允许阻断工具调用。");
    }

    // ── 帧读取助手 ──────────────────────────────────────────────────────────
    private static ServerSentEventFrame FindFrame(
        IReadOnlyList<ServerSentEventFrame> frames,
        string eventName)
    {
        var frame = frames.FirstOrDefault(f => f.Event == eventName);
        Assert.IsNotNull(frame, $"{eventName} 帧必须存在；实际帧：{string.Join(",", frames.Select(f => f.Event))}");
        return frame!;
    }

    private static JsonElement Presentation(ServerSentEventFrame frame)
    {
        using var document = JsonDocument.Parse(frame.Data);
        Assert.IsTrue(
            document.RootElement.TryGetProperty("presentation", out var presentation),
            $"payload 必须带 presentation：{frame.Data}");
        return presentation.Clone();
    }

    private static string PresentationKind(ServerSentEventFrame frame)
    {
        var presentation = Presentation(frame);
        Assert.AreEqual(JsonValueKind.Object, presentation.ValueKind);
        return presentation.GetProperty("kind").GetString()!;
    }

    private static string? PresentationMetaString(ServerSentEventFrame frame, string key)
        => PresentationMeta(frame, key) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    private static int? PresentationMetaInt(ServerSentEventFrame frame, string key)
        => PresentationMeta(frame, key) is { ValueKind: JsonValueKind.Number } value ? value.GetInt32() : null;

    private static JsonElement? PresentationMeta(ServerSentEventFrame frame, string key)
    {
        var presentation = Presentation(frame);
        return presentation.TryGetProperty("meta", out var meta)
               && meta.ValueKind == JsonValueKind.Object
               && meta.TryGetProperty(key, out var value)
            ? value
            : null;
    }

    // ── Fixture ────────────────────────────────────────────────────────────
    private static async Task<List<ServerSentEventFrame>> DrainAsync(
        IAsyncEnumerable<ServerSentEventFrame> frames)
    {
        var collected = new List<ServerSentEventFrame>();
        await foreach (var frame in frames)
            collected.Add(frame);
        return collected;
    }

    private static AgentExecutionService CreateService(
        IRuntimeLlmClient llm,
        IToolInvocationService tools,
        IToolPresentationProjector? projector = null)
    {
        var sessionManager = new AgentSessionManager(NullLogger<AgentSessionManager>.Instance);
        var runtimeSessionStore = new InMemoryRuntimeSessionStore();
        var memory = new FakeMemoryEngine();
        var sandbox = new SandboxExecutor(NullLogger<SandboxExecutor>.Instance);
        var guardrails = new AgentExecutionGuardrails();
        var controlRegistry = new ExecutionControlRegistry();
        var journal = new ExecutionJournal();
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
            new FakeEnvironmentProvider());
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
            toolInvocationService: tools,
            toolPresentationProjector: projector);
    }

    private static RuntimeDispatchRequest CreateDispatchRequest(string sessionId) => new()
    {
        SessionId = sessionId,
        AgentTemplateId = "workspace-task-agent",
        MessageText = "run the tool",
        MessageId = $"msg-{sessionId}",
        WorkspaceId = "ws-1",
        AgentInstanceId = "agent-1",
        LlmConfig = new LlmConfig { ModelId = "test-model", MaxContextTokens = 8192 },
        SuppressContextAutoCompaction = true,
        MaxRounds = 4,
    };

    /// <summary>脚本化流式 LLM：按轮次出队；有 tool calls 时发 name/args 增量。</summary>
    private sealed class ToolCallingLlmClient(params LlmResponse[] responses) : IRuntimeLlmClient
    {
        private readonly Queue<LlmResponse> _responses = new(responses);

        public Task<LlmResponse> ChatAsync(
            string workspaceId,
            string sessionId,
            string agentTemplateId,
            IReadOnlyList<ChatMessage> messages,
            IReadOnlyList<LlmToolDefinition>? tools = null,
            LlmConfig? llmConfig = null,
            CancellationToken ct = default)
            => Task.FromResult(Dequeue());

        public async IAsyncEnumerable<StreamDelta> ChatStreamAsync(
            string workspaceId,
            string sessionId,
            string agentTemplateId,
            IReadOnlyList<ChatMessage> messages,
            IReadOnlyList<LlmToolDefinition>? tools = null,
            LlmConfig? llmConfig = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            var response = Dequeue();
            for (var i = 0; i < (response.ToolCalls?.Count ?? 0); i++)
            {
                var call = response.ToolCalls![i];
                yield return new StreamDelta
                {
                    ToolCallIndex = i,
                    ToolCallId = call.Id,
                    ToolCallNameDelta = call.Name,
                    ToolCallArgsDelta = call.ArgumentsJson,
                };
            }

            yield return new StreamDelta
            {
                ContentDelta = response.Content,
                Usage = response.Usage,
                FinishReason = response.ToolCalls?.Count > 0 ? "tool_calls" : "stop",
            };

            await Task.CompletedTask;
        }

        private LlmResponse Dequeue()
            => _responses.Count > 0
                ? _responses.Dequeue()
                : throw new InvalidOperationException("Scripted LLM exhausted: more rounds than scripted responses.");
    }

    private sealed class StubToolInvocationService(string output) : IToolInvocationService
    {
        public List<ToolInvocationRequest> Requests { get; } = [];

        public Task<ToolInvocationResult> InvokeAsync(
            ToolInvocationRequest request,
            CancellationToken ct = default)
        {
            Requests.Add(request);
            return Task.FromResult(new ToolInvocationResult
            {
                Success = true,
                ToolCallId = request.ToolCallId,
                ToolName = request.ToolName,
                Output = output,
            });
        }
    }

    private sealed class ThrowingProjector : IToolPresentationProjector
    {
        public JsonElement Project(string? toolId, string? argumentsJson, string? resultJson)
            => throw new InvalidOperationException("presentation projector blew up");
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

    private sealed class FakeEnvironmentProvider : IExecutionEnvironmentProvider
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
}
