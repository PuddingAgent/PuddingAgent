using System.Security.Cryptography;
using System.Text.Json;
using Pudding.ToolPlugins.Contracts;

namespace Pudding.ToolPlugins.Runtime;

/// <summary>Product-authored data packages. Export keys name existing host operations, never a DLL or script.</summary>
public sealed record HostToolPackage(string Id, string Version, Dictionary<string, string> Exports, string ContentHash = "")
{
    public static IReadOnlyList<HostToolPackage> ReadInstalled(string directory)
    {
        if (!Directory.Exists(directory)) return [];
        var packages = new List<HostToolPackage>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var exports = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal))
        {
            var bytes = File.ReadAllBytes(path);
            var package = JsonSerializer.Deserialize<HostToolPackage>(bytes, PluginJson.Options);
            if (package is null || !package.Id.StartsWith("pudding.", StringComparison.Ordinal)
                || !ids.Add(package.Id) || !System.Version.TryParse(package.Version, out _)
                || package.Exports is not { Count: > 0 }
                || package.Exports.Any(e => string.IsNullOrWhiteSpace(e.Value) || !exports.Add(e.Key)))
                throw new InvalidDataException($"Invalid or duplicate product tool declaration: {Path.GetFileName(path)}");
            packages.Add(package with { ContentHash = Convert.ToHexStringLower(SHA256.HashData(bytes)) });
        }
        return packages;
    }
}

/// <summary>Scoped lifecycle for declarative host capabilities. An unload revokes new calls before draining admitted calls.</summary>
public sealed class HostToolMounts : IAsyncDisposable
{
    private sealed class Mount(PluginActivation activation)
    {
        public PluginActivation Activation = activation;
        public readonly CancellationTokenSource Stop = new();
        public readonly TaskCompletionSource Drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;
        public Task<PluginActivation>? Unloading;
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Mount> _mounts = new(StringComparer.Ordinal);
    private readonly Dictionary<(PluginScope, string), Mount> _operations = new();
    private long _revision;
    private bool _disposed;
    public long Revision { get { lock (_gate) return _revision; } }

    public IReadOnlyList<PluginActivation> List(PluginScope? scope = null)
    {
        lock (_gate) return _mounts.Values.Select(m => m.Activation)
            .Where(a => a.State != "Unloaded" && (scope is null || PluginManager.MountOwner(scope) == a.Scope)).ToArray();
    }

    public PluginActivation Load(HostToolPackage package, PluginScope scope, string operationId, long? expectedRevision)
    {
        if (string.IsNullOrWhiteSpace(scope.WorkspaceId) || string.IsNullOrWhiteSpace(scope.AgentId)
            || string.IsNullOrWhiteSpace(operationId))
            throw new PluginException("plugin.scope_denied", "load", "Workspace, agent and operation are required.");
        scope = PluginManager.MountOwner(scope);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_operations.TryGetValue((scope, operationId), out var prior))
            {
                if (prior.Activation.ContentHash != package.ContentHash)
                    throw new PluginException("plugin.operation_conflict", "load", "Operation belongs to another package.");
                return prior.Activation;
            }
            CheckRevision(expectedRevision);
            var active = _mounts.Values.FirstOrDefault(m => m.Activation.Scope == scope
                && m.Activation.PluginId == package.Id && m.Activation.State != "Unloaded");
            if (active is null)
            {
                var id = "host-" + Guid.NewGuid().ToString("N");
                active = new(new(id, package.Id, package.Version, package.ContentHash, scope, "Active",
                    ++_revision, null, [], false, null, "host-service"));
                _mounts.Add(id, active);
            }
            if (active.Activation.State != "Active")
                throw new PluginException("plugin.draining", "load", "The previous activation is draining.");
            _operations.Add((scope, operationId), active);
            return active.Activation;
        }
    }

    public async Task<T> RunAsync<T>(string id, PluginScope scope, Func<CancellationToken, Task<T>> operation, CancellationToken ct)
    {
        Mount m;
        lock (_gate)
        {
            ct.ThrowIfCancellationRequested();
            if (!_mounts.TryGetValue(id, out var entry) || entry.Activation.Scope != PluginManager.MountOwner(scope) || entry.Activation.State != "Active")
                throw new PluginException("plugin.unloaded", "execute", "This activation is not available in this scope.");
            m = entry;
            m.Calls++;
        }
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, m.Stop.Token);
            linked.Token.ThrowIfCancellationRequested();
            return await operation(linked.Token).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
                if (--m.Calls == 0 && m.Activation.State == "Draining") m.Drained.TrySetResult();
        }
    }

    public Task<PluginActivation> UnloadAsync(string id, PluginScope scope, long? expectedRevision, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_mounts.TryGetValue(id, out var m) || m.Activation.Scope != PluginManager.MountOwner(scope))
                throw new PluginException("plugin.scope_denied", "unload", "Unknown activation in this scope.");
            if (m.Unloading is null)
            {
                CheckRevision(expectedRevision);
                m.Activation = m.Activation with { State = "Draining", Revision = ++_revision };
                if (m.Calls == 0) m.Drained.TrySetResult();
                m.Unloading = DrainAsync(m);
            }
            return m.Unloading.WaitAsync(ct);
        }
    }

    private async Task<PluginActivation> DrainAsync(Mount m)
    {
        try { await m.Stop.CancelAsync().ConfigureAwait(false); }
        finally
        {
            await m.Drained.Task.ConfigureAwait(false);
            lock (_gate)
            {
                m.Activation = m.Activation with { State = "Unloaded", Revision = ++_revision };
                m.Stop.Dispose();
            }
        }
        return m.Activation;
    }

    private void CheckRevision(long? expected)
    {
        if (expected.HasValue && expected != _revision)
            throw new PluginException("plugin.revision_conflict", "lifecycle", "Product tool revision changed.");
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate) _disposed = true;
        await Task.WhenAll(List().Select(a => UnloadAsync(a.ActivationId, a.Scope, null, default)));
    }
}
