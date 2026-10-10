using Pudding.Contracts.Desktop;
using PuddingBrowser.Abstractions;
using PuddingCode.Runtime;
using PuddingCode.Tools;
using PuddingCode.Tools.Definitions;

namespace PuddingBrowser.AgentTools.Tests;

/// <summary>
/// §3.1 身份准入（下一阶段实施方案）：**工具执行前必须有可信调用者身份**。
/// 身份来自执行上下文（不是参数/模型自报）；缺身份 ⇒ 拒绝且**不产生副作用**；作用域在成功/异常/并发下都不串用。
/// </summary>
public sealed class BrowserCallerIdentityAdmissionTests
{
    [Fact]
    public async Task ABrowserToolWithoutATrustedIdentityIsRefusedAndDoesNotRun()
    {
        var runtime = new RecordingRuntime();
        var tool = new IdentityProbeTool(runtime);

        var result = await tool.ExecuteAsync(Request());

        Assert.False(result.Success, "缺身份时必须拒绝");
        Assert.Contains("refused", result.Error ?? string.Empty);
        Assert.Contains("execution identity", result.Error ?? string.Empty);
        Assert.Equal(0, runtime.Calls); // 被拒绝的调用不得触碰驱动
    }

    [Fact]
    public async Task ASubAgentWithoutItsParentRunIsRefused()
    {
        // §3.2：子代理只能取父授权的子集 ⇒ 没有父运行就拒绝（不放过，也不靠"有没有父 RunId"猜）。
        var runtime = new RecordingRuntime();
        var tool = new IdentityProbeTool(runtime);

        var result = await tool.ExecuteAsync(Request(Identity(RuntimeExecutionKind.SubAgent, "run-sub")));

        Assert.False(result.Success);
        Assert.Contains("parent task", result.Error ?? string.Empty);
        Assert.Equal(0, runtime.Calls);
    }

    [Fact]
    public async Task TheProjectedIdentityIsVisibleToConsumersDuringTheCallAndClearedAfterwards()
    {
        var runtime = new RecordingRuntime();
        var tool = new IdentityProbeTool(runtime);

        var result = await tool.ExecuteAsync(Request(Identity(RuntimeExecutionKind.ConversationTurn, "run-1")));

        Assert.True(result.Success, result.Error);
        Assert.Equal("run-1", runtime.ObservedRunId);
        Assert.Equal("agent-1", runtime.ObservedCallerId);
        Assert.Equal(BrowserCallerKind.Agent, runtime.ObservedKind);

        // 调用结束后不得残留身份（否则后续调用会"借用"上一次的身份）。
        Assert.Null(BrowserCallerIdentityScope.Current);
        Assert.Null(BrowserCallerIdentityScope.CurrentFailureReason);
    }

    [Fact]
    public async Task TheIdentityScopeIsRestoredEvenWhenTheToolThrows()
    {
        var tool = new IdentityProbeTool(new RecordingRuntime(), throwInBody: true);

        var result = await tool.ExecuteAsync(Request(Identity(RuntimeExecutionKind.ConversationTurn, "run-throw")));

        Assert.False(result.Success);
        Assert.Null(BrowserCallerIdentityScope.Current); // 异常路径也必须还原作用域
    }

