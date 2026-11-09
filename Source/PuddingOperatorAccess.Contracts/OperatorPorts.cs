using System.Collections.Immutable;
using System.Text.Json;
using Pudding.Api.Contracts;

namespace PuddingOperatorAccess.Contracts;

/// <summary>Trusted composition grants access; plugin payloads cannot deserialize this type.</summary>
public sealed record OperatorGrant(string CallerId, OperatorScopeRef Scope, DateTimeOffset Deadline, bool CanList,
    ImmutableArray<string> ScoreScenes, int MaxCalls = 16);
public interface IOperatorBackend
{
    OperatorDescriptor Descriptor { get; }
    Task<OperatorScoreValue> ScoreAsync(JsonElement input, CancellationToken ct);
}
public interface IOperatorLease : IOperatorApi, IAsyncDisposable { }
