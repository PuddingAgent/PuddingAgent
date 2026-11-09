using Pudding.Api.Contracts;

namespace Pudding.Api.Sdk;

/// <summary>
/// PA-06 model-catalog facade. Listing exposes only authorized capabilities; Resolve returns the explicit
/// purpose model decision. No provider secret or endpoint is reachable through this client.
/// </summary>
public sealed class ModelCatalogClient(IModelCatalogApi api)
{
    public Task<ModelCatalogPage> ListAsync(ModelQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return api.ListAsync(query, ct);
    }

    public Task<ModelSelection> ResolveAsync(ModelSelectionRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return api.ResolveAsync(request, ct);
    }
}
