using System.Text.Json;
using System.Text.Json.Serialization;
using Pudding.Api.Contracts;
using Pudding.ToolPlugins.Contracts;
using PuddingCode.Tools;
using PuddingExecutionResources.Contracts;
using PuddingModelEvaluation;

namespace PuddingRuntime.Operators.PublicApi;

public sealed class ModelEvaluationApiFactory(ModelEvaluationService service, IExecutionModelBinder models)
{
    public ModelEvaluationApiLease Open(ToolExecutionContext context, DateTimeOffset deadline, bool canList, bool canClassify, bool canReview)
    {
        var parent = context.ExecutionResources ?? throw new PluginException("execution.parent_required", "authorize", "Model evaluation requires a live host-owned parent budget.");
        if (parent.Identity.WorkspaceId != context.WorkspaceId || parent.Identity.AgentId != context.AgentInstanceId || parent.Identity.SessionId != context.SessionId
            || context.ExecutionIdentity is { } id && id.RunId != parent.Identity.RunId)
            throw new PluginException("execution.scope_denied", "authorize", "Parent model budget does not belong to this execution.");
        try { return new(service, models, context, parent.CreateChild(new(MaxCalls: 8), deadline), canList, canClassify, canReview); }
        catch (ExecutionResourceException ex) { throw new PluginException(ex.Code, "authorize", ex.Message); }
    }
}

public sealed class ModelEvaluationApiLease(ModelEvaluationService service, IExecutionModelBinder models, ToolExecutionContext context,
    IExecutionResourceScope scope, bool canList, bool canClassify, bool canReview) : IModelEvaluationApi, IPluginOperatorApiLease
{
    private readonly SemaphoreSlim _binding = new(1, 1);
    private IModelEvaluationApi? _api;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true };
    private async Task<IModelEvaluationApi> Get(CancellationToken ct)
    {
        if (scope.Cancellation.IsCancellationRequested) throw new ModelEvaluationException("execution.revoked", "Model capability has ended.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, scope.Cancellation);
        await _binding.WaitAsync(linked.Token);
        try
        {
            if (_api is not null) return _api;
            var model = await models.BindAsync(context, linked.Token); linked.Token.ThrowIfCancellationRequested();
            return _api = service.Bind(scope, model, canList, canClassify, canReview);
        }
        catch (ExecutionResourceException ex) { throw new ModelEvaluationException(ex.Code, ex.Message); }
        finally { _binding.Release(); }
    }
    private static void Demand(bool granted, CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); if (!granted) throw new ModelEvaluationException("permission.denied", "Model capability is not granted."); }
    public async Task<OperatorCatalog> ListAsync(CancellationToken ct = default)
    { Demand(canList, ct); return await (await Get(ct)).ListAsync(ct); }
    public async Task<ClassificationReceipt> ClassifyAsync(ClassificationRequest request, CancellationToken ct = default)
    { Demand(canClassify, ct); return await (await Get(ct)).ClassifyAsync(request, ct); }
    public async Task<AcceptanceReceipt> ReviewAsync(AcceptanceRequest request, CancellationToken ct = default)
    { Demand(canReview, ct); return await (await Get(ct)).ReviewAsync(request, ct); }
    public async Task<JsonElement> CallAsync(string method, JsonElement arguments, CancellationToken ct)
    {
        try
        {
            if (arguments.ValueKind != JsonValueKind.Object) throw new JsonException();
            switch (method)
            {
                case "operators.model.list":
                    _ = arguments.Deserialize<Empty>(Json) ?? throw new JsonException(); return PluginJson.Value(await ListAsync(ct));
                case "operators.text.classify":
                    return PluginJson.Value(await ClassifyAsync(arguments.Deserialize<ClassificationRequest>(Json) ?? throw new JsonException(), ct));
                case "operators.delivery.review":
                    return PluginJson.Value(await ReviewAsync(arguments.Deserialize<AcceptanceRequest>(Json) ?? throw new JsonException(), ct));
                default: throw new PluginException("capability.unsupported", "core_callback", "Unknown model evaluation method.");
            }
        }
        catch (JsonException) { throw new PluginException("request.invalid", "core_callback", "Invalid or unknown evaluation request fields."); }
        catch (ModelEvaluationException ex) { throw new PluginException(ex.Code, "core_callback", ex.Message, "unknown"); }
    }
    private sealed record Empty;
    public ValueTask DisposeAsync() { scope.Dispose(); return ValueTask.CompletedTask; }
}
