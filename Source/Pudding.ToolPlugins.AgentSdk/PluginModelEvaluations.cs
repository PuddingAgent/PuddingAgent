using System.Text.Json;
using Pudding.Api.Contracts;
using Pudding.Api.Sdk;
using Pudding.ToolPlugins.Contracts;

namespace Pudding.ToolPlugins.AgentSdk;

public static class PluginModelEvaluations
{
    public static ModelEvaluationsClient ModelEvaluations(this IPluginContext context) => new(new Proxy(context));
    private sealed class Proxy(IPluginContext context) : IModelEvaluationApi
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { RespectRequiredConstructorParameters = true };
        public async Task<OperatorCatalog> ListAsync(CancellationToken ct = default)
        {
            var result = await Call<OperatorCatalog>("operators.model.list", new { }, ct);
            if (result.Items.IsDefault || result.Items.Any(d => d is null || !d.UsesModel || d.Primitive is not ("classify" or "judge"))) throw Invalid();
            return result;
        }
        public async Task<ClassificationReceipt> ClassifyAsync(ClassificationRequest request, CancellationToken ct = default)
        {
            var result = await Call<ClassificationReceipt>("operators.text.classify", request, ct);
            Validate(result.Provenance, request.Options.CallId, "text.classification");
            if (!Confidence(result.Confidence) || result.Availability is not (EvaluationAvailability.Valid or EvaluationAvailability.Abstain or EvaluationAvailability.Degraded)
                || (result.Availability == EvaluationAvailability.Valid ? result.Label is null || !request.Input.Categories.Any(c => c.Id == result.Label) : result.Label is not null)) throw Invalid();
            return result;
        }
        public async Task<AcceptanceReceipt> ReviewAsync(AcceptanceRequest request, CancellationToken ct = default)
        {
            var result = await Call<AcceptanceReceipt>("operators.delivery.review", request, ct);
            Validate(result.Provenance, request.Options.CallId, "delivery.acceptance");
            if (!Confidence(result.Confidence) || result.Availability is not (EvaluationAvailability.Valid or EvaluationAvailability.Abstain or EvaluationAvailability.Degraded)
                || result.Checks.IsDefault || (result.Availability == EvaluationAvailability.Valid ? result.Decision is not (AcceptanceDecision.Accept or AcceptanceDecision.Reject) : result.Decision != AcceptanceDecision.Abstain)) throw Invalid();
            if (result.Availability != EvaluationAvailability.Degraded)
            {
                if (result.Checks.Length != request.Input.Criteria.Length
                    || result.Checks.Any(c => c is null || !request.Input.Criteria.Any(r => r.Id == c.CriterionId)
                        || c.Outcome is not (CriterionOutcome.Supported or CriterionOutcome.NotSupported or CriterionOutcome.Unknown)
                        || c.EvidenceIds.IsDefault || c.EvidenceIds.Any(id => !request.Input.Evidence.Any(e => e.Id == id))
                        || c.Outcome != CriterionOutcome.Unknown && c.EvidenceIds.Length == 0)
                    || result.Checks.Select(c => c.CriterionId).Distinct(StringComparer.Ordinal).Count() != request.Input.Criteria.Length) throw Invalid();
                var decision = result.Checks.Any(c => c.Outcome == CriterionOutcome.NotSupported) ? AcceptanceDecision.Reject
                    : result.Checks.All(c => c.Outcome == CriterionOutcome.Supported) ? AcceptanceDecision.Accept : AcceptanceDecision.Abstain;
                if (result.Decision != decision) throw Invalid();
            }
            return result;
        }
        private void Validate(ModelEvaluationProvenance p, string call, string scene)
        {
            if (p is null || p.Scope != new OperatorScopeRef(context.Scope.WorkspaceId, context.Scope.AgentId, context.Scope.SessionId)
                || p.CallId != call || p.SceneKey != scene || string.IsNullOrEmpty(p.LeaseId) || string.IsNullOrEmpty(p.ParentRunId)
                || p.Usage is null || p.Usage.InputTokens < 0 || p.Usage.OutputTokens < 0 || p.Usage.CacheHitTokens < 0
                || p.Usage.CacheHitTokens > p.Usage.InputTokens || p.Usage.Cost < 0 || p.ConfidenceKind != "ModelSelfReported") throw Invalid();
        }
        private async Task<T> Call<T>(string method, object args, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (context.Core is not IPluginAgentApiTransport transport) throw new ModelEvaluationException("capability.unsupported", "Core API 1.3 model evaluation transport is required.");
            try { return (await transport.InvokeAgentApiAsync(method, JsonSerializer.SerializeToElement(args, Json), ct)).Deserialize<T>(Json) ?? throw Invalid(); }
            catch (PluginException ex) { throw new ModelEvaluationException(ex.Failure.Code, ex.Failure.Message); }
            catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException) { throw Invalid(); }
        }
        private static ModelEvaluationException Invalid() => new("transport.invalid_response", "Model evaluation could not be confirmed; no retry was performed.");
        private static bool Confidence(double? value) => value is null || double.IsFinite(value.Value) && value is >= 0 and <= 1;
    }
}
