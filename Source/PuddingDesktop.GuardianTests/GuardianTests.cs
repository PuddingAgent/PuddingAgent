using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using PuddingDesktop.Guardian;

namespace PuddingDesktop.GuardianTests;

public sealed class GuardianTests
{
    [Fact]
    public void ComponentReferencesOnlyFrameworkAssemblies()
    {
        var references = typeof(GuardianController).Assembly.GetReferencedAssemblies().Select(x => x.Name!).ToArray();
        Assert.All(references, name => Assert.True(name.StartsWith("System.") || name is "System" or "netstandard" or "Microsoft.Win32.Primitives", name));
        Assert.DoesNotContain(AppDomain.CurrentDomain.GetAssemblies(), assembly => assembly.GetName().Name is "PuddingHost" or "PuddingRuntime" or "Microsoft.CodeAnalysis" or "Microsoft.Build");
    }

    [Fact]
    public async Task HealthyCoreDeploymentRetainsOldVersionAndDoesNotReplaceShell()
    {
        using var f = new Fixture();
        await f.Execute(new());
        Assert.Equal("healthy", f.Controller.State.Phase);
        Assert.Equal("new", File.ReadAllText(f.Settings.CoreExecutablePath));
        Assert.Equal("shell", File.ReadAllText(f.Settings.DesktopExecutablePath));
        Assert.Equal("old", File.ReadAllText(Path.Combine(f.Controller.State.Fallback!.Directory, "PuddingAgent.exe")));
        Assert.Null(f.Controller.State.Pending);
        Assert.Equal(["core"], f.Runtime.Stops);
    }

    [Fact]
    public async Task FailedHealthRollsBackAndRemovesCandidateOnlyAssemblies()
    {
        using var f = new Fixture();
        File.WriteAllText(Path.Combine(f.Candidate, "PuddingAgent.exe"), "bad");
        File.WriteAllText(Path.Combine(f.Candidate, "new-only.dll"), "new dependency");
        await f.Execute(new());
        Assert.Equal("rolled-back", f.Controller.State.Phase);
        Assert.Equal("old", File.ReadAllText(f.Settings.CoreExecutablePath));
        Assert.False(File.Exists(Path.Combine(f.Settings.TargetDirectory("core"), "new-only.dll")));
        Assert.Equal(["core", "desktop"], f.Runtime.Stops);
        Assert.Null(f.Store.Load().Pending);
    }

    [Fact]
    public async Task BuildFailureLeavesProductRunningAndDoesNotCreateRollbackTransaction()
    {
        using var f = new Fixture();
        f.Runtime.BuildFailure = true;
        await f.Execute(new());
        Assert.Equal("failed", f.Controller.State.Phase);
        Assert.Empty(f.Runtime.Stops);
        Assert.Equal("old", File.ReadAllText(f.Settings.CoreExecutablePath));
        Assert.Null(f.Controller.State.Pending);
    }

    [Fact]
    public async Task BuildOnlyDoesNotStopOrDeploy()
    {
        using var f = new Fixture();
        await f.Execute(new() { Action = "build" });
        Assert.Equal("built", f.Controller.State.Phase);
        Assert.Equal(f.Candidate, f.Controller.State.PreparedDirectory);
        Assert.Empty(f.Runtime.Stops);
        Assert.Equal("old", File.ReadAllText(f.Settings.CoreExecutablePath));
    }

    [Fact]
    public async Task InterruptedDeploymentRestoresPersistedBackupBeforeStarting()
    {
        using var f = new Fixture();
        var backup = f.Store.Capture("core");
        var candidate = f.Store.Inspect(f.Candidate, "core");
        f.Store.Save(new() { Pending = new(backup, candidate), Phase = "deploying" });
        f.Store.Apply(candidate);
        var restarted = new GuardianController(f.Settings, f.Store, f.Runtime, f.Time);
        await restarted.RecoverInterruptedAsync(default);
        Assert.Equal("old", File.ReadAllText(f.Settings.CoreExecutablePath));
        Assert.Equal("rolled-back", restarted.State.Phase);
        Assert.Equal(["desktop"], f.Runtime.Stops);
    }