    [Fact]
    public async Task ConcurrentCallsDoNotShareIdentity()
    {
        // 两个并发调用（不同 run）各自的身份不能互相看见 —— 作用域是调用级的（AsyncLocal），不落单例字段。
        var runtimeA = new RecordingRuntime();
        var runtimeB = new RecordingRuntime();
        var gate = new TaskCompletionSource();
        var bothStarted = 0;

        async Task<ToolExecutionResult> RunAsync(RecordingRuntime runtime, string runId)
        {
            var tool = new IdentityProbeTool(runtime);
            runtime.OnObserve = async () =>
            {
                if (Interlocked.Increment(ref bothStarted) == 2)
                {
                    gate.TrySetResult();
                }

                // 异步等待（**不**阻塞线程）：两个调用都进入主体后才继续，制造真正的重叠。
                await gate.Task.WaitAsync(TimeSpan.FromSeconds(10));
            };
            return await tool.ExecuteAsync(Request(Identity(RuntimeExecutionKind.ConversationTurn, runId), runId + "-agent"));
        }

        var results = await Task.WhenAll(RunAsync(runtimeA, "run-a"), RunAsync(runtimeB, "run-b"));

        Assert.True(results[0].Success, results[0].Error);
        Assert.True(results[1].Success, results[1].Error);
        Assert.Equal("run-a", runtimeA.ObservedRunId);
        Assert.Equal("run-b", runtimeB.ObservedRunId);
        Assert.Equal("run-a-agent", runtimeA.ObservedCallerId);
        Assert.Equal("run-b-agent", runtimeB.ObservedCallerId);
    }

    [Fact]
    public void TheAccessorReadsTheCallScopeWithoutCachingIt()
    {
        // Host 侧消费者用 IBrowserCallerIdentityAccessor 读"本次调用"的身份；它本身不保存状态。
        IBrowserCallerIdentityAccessor accessor = new BrowserCallerIdentityAccessor();
        Assert.Null(accessor.Current);

        using (BrowserCallerIdentityScope.Push(
            new BrowserCallerIdentity(BrowserCallerKind.Agent, "agent-1", "run-1"),
            null))
        {
            Assert.Equal("run-1", accessor.Current!.TaskId);
        }

        Assert.Null(accessor.Current); // 作用域退出后访问器不得再看到身份
    }

    private static RuntimeExecutionIdentity Identity(
        RuntimeExecutionKind kind,
        string runId,
        string? parentRunId = null) => new()
        {
            Kind = kind,
            ConversationId = "conv-1",
            RunId = runId,
            ParentRunId = parentRunId,
            TraceId = null,
        };

    private static ToolExecutionRequest Request(
        RuntimeExecutionIdentity? identity = null,
        string agentInstanceId = "agent-1") => new()
        {
            ToolCallId = Guid.NewGuid().ToString("N"),
            ArgumentsJson = "{}",
            Context = new ToolExecutionContext
            {
                WorkspaceId = "default",
                SessionId = "session-1",
                AgentInstanceId = agentInstanceId,
                ExecutionIdentity = identity,
            },
        };

    private sealed record IdentityProbeArgs
    {
        public string? Dummy { get; init; }
    }

    [Tool(
        id: "test_identity_probe",
        name: "Test Identity Probe",
        description: "Captures the caller identity visible during execution.",
        category: ToolCategory.General)]
    private sealed class IdentityProbeTool(RecordingRuntime runtime, bool throwInBody = false)
        : BrowserAgentToolBase<IdentityProbeArgs>(new BrowserOperationOriginAccessor())
    {
        protected override async Task<ToolExecutionResult> ExecuteBrowserCoreAsync(
            IdentityProbeArgs args,
            ToolExecutionContext context,
            CancellationToken ct)
        {
            runtime.Record(BrowserCallerIdentityScope.Current);
            if (runtime.OnObserve is { } observe)
            {
                await observe();
            }

            if (throwInBody)
            {
                throw new InvalidOperationException("boom");
            }

            return ToolExecutionResult.Ok("{}");
        }
    }

    private sealed class RecordingRuntime
    {
        private int _calls;

        public int Calls => _calls;

        public string? ObservedRunId { get; private set; }

        public string? ObservedCallerId { get; private set; }

        public BrowserCallerKind? ObservedKind { get; private set; }

        public Func<Task>? OnObserve { get; set; }

        public void Record(BrowserCallerIdentity? identity)
        {
            Interlocked.Increment(ref _calls);
            ObservedRunId = identity?.TaskId;
            ObservedCallerId = identity?.CallerId;
            ObservedKind = identity?.Kind;
        }
    }
}
