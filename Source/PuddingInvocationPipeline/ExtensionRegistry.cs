using System.Collections.Immutable;
using PuddingInvocationPipeline.Contracts;

namespace PuddingInvocationPipeline;

/// <summary>
/// PA-07 07B extension registry: each load produces an immutable snapshot for one point. A caller that observed a
/// generation must re-check it before committing a side effect; duplicate extension ids are rejected; an invalid or
/// unreachable source is a typed failure rather than an empty (silently disabled) plugin set.
/// </summary>
public sealed class ExtensionRegistry
{
    public async ValueTask<ExtensionSnapshot> LoadAsync(IExtensionSource source, string point,
        long? observedGeneration = null, CancellationToken ct = default)
    {
        if (!ExtensionPoints.Known(point))
            throw Error(ExtensionErrorCodes.PointUnknown, "The extension point is not published.");
        if (source is null) throw Error(ExtensionErrorCodes.SourceUnavailable, "An extension source is required.");
        ExtensionSnapshot snapshot;
        try
        {
            snapshot = await source.LoadAsync(point, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (ExtensionException) { throw; }
        catch (Exception) { throw Error(ExtensionErrorCodes.SourceUnavailable, "The extension registry is unavailable."); }
        if (snapshot is null || snapshot.Generation < 1 || snapshot.Extensions.IsDefault)
            throw Error(ExtensionErrorCodes.SourceUnavailable, "The extension snapshot is invalid.");
        if (observedGeneration is { } observed && observed != snapshot.Generation)
            throw Error(ExtensionErrorCodes.GenerationStale, "The observed extension generation is stale.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var filtered = ImmutableArray.CreateBuilder<ExtensionDescriptor>();
        foreach (var descriptor in snapshot.Extensions)
        {
            if (descriptor is null || !descriptor.IsValid)
                throw Error(ExtensionErrorCodes.DescriptorInvalid, "An extension descriptor is invalid.");
            if (!ids.Add(descriptor.ExtensionId))
                throw Error(ExtensionErrorCodes.Duplicate, "An extension id is duplicated in the snapshot.");
            if (string.Equals(descriptor.Point, point, StringComparison.Ordinal)) filtered.Add(descriptor);
        }
        return new(filtered.ToImmutable(), snapshot.Generation);
    }

    /// <summary>Second check before a side effect: a generation change invalidates the earlier snapshot decision.</summary>
    public static void EnsureGeneration(long observedGeneration, long currentGeneration)
    {
        if (observedGeneration != currentGeneration)
            throw Error(ExtensionErrorCodes.GenerationStale, "The extension generation changed before commit.");
    }

    private static ExtensionException Error(string code, string message) => new(code, message);
}