    [Fact]
    public async Task CorruptRollbackNeverOverwritesProductAndRetainsRecoveryEvidence()
    {
        using var f = new Fixture();
        var backup = f.Store.Capture("core");
        f.Store.Save(new() { Pending = new(backup, f.Store.Inspect(f.Candidate, "core")) });
        File.WriteAllText(Path.Combine(backup.Directory, "PuddingAgent.exe"), "tampered");
        var restarted = new GuardianController(f.Settings, f.Store, f.Runtime, f.Time);
        await restarted.RecoverInterruptedAsync(default);
        Assert.Equal("faulted", restarted.State.Phase);
        Assert.False(restarted.State.Monitoring);
        Assert.NotNull(restarted.State.Pending);
        Assert.Empty(f.Runtime.Stops);
    }

    [Fact]
    public async Task TransientReadinessDoesNotPassStableAcceptanceWindow()
    {
        using var f = new Fixture();
        var count = 0;
        f.Runtime.HealthOverride = () => File.ReadAllText(f.Settings.CoreExecutablePath) == "old" || ++count % 2 == 1;
        await f.Execute(new());
        Assert.Equal("rolled-back", f.Controller.State.Phase);
        Assert.True(count > 2);
    }

    [Fact]
    public async Task ExplicitStopDisablesAutomaticRecovery()
    {
        using var f = new Fixture();
        await f.Execute(new() { Action = "stop", Target = "desktop" });
        f.Runtime.HealthOverride = () => false;
        for (var i = 0; i < 10; i++) await f.Controller.TickAsync(default);
        Assert.Equal("stopped", f.Controller.State.Phase);
        Assert.False(f.Controller.State.Monitoring);
        Assert.Empty(f.Runtime.Starts);
    }

    [Fact]
    public async Task UserExitIsNotRestarted()
    {
        using var f = new Fixture();
        f.Runtime.IntentionalStop = true;
        await f.Controller.TickAsync(default);
        await f.Controller.TickAsync(default);
        Assert.False(f.Controller.State.Monitoring);
        Assert.Empty(f.Runtime.Starts);
    }

    [Fact]
    public async Task RepeatedRequestIsIdempotentAndConcurrentDifferentCommandIsBusy()
    {
        using var f = new Fixture();
        f.Runtime.BuildGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var command = new GuardianCommand();
        Assert.Equal("accepted", f.Controller.Submit(command, default));
        Assert.Equal("duplicate", f.Controller.Submit(command, default));
        Assert.Equal("conflict", f.Controller.Submit(command with { Target = "desktop" }, default));
        Assert.Equal("busy", f.Controller.Submit(new(), default));
        f.Runtime.BuildGate.SetResult();
        await f.Controller.RunningOperation!;
        Assert.Single(f.Runtime.Starts);
    }

    [Fact]
    public async Task RuntimeFailureUsesKnownGoodFallback()
    {
        using var f = new Fixture();
        await f.Execute(new());
        File.WriteAllText(f.Settings.CoreExecutablePath, "bad");
        f.Time.Advance(TimeSpan.FromMinutes(1));
        await f.Controller.TickAsync(default);
        Assert.Equal("rolled-back", f.Controller.State.Phase);
        Assert.Equal("old", File.ReadAllText(f.Settings.CoreExecutablePath));
    }

    [Fact]
    public async Task RepeatedCrashesStopAfterThreeRecoveriesWithinFiveMinutes()
    {
        using var f = new Fixture();
        await f.Execute(new());
        for (var attempt = 0; attempt < 3; attempt++)
        {
            f.Time.Advance(TimeSpan.FromSeconds(10));
            File.WriteAllText(f.Settings.CoreExecutablePath, "bad");
            await f.Controller.TickAsync(default);
            Assert.Equal("rolled-back", f.Controller.State.Phase);
        }
        File.WriteAllText(f.Settings.CoreExecutablePath, "bad");
        await f.Controller.TickAsync(default);
        Assert.Equal("faulted", f.Controller.State.Phase);
        Assert.False(f.Controller.State.Monitoring);
    }

    [Fact]
    public async Task FreshStartDoesNotClaimAnInterruptedCommand()
    {
        using var f = new Fixture();
        await f.Controller.RecoverInterruptedAsync(default);
        Assert.Equal("starting", f.Controller.State.Phase);
        Assert.Null(f.Controller.State.Error);
    }

