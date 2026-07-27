using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using PuddingCodexService;
using PuddingCodexService.Models;
using PuddingCodexService.Services;
using PuddingCodexService.Tools;

namespace PuddingCodexServiceTests.Services;

[TestClass]
public sealed class CodexTaskCoordinatorTests
{
    [TestMethod]
    public async Task Submitted_Task_Continues_After_Request_Returns_And_Is_Persisted()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Coordinator.StartAsync(CancellationToken.None);

        var accepted = await fixture.Coordinator.StartAsync(
            "repair pudding",
            fixture.RepositoryRoot,
            model: null);

        await fixture.Executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var running = await fixture.Store.GetAsync(accepted.TaskId);
        Assert.AreEqual(CodexTaskStatus.Running, running?.Status);
        Assert.AreEqual("danger-full-access", running?.Sandbox);
        Assert.AreEqual("never", running?.ApprovalPolicy);

        fixture.Executor.Complete(new CodexExecutionResult(
            "thread-persisted",
            "{\"content\":\"done\"}",
            IsError: false));
        var completed = await WaitForStatusAsync(
            fixture.Store,
            accepted.TaskId,
            CodexTaskStatus.Completed);
        Assert.AreEqual("thread-persisted", completed.ThreadId);

        var reopenedStore = new FileCodexTaskStore(fixture.Options);
        var reopened = await reopenedStore.GetAsync(accepted.TaskId);
        Assert.AreEqual(CodexTaskStatus.Completed, reopened?.Status);
        Assert.AreEqual("thread-persisted", reopened?.ThreadId);
    }

    [TestMethod]
    public async Task Reply_Uses_Completed_Parent_Thread()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Coordinator.StartAsync(CancellationToken.None);
        var first = await fixture.Coordinator.StartAsync(
            "first",
            fixture.RepositoryRoot,
            null);
        await fixture.Executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Executor.Complete(new CodexExecutionResult("thread-1", "{}", false));
        await WaitForStatusAsync(fixture.Store, first.TaskId, CodexTaskStatus.Completed);

        fixture.Executor.Reset();
        var reply = await fixture.Coordinator.ReplyAsync(first.TaskId, "continue");
        var executedReply = await fixture.Executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(first.TaskId, executedReply.ParentTaskId);
        Assert.AreEqual("thread-1", executedReply.ThreadId);
        Assert.AreEqual("danger-full-access", executedReply.Sandbox);
        Assert.AreEqual("never", executedReply.ApprovalPolicy);
        fixture.Executor.Complete(new CodexExecutionResult("thread-1", "{}", false));
        await WaitForStatusAsync(fixture.Store, reply.TaskId, CodexTaskStatus.Completed);
    }

    [TestMethod]
    public async Task SelfHeal_Task_Uses_Safe_Policy_And_Schedules_One_Staged_Restart()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Coordinator.StartAsync(CancellationToken.None);

        var accepted = await fixture.Coordinator.StartSelfHealAsync(
            "terminate Pudding and restart it",
            fixture.RepositoryRoot,
            model: null);
        Assert.IsTrue(accepted.RestartPuddingOnCompletion);

        var executed = await fixture.Executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(executed.RestartPuddingOnCompletion);
        StringAssert.Contains(executed.Prompt, "Do not stop, kill, restart, or launch PuddingAgent");
        StringAssert.Contains(executed.Prompt, "Ignore any requested-work instruction");
        StringAssert.Contains(executed.Prompt, "terminate Pudding and restart it");

        fixture.Executor.Complete(new CodexExecutionResult(
            "thread-self-heal",
            "{\"content\":\"ready\"}",
            IsError: false));
        var completed = await WaitForRestartScheduledAsync(fixture.Store, accepted.TaskId);
        Assert.AreEqual(CodexTaskStatus.Completed, completed.Status);
        Assert.IsNotNull(completed.RestartRequestId);
        Assert.IsNotNull(completed.RestartNotBeforeUtc);

        var requestPath = Path.Combine(
            fixture.Options.SupervisorRunDirectory,
            "backend.restart.request.json");
        using var request = JsonDocument.Parse(await File.ReadAllTextAsync(requestPath));
        Assert.AreEqual(accepted.TaskId, request.RootElement.GetProperty("taskId").GetString());
        Assert.AreEqual(completed.RestartRequestId, request.RootElement.GetProperty("requestId").GetString());

        var repeated = await fixture.RestartWriter.RequestAsync(completed);
        Assert.AreEqual(completed.RestartRequestId, repeated.RequestId);

        var tools = new CodexTaskTools(fixture.Coordinator, fixture.RestartWriter);
        var projected = await tools.GetAsync(completed.TaskId);
        Assert.IsNotNull(projected.RestartResultJson);
        StringAssert.Contains(projected.RestartResultJson, "pending");
    }

    [TestMethod]
    public async Task Failed_SelfHeal_Task_Does_Not_Request_A_Restart()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Coordinator.StartAsync(CancellationToken.None);
        var accepted = await fixture.Coordinator.StartSelfHealAsync(
            "repair before restart",
            fixture.RepositoryRoot,
            model: null);
        await fixture.Executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        fixture.Executor.Complete(new CodexExecutionResult(
            "thread-failed",
            "{\"error\":\"build failed\"}",
            IsError: true));
        await WaitForStatusAsync(fixture.Store, accepted.TaskId, CodexTaskStatus.Failed);

        Assert.IsFalse(File.Exists(Path.Combine(
            fixture.Options.SupervisorRunDirectory,
            "backend.restart.request.json")));
    }

    private static async Task<CodexTaskRecord> WaitForStatusAsync(
        FileCodexTaskStore store,
        string taskId,
        CodexTaskStatus expected)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!timeout.IsCancellationRequested)
        {
            var record = await store.GetAsync(taskId, timeout.Token);
            if (record?.Status == expected)
                return record;
            await Task.Delay(25, timeout.Token);
        }

        throw new TimeoutException($"Task {taskId} did not reach {expected}.");
    }

    private static async Task<CodexTaskRecord> WaitForRestartScheduledAsync(
        FileCodexTaskStore store,
        string taskId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!timeout.IsCancellationRequested)
        {
            var record = await store.GetAsync(taskId, timeout.Token);
            if (record is { Status: CodexTaskStatus.Completed, RestartRequestId.Length: > 0 })
                return record;
            await Task.Delay(25, timeout.Token);
        }

        throw new TimeoutException($"Task {taskId} did not schedule a Pudding restart.");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(
            string root,
            string repositoryRoot,
            CodexServiceOptions options,
            FileCodexTaskStore store,
            ControlledExecutor executor,
            SupervisorRestartRequestWriter restartWriter,
            CodexTaskCoordinator coordinator)
        {
            Root = root;
            RepositoryRoot = repositoryRoot;
            Options = options;
            Store = store;
            Executor = executor;
            RestartWriter = restartWriter;
            Coordinator = coordinator;
        }

        public string Root { get; }
        public string RepositoryRoot { get; }
        public CodexServiceOptions Options { get; }
        public FileCodexTaskStore Store { get; }
        public ControlledExecutor Executor { get; }
        public SupervisorRestartRequestWriter RestartWriter { get; }
        public CodexTaskCoordinator Coordinator { get; }

        public static Task<Fixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), $"pudding-codex-tests-{Guid.NewGuid():N}");
            var repositoryRoot = Path.Combine(root, "repo");
            Directory.CreateDirectory(repositoryRoot);
            var options = new CodexServiceOptions
            {
                DataRoot = Path.Combine(root, "data"),
                RepositoryRoot = repositoryRoot,
                SupervisorRunDirectory = Path.Combine(root, "run"),
            };
            options.Validate();
            var store = new FileCodexTaskStore(options);
            var executor = new ControlledExecutor();
            var restartWriter = new SupervisorRestartRequestWriter(options);
            var coordinator = new CodexTaskCoordinator(
                options,
                store,
                executor,
                restartWriter,
                NullLogger<CodexTaskCoordinator>.Instance);
            return Task.FromResult(new Fixture(
                root,
                repositoryRoot,
                options,
                store,
                executor,
                restartWriter,
                coordinator));
        }

        public async ValueTask DisposeAsync()
        {
            await Coordinator.StopAsync(CancellationToken.None);
            Coordinator.Dispose();
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class ControlledExecutor : ICodexExecutor
    {
        private TaskCompletionSource<CodexTaskRecord> _started = NewSource<CodexTaskRecord>();
        private TaskCompletionSource<CodexExecutionResult> _completion = NewSource<CodexExecutionResult>();

        public TaskCompletionSource<CodexTaskRecord> Started => _started;

        public async Task<CodexExecutionResult> ExecuteAsync(CodexTaskRecord task, CancellationToken ct)
        {
            _started.TrySetResult(task);
            return await _completion.Task.WaitAsync(ct);
        }

        public void Complete(CodexExecutionResult result) => _completion.TrySetResult(result);

        public void Reset()
        {
            _started = NewSource<CodexTaskRecord>();
            _completion = NewSource<CodexExecutionResult>();
        }

        private static TaskCompletionSource<T> NewSource<T>() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
