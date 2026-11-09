using System.Text.Json;
using System.Collections.Frozen;
using Pudding.ToolPlugins.Contracts;

namespace Pudding.ToolPlugins.Runtime;

public sealed record PluginAuthorization(bool ExecuteApproved, IReadOnlySet<string> Capabilities, bool HooksApproved,
    bool SubscriptionsApproved = false);
public sealed record PluginActivation(string ActivationId, string PluginId, string Version, string ContentHash,
    PluginScope Scope, string State, long Revision, int? ProcessId, IReadOnlyList<string> ToolNames,
    bool HooksEnabled, PluginFailure? Failure, string ContainmentLevel = "process", bool StopWasForced = false);
public sealed record PluginDiagnostic(DateTimeOffset At, string ActivationId, PluginScope Scope, PluginFailure Failure);
public sealed record PluginCallable(string FunctionName, string ActivationId, PluginScope Scope, PluginToolDefinition Definition);

/// <summary>Owns activations; OS process ownership is behind IPluginWorkerFactory. Never loads a DLL.</summary>
public sealed class PluginManager(PluginPackageStore packages, IPluginWorkerFactory workers) : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new();
    private readonly Dictionary<string, (string Fingerprint, Task<PluginActivation> Task)> _operations = new();
    private readonly Queue<PluginDiagnostic> _diagnostics = new();
    private long _revision;
    private bool _disposed;
    public long Revision { get { lock (_gate) { ReapExited(); return _revision; } } }
    public IReadOnlyList<PluginActivation> List(PluginScope? scope = null)
    { lock (_gate) { ReapExited(); return _entries.Values.Where(e => scope is null || e.Scope == scope).Select(Snapshot).ToArray(); } }
    public IReadOnlyList<PluginDiagnostic> Diagnostics(PluginScope? scope = null)
    { lock (_gate) return _diagnostics.Where(e => scope is null || e.Scope == scope).ToArray(); }
    public IReadOnlyList<PluginCallable> Tools(PluginScope scope)
    {
        lock (_gate) { ReapExited(); return _entries.Values.Where(e => e.Scope == scope && e.State == "Active")
            .SelectMany(e => e.Package.Manifest.Tools.Select(t => new PluginCallable(Name(e, t.Id), e.Id, e.Scope, t))).ToArray(); }
    }
    public Task<PluginActivation> LoadAsync(string operationId, PluginPackage package, PluginScope scope,
        PluginAuthorization authorization, long? expectedRevision = null, CancellationToken ct = default)
    {
        ValidateScope(scope);
        // IReadOnlySet may still wrap a mutable HashSet supplied by a caller. Freeze before publishing a grant.
        authorization = authorization with { Capabilities = authorization.Capabilities.ToFrozenSet(StringComparer.Ordinal) };
        if ((package.Manifest.Tools.Length > 0 && !authorization.ExecuteApproved)
            || (package.Manifest.Hooks.Length > 0 && !authorization.HooksApproved)
            || (package.Manifest.Subscriptions.Length > 0 && !authorization.SubscriptionsApproved)
            || package.Manifest.RequestedCapabilities.Any(c => !authorization.Capabilities.Contains(c)))
            throw new PluginException("plugin.capability_denied", "authorize", "Execution, requested capabilities, hooks and subscriptions require host authorization.");
        if (string.IsNullOrWhiteSpace(operationId) || operationId.Length > 100) throw new PluginException("plugin.operation_invalid", "load", "operationId is required.");
        var opKey = JsonSerializer.Serialize(new { scope, operationId });
        var fingerprint = JsonSerializer.Serialize(new { package.ContentHash, package.Manifest.Id, package.Manifest.Version,
            capabilities = authorization.Capabilities.Order().ToArray(), authorization.HooksApproved });
        Entry entry;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ReapExited();
            if (_operations.TryGetValue(opKey, out var op))
            {
                if (op.Fingerprint != fingerprint) throw new PluginException("plugin.operation_conflict", "load", "operationId was already used with different content.");
                return op.Task;
            }
            CheckRevision(expectedRevision);
            var existing = _entries.Values.FirstOrDefault(e => e.Scope == scope && e.Package.ContentHash == package.ContentHash && e.State is "Loading" or "Active");
            if (existing is not null) return existing.Completion.Task;
            if (_entries.Values.Count(ConsumesCapacity) >= 16
                || _entries.Values.Count(e => e.Scope.AgentId == scope.AgentId && e.Scope.WorkspaceId == scope.WorkspaceId && ConsumesCapacity(e)) >= 4)
                throw new PluginException("plugin.resource_limit", "load", "Active plugin instance budget exhausted.");
            TrimHistory();
            entry = new(package, scope, authorization, ++_revision);
            _entries.Add(entry.Id, entry);
            _operations.Add(opKey, (fingerprint, entry.Completion.Task));
        }
        _ = InitializeAsync(entry, ct);
        return entry.Completion.Task;
    }
    private async Task InitializeAsync(Entry e, CancellationToken caller)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(caller, e.Lifetime.Token);
        linked.CancelAfter(TimeSpan.FromSeconds(30));
        IPluginWorker? worker = null;
        try
        {
            packages.Verify(e.Package);
            worker = await workers.StartAsync(e.Package, e.Scope, e.Id, linked.Token);
            lock (_gate) { e.Worker = worker; if (e.State != "Loading") linked.Cancel(); }
            linked.Token.ThrowIfCancellationRequested();
            var payload = PluginJson.Value(new { manifest = e.Package.Manifest, directory = e.Package.Directory, scope = e.Scope });
            var result = await worker.RequestAsync(new("initialize", payload, DateTimeOffset.UtcNow.AddSeconds(15)), DeniedCallback, linked.Token);
            var tools = result.GetProperty("toolIds").Deserialize<string[]>()!;
            var hooks = result.GetProperty("hookIds").Deserialize<string[]>()!;
            var subscriptions = result.GetProperty("subscriptionIds").Deserialize<string[]>()!;
            if (!tools.Order().SequenceEqual(e.Package.Manifest.Tools.Select(t => t.Id).Order())
                || !hooks.Order().SequenceEqual(e.Package.Manifest.Hooks.Select(h => h.Id).Order())
                || !subscriptions.Order().SequenceEqual(e.Package.Manifest.Subscriptions.Select(s => s.Id).Order()))
                throw new PluginException("plugin.descriptor_mismatch", "publish", "Implemented contribution IDs differ from the manifest.");
            lock (_gate)
            {
                if (e.State != "Loading" || linked.IsCancellationRequested) throw new OperationCanceledException();
                e.State = "Active"; e.Revision = ++_revision; e.Completion.TrySetResult(Snapshot(e));
            }
        }
        catch (Exception ex)
        {
            var exited = worker is null || await worker.StopAsync(CancellationToken.None);
            lock (_gate)
            {
                if (e.State == "Loading") e.State = exited ? "Failed" : "StopFailed";
                e.Failure = Failure(ex, "initialize"); e.Revision = ++_revision; Record(e);
                e.Completion.TrySetResult(Snapshot(e));
            }
        }
        finally { e.Initialized.TrySetResult(); }
    }

    public async Task<PluginActivation> UnloadAsync(string activationId, PluginScope scope, long? expectedRevision = null, CancellationToken ct = default)
    {
        Entry e;
        lock (_gate)
        {
            e = Own(activationId, scope); CheckRevision(expectedRevision);
            if (e.State == "Unloaded") return Snapshot(e);
            e.State = "Stopping"; e.Revision = ++_revision;
        }
        e.Lifetime.Cancel();
        // Loading has a 30-second bound; late initialize must not republish a stopped entry.
        try { await e.Initialized.Task.WaitAsync(TimeSpan.FromSeconds(35), ct); } catch (OperationCanceledException) { } catch (TimeoutException) { }
        var stopped = e.Worker is null ? e.Initialized.Task.IsCompleted : await e.Worker.StopAsync(CancellationToken.None);
        var drained = false;
        try { await e.CallsDrained.Task.WaitAsync(TimeSpan.FromSeconds(5), ct); drained = true; }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) { }
        lock (_gate)
        {
            e.State = stopped && drained ? "Unloaded" : "StopFailed"; e.Revision = ++_revision;
            if (!stopped || !drained) { e.Failure = new("plugin.stop_failed", "stop", Guid.NewGuid().ToString("N"), "Worker exit or host callback drain has not been confirmed.", "unknown"); Record(e); }
            return Snapshot(e);
        }
    }

    public async Task<PluginResult> ExecuteAsync(string functionName, PluginScope scope, string callId, JsonElement arguments,
        Func<string, JsonElement, CancellationToken, Task<JsonElement>> callback, DateTimeOffset? outerDeadline = null, CancellationToken ct = default)
    {
        Entry e; PluginToolDefinition tool;
        lock (_gate)
        {
            e = _entries.Values.SingleOrDefault(e => e.Scope == scope && e.State == "Active" && e.Package.Manifest.Tools.Any(t => Name(e, t.Id) == functionName))
                ?? throw new PluginException("plugin.unloaded", "execute", "Plugin is not bound to this agent/session or its generation has changed.");
            tool = e.Package.Manifest.Tools.Single(t => Name(e, t.Id) == functionName);
        }
        PluginValidation.Value(tool.InputSchema, arguments);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(e.Package.Manifest.Limits.ExecutionTimeoutSeconds);
        if (outerDeadline is { } d && d < deadline) deadline = d;
        var call = new PluginCall(callId, tool.Id, arguments, scope, deadline);
        var payload = await InvokeAsync(e, "execute", PluginJson.Value(call), deadline, async (method, args, token) =>
        {
            if (method == "agent.configuration.update" && !tool.Effects.Contains("agent.configuration.write"))
                throw new PluginException("plugin.core_callback_denied", "core_callback", "This tool does not declare configuration write effects.");
            return await callback(method, args, token);
        }, ct);
        var result = payload.Deserialize<PluginResult>(PluginJson.Options) ?? throw new PluginException("plugin.result_invalid", "execute", "Missing result.");
        if (result.Success) PluginValidation.Value(tool.OutputSchema, result.Value);
        return result;
    }

    public async Task<IReadOnlyList<(string PluginId, string Advice)>> AdviseAsync(PluginHookCall call, CancellationToken ct)
    {
        (Entry Entry, PluginHookDefinition Hook)[] hooks;
        lock (_gate) hooks = _entries.Values.Where(e => e.Scope == call.Scope && e.State == "Active" && e.Authorization.HooksApproved)
            .SelectMany(e => e.Package.Manifest.Hooks.Select(h => (e, h))).OrderBy(x => x.h.Order)
            .ThenBy(x => x.e.Package.Manifest.Id, StringComparer.Ordinal).ThenBy(x => x.h.Id, StringComparer.Ordinal).ToArray();
        var result = new List<(string, string)>();
        foreach (var (e, h) in hooks)
        {
            try
            {
                var payload = await InvokeAsync(e, "hook", PluginJson.Value(call with { HookId = h.Id }), DateTimeOffset.UtcNow.AddSeconds(3), DeniedCallback, ct);
                var advice = payload.Deserialize<PluginHookResult>(PluginJson.Options)?.AppendText;
                if (!string.IsNullOrWhiteSpace(advice))
                {
                    if (advice.Length > 2048) throw new PluginException("plugin.output_limit", "hook", "Hook advice exceeds 2048 characters.");
                    result.Add((e.Package.Manifest.Id, advice));
                }
            }
            catch (Exception ex)
            { lock (_gate) { e.Failure = Failure(ex, "hook"); Record(e); } }
        }
        return result;
    }

    /// <summary>
    /// Delivers one host workspace event to every active subscription in the matching workspace. Delivery is outside any
    /// Agent session; <see cref="PluginWorkspaceEvent.EventId"/> is the host idempotency key the consumer dedupes on.
    /// </summary>
    /// <summary>
    /// Delivers one host workspace event to every active subscription in the matching workspace, optionally restricted to
    /// one plugin id/generation (a durable event subscription delivers to its own subscriber, not to every subscriber).
    /// </summary>
    public async Task<IReadOnlyList<PluginEventDelivery>> PublishWorkspaceEventAsync(PluginWorkspaceEvent workspaceEvent,
        string? targetPluginId = null, long? targetGeneration = null,
        Func<string, JsonElement, CancellationToken, Task<JsonElement>>? callback = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(workspaceEvent);
        (Entry Entry, PluginSubscriptionDefinition Subscription)[] subscriptions;
        lock (_gate)
        {
            subscriptions = _entries.Values
                .Where(e => e.State == "Active" && e.Authorization.SubscriptionsApproved
                    && (targetPluginId is null
                        || string.Equals(e.Package.Manifest.Id, targetPluginId, StringComparison.Ordinal))
                    && (targetGeneration is null || e.Revision == targetGeneration)
                    && string.Equals(e.Scope.WorkspaceId, workspaceEvent.Scope.WorkspaceId, StringComparison.Ordinal)
                    && (string.IsNullOrWhiteSpace(e.Scope.AgentId)
                        || string.Equals(e.Scope.AgentId, workspaceEvent.Scope.AgentId, StringComparison.Ordinal))
                    && (string.IsNullOrWhiteSpace(e.Scope.SessionId)
                        || string.Equals(e.Scope.SessionId, workspaceEvent.Scope.SessionId, StringComparison.Ordinal)))
                .SelectMany(e => e.Package.Manifest.Subscriptions
                    .Where(s => string.Equals(s.Event, workspaceEvent.EventName, StringComparison.Ordinal))
                    .Select(s => (Entry: e, Subscription: s)))
                .ToArray();
        }
        var deliveries = new List<PluginEventDelivery>(subscriptions.Length);
        foreach (var (e, subscription) in subscriptions)
        {
            try
            {
                await InvokeAsync(e, "event", PluginJson.Value(workspaceEvent),
                    DateTimeOffset.UtcNow.AddSeconds(5), callback ?? DeniedCallback, ct);
                deliveries.Add(new(e.Id, e.Package.Manifest.Id, subscription.Id, true));
            }
            catch (Exception ex)
            {
                var failure = Failure(ex, "event");
                lock (_gate) { e.Failure = failure; Record(e); }
                deliveries.Add(new(e.Id, e.Package.Manifest.Id, subscription.Id, false, failure.Code));
            }
        }
        return deliveries;
    }

    private async Task<JsonElement> InvokeAsync(Entry e, string kind, JsonElement payload, DateTimeOffset deadline,
        Func<string, JsonElement, CancellationToken, Task<JsonElement>> callback, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, e.Lifetime.Token);
        lock (_gate)
        {
            if (e.State != "Active" || e.Worker is null) throw new PluginException("plugin.unloaded", kind, "Activation is no longer active.");
            if (e.InFlight++ == 0) e.CallsDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        try
        {
            return await e.Worker!.RequestAsync(new(kind, payload, deadline), async (method, args, token) =>
            {
                lock (_gate)
                {
                    var active = e.State == "Active" && !e.Lifetime.IsCancellationRequested
                        && DateTimeOffset.UtcNow < deadline;
                    // Event observers may only request the host-derived archive; execute keeps the existing capability lease.
                    var allowed = kind == "event"
                        ? active && method == "plugin.archive.record"
                        : kind == "execute" && active
                          && e.Authorization.Capabilities.Contains(method)
                          && e.Package.Manifest.RequestedCapabilities.Contains(method);
                    if (!allowed)
                        throw new PluginException("plugin.core_callback_denied", "core_callback",
                            "Callback is outside the current execution lease.");
                }
                return await callback(method, args, token);
            }, linked.Token);
        }
        catch (Exception ex)
        {
            var failure = Failure(ex, kind);
            var fatal = ex is OperationCanceledException or IOException or ObjectDisposedException
                || ex is PluginException pe && pe.Failure.Code is "plugin.protocol_invalid" or "plugin.output_limit" or "plugin.worker_exited";
            if (fatal)
            {
                lock (_gate) { if (e.State == "Active") { e.State = "Failed"; e.Revision = ++_revision; } }
                var stopped = await e.Worker!.StopAsync(CancellationToken.None);
                if (!stopped) lock (_gate) e.State = "StopFailed";
            }
            lock (_gate) { e.Failure = failure; Record(e); }
            throw new PluginException(failure);
        }
        finally
        {
            lock (_gate) if (--e.InFlight == 0) e.CallsDrained.TrySetResult();
        }
    }
    private static Task<JsonElement> DeniedCallback(string method, JsonElement arguments, CancellationToken ct)
        => Task.FromException<JsonElement>(new PluginException("plugin.core_callback_denied", "core_callback", "No host capabilities are granted during lifecycle or hook callbacks."));
    private Entry Own(string id, PluginScope scope)
        => _entries.TryGetValue(id, out var e) && e.Scope == scope ? e : throw new PluginException("plugin.scope_denied", "authorize", "Activation is not owned by this scope.");
    private void CheckRevision(long? expected)
    { if (expected is { } revision && revision != _revision) throw new PluginException("plugin.revision_conflict", "manage", $"Catalog revision is {_revision}."); }
    private static void ValidateScope(PluginScope s)
    {
        if (new[] { s.WorkspaceId }.Any(v => string.IsNullOrWhiteSpace(v) || v.Length > 256))
            throw new PluginException("plugin.scope_denied", "authorize", "Workspace is required.");
        if (!string.IsNullOrWhiteSpace(s.ActivationKey))
        {
            // Host-scoped subscriber: an activation identity independent of any Agent session.
            if (s.ActivationKey.Length > 256)
                throw new PluginException("plugin.scope_denied", "authorize", "Activation identity is too long.");
            return;
        }
        if (new[] { s.AgentId, s.SessionId }.Any(v => string.IsNullOrWhiteSpace(v) || v.Length > 256))
            throw new PluginException("plugin.scope_denied", "authorize", "Workspace, execution agent and session are required.");
    }
    private static string Name(Entry e, string tool) => PluginValidation.FunctionName(e.Package.Manifest.Id, tool, e.Id);
    private PluginActivation Snapshot(Entry e) => new(e.Id, e.Package.Manifest.Id, e.Package.Manifest.Version, e.Package.ContentHash, e.Scope,
        e.State == "Active" && e.Worker?.HasExited == true ? "Failed" : e.State, e.Revision, e.Worker?.ProcessId,
        e.Package.Manifest.Tools.Select(t => Name(e, t.Id)).ToArray(), e.Authorization.HooksApproved && e.Package.Manifest.Hooks.Length > 0, e.Failure,
        StopWasForced: e.Worker?.StopWasForced ?? false);
    private static PluginFailure Failure(Exception ex, string stage) => ex is PluginException p ? p.Failure : new(
        ex is OperationCanceledException ? "plugin.execution_timeout" : ex is IOException ? "plugin.worker_exited" : "plugin.execution_failed",
        stage, Guid.NewGuid().ToString("N"), $"{stage}: {ex.GetType().Name}.", stage == "execute" ? "unknown" : "none");
    private void Record(Entry e)
    { if (e.Failure is null) return; _diagnostics.Enqueue(new(DateTimeOffset.UtcNow, e.Id, e.Scope, e.Failure)); while (_diagnostics.Count > 256) _diagnostics.Dequeue(); }
    private void TrimHistory()
    {
        if (_entries.Count >= 256) foreach (var id in _entries.Values.Where(e => !ConsumesCapacity(e) && e.State is "Failed" or "Unloaded").Select(e => e.Id).Take(64).ToArray()) _entries.Remove(id);
        if (_operations.Count >= 256) foreach (var key in _operations.Where(p => p.Value.Task.IsCompleted).Select(p => p.Key).Take(64).ToArray()) _operations.Remove(key);
    }
    private void ReapExited()
    {
        foreach (var e in _entries.Values.Where(e => e.State == "Active" && e.InFlight == 0 && e.Worker?.HasExited == true))
        {
            e.State = "Failed"; e.Revision = ++_revision;
            e.Failure = new("plugin.worker_exited", "idle", Guid.NewGuid().ToString("N"), "Worker exited outside an active call.");
            Record(e);
            // Close the job even if the main process has already exited: it may still own children.
            _ = e.Worker!.StopAsync(CancellationToken.None);
        }
    }
    public async ValueTask DisposeAsync()
    {
        Entry[] entries; lock (_gate) { _disposed = true; entries = _entries.Values.ToArray(); }
        foreach (var e in entries) { await UnloadAsync(e.Id, e.Scope); if (e.Worker is not null) await e.Worker.DisposeAsync(); e.Lifetime.Dispose(); }
    }
    private static bool ConsumesCapacity(Entry e) => e.InFlight > 0 || e.State is "Active" or "Loading" or "Stopping"
        || e.State == "StopFailed" && e.Worker?.HasExited != true;
    private sealed class Entry(PluginPackage package, PluginScope scope, PluginAuthorization authorization, long revision)
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public PluginPackage Package { get; } = package;
        public PluginScope Scope { get; } = scope;
        public PluginAuthorization Authorization { get; } = authorization;
        public long Revision = revision;
        public string State = "Loading";
        public IPluginWorker? Worker;
        public PluginFailure? Failure;
        public CancellationTokenSource Lifetime { get; } = new();
        public TaskCompletionSource<PluginActivation> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Initialized { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int InFlight;
        public TaskCompletionSource CallsDrained = Completed();
        private static TaskCompletionSource Completed()
        { var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); source.SetResult(); return source; }
    }
}
