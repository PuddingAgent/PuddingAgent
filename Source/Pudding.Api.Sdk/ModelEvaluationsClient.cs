using Pudding.Api.Contracts;

namespace Pudding.Api.Sdk;

/// <summary>Consumes a host-bound API; callers do not acquire keys, select models or create parent budgets.</summary>
public sealed class ModelEvaluationsClient(IModelEvaluationApi api)
{
    public Task<OperatorCatalog> ListAsync(CancellationToken ct = default) => api.ListAsync(ct);
    public Task<ClassificationReceipt> ClassifyAsync(ClassificationInput input, ModelEvaluationOptions options, CancellationToken ct = default)
        => api.ClassifyAsync(new(input, options), ct);
    public Task<AcceptanceReceipt> ReviewAsync(AcceptanceInput input, ModelEvaluationOptions options, CancellationToken ct = default)
        => api.ReviewAsync(new(input, options), ct);
}
