using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Pudding.Api.Contracts;
using PuddingOperatorAccess.Contracts;

namespace PuddingOperatorAccess;

/// <summary>Immutable capability catalog and bounded invocation factory, independent of Runtime and plugins.</summary>
public sealed class OperatorAccessService
{
    private readonly FrozenDictionary<string, (IOperatorBackend Backend, OperatorDescriptor Descriptor)> _backends;
    private readonly TimeProvider _clock;
    public OperatorAccessService(IEnumerable<IOperatorBackend> backends, TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        var entries = new Dictionary<string, (IOperatorBackend, OperatorDescriptor)>(StringComparer.Ordinal);
        foreach (var backend in backends)
        {
            var d = backend.Descriptor;
            if (string.IsNullOrWhiteSpace(d.SceneKey) || string.IsNullOrWhiteSpace(d.InputSchema) || d.InputVersion < 1
                || string.IsNullOrWhiteSpace(d.ImplementationVersion) || d.UsesModel || d.Primitive != "score" || d.MaxInputBytes is < 1 or > 65536)
                throw new ArgumentException("Only bounded deterministic score backends are supported.", nameof(backends));
            if (!entries.TryAdd(d.SceneKey, (backend, d))) throw new ArgumentException("Duplicate operator scene.", nameof(backends));
        }
        _backends = entries.ToFrozenDictionary(StringComparer.Ordinal);
    }
    public IOperatorLease Open(OperatorGrant grant)
    {
        var remaining = grant.Deadline - _clock.GetUtcNow();
        if (grant.Scope is null || new[] { grant.CallerId, grant.Scope.WorkspaceId, grant.Scope.AgentId, grant.Scope.SessionId }.Any(s => string.IsNullOrWhiteSpace(s) || s.Length > 256 || s.Any(char.IsControl))
            || remaining <= TimeSpan.Zero || remaining > TimeSpan.FromMinutes(5) || grant.MaxCalls is < 1 or > 32
            || grant.ScoreScenes.IsDefault || grant.ScoreScenes.Length > 32 || grant.ScoreScenes.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("A live, bounded host grant is required.", nameof(grant));
        return new Lease(this, grant, remaining);
    }
    private sealed class Lease(OperatorAccessService owner, OperatorGrant grant, TimeSpan remaining) : IOperatorLease
    {
        private readonly FrozenSet<string> _scenes = grant.ScoreScenes.ToFrozenSet(StringComparer.Ordinal);
        private readonly CancellationTokenSource _stop = new(remaining, owner._clock);
        private readonly SemaphoreSlim _gate = new(1, 1);
        private int _revoked, _disposed, _calls;
        public Task<OperatorCatalog> ListAsync(CancellationToken ct = default) => Run(async token =>
        {
            if (!grant.CanList) throw Error("permission.denied", "Operator discovery is not granted.");
            var items = owner._backends.Where(p => _scenes.Contains(p.Key)).OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => p.Value.Descriptor).ToImmutableArray();
            await Task.CompletedTask;
            return new OperatorCatalog(items);
        }, 5000, ct);

        public Task<OperatorScoreReceipt> ScoreAsync(OperatorScoreRequest request, CancellationToken ct = default)
        {
            if (request is null || string.IsNullOrWhiteSpace(request.SceneKey) || string.IsNullOrWhiteSpace(request.InputSchema)
                || request.Options is null || string.IsNullOrWhiteSpace(request.Options.CallId)
                || request.Options.CallId.Length > 128 || request.Options.CallId.Any(char.IsControl)
                || request.Options.TimeoutMilliseconds is < 1 or > 60000)
                throw Error("request.invalid", "A scene, schema, bounded call ID and timeout are required.");
            return Run(async token =>
            {
                if (!_scenes.Contains(request.SceneKey)) throw Error("permission.denied", "This scoring scene is not granted.");
                if (!owner._backends.TryGetValue(request.SceneKey, out var entry)) throw Error("operator.not_found", "The scoring scene is not installed.");
                if (request.InputSchema != entry.Descriptor.InputSchema || request.InputVersion != entry.Descriptor.InputVersion)
                    throw Error("schema.unsupported", "The scoring input schema/version does not match.");
                if (request.Input.ValueKind != JsonValueKind.Object || Encoding.UTF8.GetByteCount(request.Input.GetRawText()) > entry.Descriptor.MaxInputBytes)
                    throw Error("request.invalid", "Expected a bounded input object.");
                OperatorScoreValue value;
                try { value = await entry.Backend.ScoreAsync(request.Input, token); }
                catch (Exception ex) when (ex is not (OperatorApiException or OperationCanceledException))
                { throw Error("operator.failed", "The scoring backend failed; no result was confirmed."); }
                token.ThrowIfCancellationRequested();
                Validate(value, request.SceneKey);
                var fingerprint = Hash(new { grant.CallerId, grant.Scope, request.SceneKey, request.InputSchema, request.InputVersion, value.Provenance.InputDigest });
                var id = Hash(new { request.Options.CallId, fingerprint, entry.Descriptor.ImplementationVersion });
                return new OperatorScoreReceipt(request.Options.CallId, id, grant.Scope, fingerprint, "caller-supplied", value);
            }, request.Options.TimeoutMilliseconds, ct);
        }
        private async Task<T> Run<T>(Func<CancellationToken, Task<T>> action, int timeout, CancellationToken ct)
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                if (Volatile.Read(ref _revoked) != 0) throw Error("delegation.revoked", "The operator invocation has ended.");
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
                linked.CancelAfter(TimeSpan.FromMilliseconds(timeout));
                await _gate.WaitAsync(linked.Token);
                try
                {
                    if (Volatile.Read(ref _revoked) != 0 || owner._clock.GetUtcNow() >= grant.Deadline) throw Error("delegation.revoked", "The operator invocation has ended.");
                    if (_calls++ >= grant.MaxCalls) throw Error("delegation.limit", "The operator call budget is exhausted.");
                    var result = await action(linked.Token);
                    linked.Token.ThrowIfCancellationRequested();
                    return result;
                }
                finally { _gate.Release(); }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            { throw Error(Volatile.Read(ref _revoked) != 0 ? "delegation.revoked" : "operation.timeout", "The operator invocation ended before a result was confirmed."); }
            catch (ObjectDisposedException) when (Volatile.Read(ref _revoked) != 0)
            { throw Error("delegation.revoked", "The operator invocation has ended."); }
        }
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _revoked, 1) == 0)
                try { _stop.Cancel(); } catch (AggregateException) { }
            await _gate.WaitAsync(); _gate.Release();
            if (Interlocked.Exchange(ref _disposed, 1) == 0) _stop.Dispose();
        }
    }
    private static void Validate(OperatorScoreValue value, string scene)
    {
        if (value is null || value.Provenance is null || value.Provenance.SceneKey != scene || value.Provenance.ModelId is not null
            || string.IsNullOrWhiteSpace(value.Provenance.InputDigest) || string.IsNullOrWhiteSpace(value.Provenance.OperatorId)
            || string.IsNullOrWhiteSpace(value.Scale) || value.Provenance.Evidence.IsDefault
            || value.Availability is not (ScoreAvailability.Valid or ScoreAvailability.InsufficientData or ScoreAvailability.Degraded)
            || (value.Availability == ScoreAvailability.Valid ? value.Score is null || !double.IsFinite(value.Score.Value) : value.Score is not null))
            throw Error("operator.result_invalid", "The operator result violates its public contract.");
    }
    private static string Hash<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
    private static OperatorApiException Error(string code, string message) => new(code, message);
}
