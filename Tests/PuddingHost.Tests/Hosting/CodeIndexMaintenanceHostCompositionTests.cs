using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using PuddingCode.Configuration;
using PuddingCodeIndex.Contracts;
using PuddingHost.Hosting;

namespace PuddingHost.Tests.Hosting;

/// <summary>
/// U3-B2a — host composition tests for the code-index maintenance driver (closes the P0 "enqueue with nobody
/// pumping" defect).
/// <para>
/// These assertions exist because the repository has a real accident in its history: registering
/// <c>TryAddSingleton&lt;IFace, Impl&gt;()</c> whose implementation constructor parameter was not registered
/// makes <c>ValidateOnBuild</c> fail at <b>Build</b> time (it took down six service descriptors and Core did
/// not start). Building the product composition root runs that validation, so a broken registration fails
/// here instead of in production.
/// </para>
/// <para>
/// Runs in the same xUnit collection as <see cref="PuddingApplicationHostCompositionTests"/>: these tests
/// reconfigure the process-global Serilog logger through <c>PuddingLoggingBootstrapper</c>, so they must not
/// run in parallel with each other.
/// </para>
/// </summary>
[Collection("Pudding application host composition")]
public sealed class CodeIndexMaintenanceHostCompositionTests
{
    private const string WorkspaceId = "default";

    /// <summary>
    /// A2 + A3: the maintenance driver resolves from the host container, the pump port is the *same* instance
    /// as the index scheduler, exactly one lifecycle driver is registered, and that driver holds the very
    /// instance. Also: with nothing registered the startup attachment is a safe no-op, and stop is bounded.
    /// </summary>
    [Fact]
    public async Task Host_Composition_Registers_The_Index_Maintenance_Driver()
    {
        var dataRoot = NewDataRoot();
        try
        {
            await using var app = BuildHost(dataRoot);

            // A2 — the maintenance driver is resolvable from the host container.
            var maintenance = app.Services.GetRequiredService<ICodeIndexMaintenance>();
            Assert.NotNull(maintenance);
            Assert.IsType<PuddingCodeIndex.Services.CodeIndex.CodeIndexMaintenanceService>(maintenance);

            // The pump port must not be a second scheduler: two schedulers would be two writers over one index.
            var scheduler = app.Services.GetRequiredService<ICodeIndexScheduler>();
            var pump = app.Services.GetRequiredService<ICodeIndexSchedulerDriver>();
            Assert.Same(scheduler, pump);

            // A3 — the lifecycle driver is registered, once, and is wired to that same component instance.
            var drivers = app.Services.GetServices<IHostedService>()
                .OfType<CodeIndexMaintenanceHostedService>()
                .ToArray();
            Assert.Single(drivers);

            var driver = drivers[0];
            var maintenanceField = typeof(CodeIndexMaintenanceHostedService).GetField(
                "_maintenance", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(maintenanceField);
            Assert.Same(maintenance, maintenanceField.GetValue(driver));

            // Start is non-blocking and, with no registered scope, attachment is a safe no-op.
            using var startCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await driver.StartAsync(startCts.Token);
            Assert.True(maintenance.IsRunning);

            Assert.True(
                await WaitUntilAsync(() => driver.ScopeAttachmentCompleted, TimeSpan.FromSeconds(30)),
                "the startup attachment must complete (it runs off the startup path)");
            Assert.Equal(0, driver.AttachedScopeCount);
            Assert.Empty(maintenance.GetScopeStatuses());

            // Stop is bounded and reaches the component driver.
            using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            await driver.StopAsync(stopCts.Token);
            stopwatch.Stop();

            Assert.False(maintenance.IsRunning);
            Assert.True(
                stopwatch.Elapsed < CodeIndexMaintenanceHostedService.DefaultStopTimeout,
                $"stop must be bounded, took {stopwatch.Elapsed}");
        }
        finally
        {
            Cleanup(dataRoot);
        }
    }

    /// <summary>
    /// A4 — closure: the production acceptor path (register a project, then <c>Enqueue</c> exactly the way
    /// <c>code_index_register_project</c> does) must reach <see cref="ICodeIndexer"/>. Nothing else drives the
    /// queue, so this can only pass when the host-level driver actually pumps it.
    /// </summary>
    [Fact]
    public async Task Enqueue_Is_Pumped_By_The_Host_Driver_And_Reaches_The_Indexer()
    {
        var dataRoot = NewDataRoot();
        var projectRoot = NewProjectRoot();
        var indexer = new CountingCodeIndexer();

        try
        {
            await using var app = BuildHost(dataRoot, services =>
            {
                // Test double for the real (Roslyn) indexer: this suite asserts *closure*, not parsing.
                services.RemoveAll<ICodeIndexer>();
                services.AddSingleton<ICodeIndexer>(indexer);
            });

            Assert.Same(indexer, app.Services.GetRequiredService<ICodeIndexer>());

            var driver = app.Services.GetServices<IHostedService>()
                .OfType<CodeIndexMaintenanceHostedService>()
                .Single();

            using var startCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await driver.StartAsync(startCts.Token);

            // The same two calls the production tool makes: register the directory, then enqueue the scope.
            var registry = app.Services.GetRequiredService<ICodeProjectRegistry>();
            var registered = await registry.AddProjectAsync(
                new CodeProjectAddRequest(WorkspaceId, projectRoot),
                CancellationToken.None);
            Assert.True(registered.Success, registered.Message);

            var scheduler = app.Services.GetRequiredService<ICodeIndexScheduler>();
            scheduler.Enqueue(registered.WorkspaceId!, registered.ProjectId!);

            Assert.True(
                await WaitUntilAsync(() => indexer.CallCount >= 1, TimeSpan.FromSeconds(30)),
                "Enqueue must be pumped by the host driver and reach the indexer "
                + $"(scheduler queue depth: {scheduler.GetQueueDepth(WorkspaceId)})");

            // The indexer must have been called for the registered directory, not for something else.
            Assert.True(
                PathEquals(projectRoot, indexer.CalledProjectPaths[0]),
                $"expected {projectRoot}, called {indexer.CalledProjectPaths[0]}");

            // The water mark must have advanced: the pump ran one real run, it did not merely dequeue.
            var progress = app.Services.GetRequiredService<ICodeIndexSchedulerDriver>()
                .GetProgress(registered.WorkspaceId!, registered.ProjectId!);
            Assert.True(progress.CommittedVersion >= 1, $"committed={progress.CommittedVersion}");
            Assert.False(progress.Pending);

            using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await driver.StopAsync(stopCts.Token);
        }
        finally
        {
            Cleanup(dataRoot);
            Cleanup(projectRoot);
        }
    }

    /// <summary>
    /// Lifecycle driver, part 2: scopes that are already registered must get a change source. This is the
    /// difference between "the queue is drained" and "the index stays fresh when files change".
    /// </summary>
    [Fact]
    public async Task StartAsync_Attaches_Already_Registered_Scopes()
    {
        var dataRoot = NewDataRoot();
        var projectRoot = NewProjectRoot();
        const string scopeId = "scope-u3b2a-attach";

        try
        {
            // A workspace id is the directory name below the data root's workspaces folder.
            Directory.CreateDirectory(Path.Combine(dataRoot, "workspaces", WorkspaceId));

            await using var app = BuildHost(dataRoot, services =>
            {
                services.RemoveAll<ICodeIndexer>();
                services.AddSingleton<ICodeIndexer>(new CountingCodeIndexer());
            });

            var store = app.Services.GetRequiredService<ICodeIndexStore>();
            await store.UpsertProjectAsync(
                new CodeProjectRecord(WorkspaceId, scopeId, projectRoot, CodeProjectStatus.Active),
                CancellationToken.None);

            var maintenance = app.Services.GetRequiredService<ICodeIndexMaintenance>();
            var driver = app.Services.GetServices<IHostedService>()
                .OfType<CodeIndexMaintenanceHostedService>()
                .Single();

            using var startCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await driver.StartAsync(startCts.Token);

            Assert.True(
                await WaitUntilAsync(
                    () => driver.ScopeAttachmentCompleted && driver.AttachedScopeCount >= 1,
                    TimeSpan.FromSeconds(30)),
                $"a registered scope must be attached (attached={driver.AttachedScopeCount})");

            var status = maintenance.GetScopeStatus(WorkspaceId, scopeId);
            Assert.NotNull(status);
            Assert.True(
                PathEquals(projectRoot, status!.RootPath),
                $"expected root {projectRoot}, got {status.RootPath}");
            Assert.True(status.WatcherAttached, "the attached scope must have a live change source");

            using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await driver.StopAsync(stopCts.Token);
            Assert.False(maintenance.IsRunning);
        }
        finally
        {
            Cleanup(dataRoot);
            Cleanup(projectRoot);
        }
    }

    private static WebApplication BuildHost(string dataRoot, Action<IServiceCollection>? configure = null)
    {
        var options = PuddingHostOptionsFactory.ForDesktopChild(
        [
            "--desktop-child",
            "--desktop-parent-pid", Environment.ProcessId.ToString(),
            "--data-root", dataRoot,
            "--urls", "http://0.0.0.0:18080",
        ]);

        var builder = PuddingApplicationHost.CreateBuilder([], options);
        configure?.Invoke(builder.Services);

        // ValidateOnBuild is enabled inside CreateBuilder: a service whose constructor dependencies cannot be
        // resolved fails here, at Build time, exactly like the accident this suite guards against.
        return PuddingApplicationHost.Build(builder);
    }

    private static bool PathEquals(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private static string NewDataRoot() =>
        Path.Combine(Path.GetTempPath(), "PuddingAgent", $"code-index-host-{Guid.NewGuid():N}");

    private static string NewProjectRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "PuddingAgent", $"code-index-project-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "Demo.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        return root;
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return true;

            await Task.Delay(50);
        }

        return condition();
    }

