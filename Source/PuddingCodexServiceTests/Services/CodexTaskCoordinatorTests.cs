using Microsoft.Extensions.Logging.Abstractions;
using PuddingCodexService;
using PuddingCodexService.Models;
using PuddingCodexService.Services;

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
            model: null,
            sandbox: "workspace-write",
            approvalPolicy: "never");

        await fixture.Executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var running = await fixture.Store.GetAsync(accepted.TaskId);
        Assert.AreEqual(CodexTaskStatus.Running, running?.Status);

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
            null,
            "read-only",
            "never");
        await fixture.Executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Executor.Complete(new CodexExecutionResult("thread-1", "{}", false));
        await WaitForStatusAsync(fixture.Store, first.TaskId, CodexTaskStatus.Completed);

        fixture.Executor.Reset();
        var reply = await fixture.Coordinator.ReplyAsync(first.TaskId, "continue");
        var executedReply = await fixture.Executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(first.TaskId, executedReply.ParentTaskId);
        Assert.AreEqual("thread-1", executedReply.ThreadId);
        fixture.Executor.Complete(new CodexExecutionResult("thread-1", "{}", false));
        await WaitForStatusAsync(fixture.Store, reply.TaskId, CodexTaskStatus.Completed);
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

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(
            string root,
            string repositoryRoot,
            CodexServiceOptions options,
            FileCodexTaskStore store,
            ControlledExecutor executor,
            CodexTaskCoordinator coordinator)
        {
            Root = root;
            RepositoryRoot = repositoryRoot;
            Options = options;
            Store = store;
            Executor = executor;
            Coordinator = coordinator;
        }

        public string Root { get; }
        public string RepositoryRoot { get; }
        public CodexServiceOptions Options { get; }
        public FileCodexTaskStore Store { get; }
        public ControlledExecutor Executor { get; }
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
            var coordinator = new CodexTaskCoordinator(
                options,
                store,
                executor,
                NullLogger<CodexTaskCoordinator>.Instance);
            return Task.FromResult(new Fixture(root, repositoryRoot, options, store, executor, coordinator));
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