    [Fact]
    public void SnapshotRetentionNeverDeletesActiveFallbackOrPendingTransaction()
    {
        using var f = new Fixture();
        var snapshots = Enumerable.Range(0, 5).Select(_ => f.Store.Capture("core")).ToArray();
        var state = new GuardianState { Fallback = snapshots[0] };
        f.Store.Prune(state with { Pending = new(snapshots[1], snapshots[2]) });
        Assert.All(snapshots, snapshot => Assert.True(Directory.Exists(snapshot.Directory)));
        f.Store.Prune(state);
        Assert.True(Directory.Exists(snapshots[0].Directory));
        Assert.Equal(3, snapshots.Count(snapshot => Directory.Exists(snapshot.Directory)));
    }

    [Fact]
    public void DesktopSnapshotExcludesRunningGuardian()
    {
        using var f = new Fixture();
        var guardian = Path.Combine(f.Settings.TargetDirectory("desktop"), "guardian");
        Directory.CreateDirectory(guardian);
        File.WriteAllText(Path.Combine(guardian, "PuddingDesktop.Guardian.exe"), "independent");
        var snapshot = f.Store.Capture("desktop");
        Assert.DoesNotContain(snapshot.Files, x => x.Path.StartsWith("guardian", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(snapshot.Files, x => x.Path.EndsWith("PuddingAgent.exe"));
    }

    [Fact]
    public void PathTraversalAndHashTamperingAreRejected()
    {
        using var f = new Fixture();
        Assert.Throws<IOException>(() => SafeFiles.Resolve(f.Candidate, "../outside.dll"));
        var snapshot = f.Store.Inspect(f.Candidate, "core");
        File.WriteAllText(Path.Combine(f.Candidate, "PuddingAgent.exe"), "changed");
        Assert.Throws<InvalidDataException>(() => f.Store.Apply(snapshot));
        Assert.Equal("old", File.ReadAllText(f.Settings.CoreExecutablePath));
    }

    [Fact]
    public async Task HttpRequiresHeaderTokenRejectsOriginsAndReturnsAcceptedReceipt()
    {
        using var f = new Fixture();
        using var server = new GuardianHttpServer(f.Settings, f.Controller);
        using var lifetime = new CancellationTokenSource();
        server.Start();
        var loop = server.RunAsync(lifetime.Token);
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = new($"http://127.0.0.1:{f.Settings.HttpPort}") };
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/guardian/status")).StatusCode);
        client.DefaultRequestHeaders.Add("X-Control-Token", "test-secret");
        client.DefaultRequestHeaders.Add("Origin", "https://untrusted.invalid");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/guardian/status")).StatusCode);
        client.DefaultRequestHeaders.Remove("Origin");
        var status = await client.GetStringAsync("/guardian/status");
        Assert.DoesNotContain("test-secret", status);
        Assert.Contains("pudding.guardian/1", status);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/guardian/commands", new { action = "shell", target = "desktop" })).StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await client.PostAsync("/guardian/commands", new StringContent(new string('x', 17000), Encoding.UTF8, "application/json"))).StatusCode);
        f.Runtime.BuildGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var command = new GuardianCommand();
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync("/guardian/commands", command)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/guardian/commands", command)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/guardian/commands", new GuardianCommand())).StatusCode);
        f.Runtime.BuildGate.SetResult();
        await f.Controller.RunningOperation!;
        lifetime.Cancel();
        await loop;
    }

    [Fact]
    public void ProcessIdentityRejectsReusedPidAndWrongExecutable()
    {
        using var current = System.Diagnostics.Process.GetCurrentProcess();
        var identity = GuardianRuntime.Identify(current.Id, Environment.ProcessPath!)!;
        Assert.True(GuardianRuntime.IsAlive(identity));
        Assert.False(GuardianRuntime.IsAlive(identity with { StartTimeUtc = identity.StartTimeUtc.AddSeconds(-1) }));
        Assert.Null(GuardianRuntime.Identify(current.Id, Path.Combine(Path.GetTempPath(), "not-this-process.exe")));
    }

    [Fact]
    public void ConfigurationCasingAndDirectoryIsolationMatchProductContract()
    {
        using var f = new Fixture();
        File.WriteAllText(Path.Combine(f.Settings.DataRoot, "config", "system.json"), """{"Desktop":{"Core":{"ControlToken":"case-test"}}}""");
        Assert.Equal("case-test", GuardianToken.Read(f.Settings));
        Assert.Throws<ArgumentException>(() => (f.Settings with { StateDirectory = f.Settings.TargetDirectory("desktop") }).Validate());
        Assert.Throws<ArgumentException>(() => (f.Settings with { DataRoot = f.Settings.TargetDirectory("desktop") }).Validate());
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "pudding-guardian-tests", Guid.NewGuid().ToString("N"));
        public GuardianSettings Settings { get; }
        public GuardianStore Store { get; }
        public FakeRuntime Runtime { get; }
        public GuardianController Controller { get; }
        public AdvancingTime Time { get; } = new();
        public string Candidate { get; }
        public Fixture()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Settings = new() { DesktopHome = Path.Combine(_root, "home"), DataRoot = Path.Combine(_root, "data"),
                StateDirectory = Path.Combine(_root, "state"), DesktopExecutablePath = Path.Combine(_root, "package", "PuddingDesktop.exe"),
                CoreExecutablePath = Path.Combine(_root, "package", "core", "PuddingAgent.exe"),
                HttpPort = port, StableSeconds = 1, PollSeconds = 1, StartupTimeoutSeconds = 5, FailureThreshold = 1 };
            Directory.CreateDirectory(Settings.TargetDirectory("core"));
            File.WriteAllText(Settings.CoreExecutablePath, "old");
            File.WriteAllText(Settings.DesktopExecutablePath, "shell");
            Directory.CreateDirectory(Path.Combine(Settings.DataRoot, "config"));
            File.WriteAllText(Path.Combine(Settings.DataRoot, "config", "system.json"), """{"desktop":{"core":{"controlToken":"test-secret"}}}""");
            Candidate = Path.Combine(_root, "candidate");
            Directory.CreateDirectory(Candidate);
            File.WriteAllText(Path.Combine(Candidate, "PuddingAgent.exe"), "new");
            Store = new(Settings);
            Runtime = new(Settings, Candidate);
            Controller = new(Settings, Store, Runtime, Time);
        }
        public async Task Execute(GuardianCommand command)
        {
            Assert.Equal("accepted", Controller.Submit(command, default));
            await Controller.RunningOperation!;
        }
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }

    private sealed class FakeRuntime(GuardianSettings settings, string candidate) : IGuardianRuntime
    {
        public ProcessIdentity? Desktop => null;
        public ProcessIdentity? Core => null;
        public List<string> Stops { get; } = [];
        public List<string> Starts { get; } = [];
        public bool BuildFailure, IntentionalStop;
        public Func<bool>? HealthOverride;
        public TaskCompletionSource? BuildGate;
        public Task<GuardianHealth> ProbeAsync(CancellationToken ct)
        {
            var healthy = HealthOverride?.Invoke() ?? File.ReadAllText(settings.CoreExecutablePath) != "bad";
            return Task.FromResult(new GuardianHealth(true, healthy, IntentionalStop));
        }
        public async Task<string> PrepareAsync(GuardianCommand command, CancellationToken ct)
        {
            if (BuildGate is not null) await BuildGate.Task.WaitAsync(ct);
            if (BuildFailure) throw new InvalidOperationException("build rejected");
            return candidate;
        }
        public Task StopAsync(string target, CancellationToken ct) { Stops.Add(target); return Task.CompletedTask; }
        public Task StartAsync(string target, bool yolo, CancellationToken ct) { Starts.Add(target); return Task.CompletedTask; }
    }

    private sealed class AdvancingTime : TimeProvider
    {
        private long _ticks = DateTimeOffset.UtcNow.Ticks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
        public void Advance(TimeSpan time) => Interlocked.Add(ref _ticks, time.Ticks);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Advance(dueTime);
            return new TimerAdapter(callback, state);
        }
        private sealed class TimerAdapter : ITimer
        {
            private readonly Timer _timer;
            public TimerAdapter(TimerCallback callback, object? state) => _timer = new(callback, state, TimeSpan.FromMilliseconds(1), Timeout.InfiniteTimeSpan);
            public bool Change(TimeSpan dueTime, TimeSpan period) => _timer.Change(dueTime, period);
            public void Dispose() => _timer.Dispose();
            public ValueTask DisposeAsync() => _timer.DisposeAsync();
        }
    }
}