    private static void Cleanup(string directory)
    {
        Serilog.Log.CloseAndFlush();

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, recursive: true);
                return;
            }
            catch (Exception)
            {
                Thread.Sleep(100);
            }
        }
    }

    /// <summary>Indexer double: records every call so closure can be asserted by counting.</summary>
    private sealed class CountingCodeIndexer : ICodeIndexer
    {
        private readonly object _gate = new();
        private readonly List<string> _projectPaths = [];

        public int CallCount
        {
            get
            {
                lock (_gate)
                    return _projectPaths.Count;
            }
        }

        public IReadOnlyList<string> CalledProjectPaths
        {
            get
            {
                lock (_gate)
                    return _projectPaths.ToArray();
            }
        }

        public Task<CodeIndexResult> IndexWorkspaceAsync(
            CodeWorkspaceDescriptor workspace,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
                _projectPaths.Add(workspace.ProjectPath);

            return Task.FromResult(new CodeIndexResult(
                true,
                CodeIndexStatus.Completed,
                "host composition double",
                WorkspaceId: workspace.WorkspaceId,
                ProjectId: workspace.ProjectId));
        }

        public Task<CodeIndexResult> RemoveWorkspaceIndexAsync(
            string workspaceId,
            string projectId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new CodeIndexResult(
                true,
                CodeIndexStatus.Completed,
                "host composition double",
                WorkspaceId: workspaceId,
                ProjectId: projectId));
    }
}
