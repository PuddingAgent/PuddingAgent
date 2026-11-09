using System.Collections.Immutable;
using Pudding.Api.Contracts;
using PuddingModels.Contracts;

namespace PuddingPlatform.Services.Models;

/// <summary>
/// PA-06B model catalog adapter: exposes only authorized catalog identity/capability/price-source fields through the
/// public read API. It never returns endpoint/API tokens or mutable provider objects. Model selection is intentionally
/// NOT faked here: resolving a purpose requires a host-bound actor identity (decision pending), so Resolve returns a
/// typed scope error instead of borrowing the agent id as the caller.
/// </summary>
public sealed class ModelCatalogAdapter(IModelCatalogSource source) : IModelCatalogApi
{
    public async Task<ModelCatalogPage> ListAsync(ModelQuery query, CancellationToken ct = default)
    {
        if (query is null || string.IsNullOrWhiteSpace(query.WorkspaceId) || query.WorkspaceId.Length > 128
            || query.MaxCount is < 1 or > 100 || query.Cursor is { Length: > 128 })
            throw new ModelCatalogException(ModelErrorCodes.RequestInvalid, "A bounded model query is required.");
        ImmutableArray<ModelCapabilityRecord> records;
        string? cursor = null;
        try
        {
            records = await source.ListAsync(query.WorkspaceId, query.Cursor, query.MaxCount, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (ModelException ex) { throw new ModelCatalogException(ex.Code, ex.Message); }
        catch (Exception) { throw new ModelCatalogException(ModelErrorCodes.Unavailable, "The model catalog is unavailable."); }
        var models = (records.IsDefault ? ImmutableArray<ModelCapabilityRecord>.Empty : records)
            .Where(r => r is not null && !string.IsNullOrWhiteSpace(r.ModelId))
            .OrderBy(r => r.ModelId, StringComparer.Ordinal)
            .Take(query.MaxCount)
            .Select(r => new ModelCatalogEntry(r.ModelId, r.Revision, r.MaxContextTokens, r.MaxOutputTokens,
                r.InputModalities, r.SupportsTools, r.SupportsStructuredOutput, r.Availability, r.PriceSource,
                r.ExplicitFree, r.Currency, r.InputPerMillion, r.CacheHitPerMillion, r.OutputPerMillion))
            .ToImmutableArray();
        cursor = records.IsDefault ? null : null;
        return new(models, cursor, Complete: models.Length < query.MaxCount);
    }

    public Task<ModelSelection> ResolveAsync(ModelSelectionRequest request, CancellationToken ct = default)
    {
        if (request is null || request.Agent is null || string.IsNullOrWhiteSpace(request.Agent.AgentId))
            throw new ModelCatalogException(ModelErrorCodes.RequestInvalid, "A bound agent and purpose are required.");
        // Pending decision: the host must bind the acting user; never substitute the agent id for the actor.
        throw new ModelCatalogException(ModelErrorCodes.ScopeForbidden,
            "Model selection requires a host-bound actor identity that is not available to this adapter yet.");
    }
}
