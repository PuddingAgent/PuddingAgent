using System.Text.Json;
using Pudding.Api.Contracts;
using Pudding.Api.Sdk;
using Pudding.ToolPlugins.Contracts;

namespace Pudding.ToolPlugins.AgentSdk;

public static class PluginOperators
{
    public static OperatorsClient Operators(this IPluginContext context) => new(new Proxy(context));
    private sealed class Proxy(IPluginContext context) : IOperatorApi
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { RespectRequiredConstructorParameters = true };
        public async Task<OperatorCatalog> ListAsync(CancellationToken ct = default)
        {
            var result = await Call<OperatorCatalog>("operators.list", new { }, ct);
            if (result.Items.IsDefault || result.Items.Any(d => d is null || d.UsesModel || d.Primitive != "score"
                || string.IsNullOrWhiteSpace(d.SceneKey) || string.IsNullOrWhiteSpace(d.InputSchema)
                || d.InputVersion < 1 || string.IsNullOrWhiteSpace(d.ImplementationVersion) || d.MaxInputBytes is < 1 or > 65536)) throw Invalid();
            return result;
        }
        public async Task<OperatorScoreReceipt> ScoreAsync(OperatorScoreRequest request, CancellationToken ct = default)
        {
            var result = await Call<OperatorScoreReceipt>("operators.skill-value.score", request, ct);
            if (result.Scope != new OperatorScopeRef(context.Scope.WorkspaceId, context.Scope.AgentId, context.Scope.SessionId)
                || result.CallId != request.Options.CallId || result.Value?.Provenance?.SceneKey != request.SceneKey
                || result.InputOrigin != "caller-supplied" || string.IsNullOrEmpty(result.EvaluationId)
                || string.IsNullOrEmpty(result.InputFingerprint) || result.Value.Provenance.ModelId is not null
                || result.Value.Availability is not (ScoreAvailability.Valid or ScoreAvailability.InsufficientData or ScoreAvailability.Degraded)
                || (result.Value.Availability == ScoreAvailability.Valid
                    ? result.Value.Score is null || !double.IsFinite(result.Value.Score.Value)
                    : result.Value.Score is not null)) throw Invalid();
            return result;
        }
        private async Task<T> Call<T>(string method, object args, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (context.Core is not IPluginAgentApiTransport transport) throw new OperatorApiException("capability.unsupported", "Core API 1.2 operator transport is required.");
            try
            {
                var value = await transport.InvokeAgentApiAsync(method, JsonSerializer.SerializeToElement(args, Json), ct);
                return value.Deserialize<T>(Json) ?? throw Invalid();
            }
            catch (PluginException ex) { throw new OperatorApiException(ex.Failure.Code, ex.Failure.Message); }
            catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException) { throw Invalid(); }
        }
        private static OperatorApiException Invalid() => new("transport.invalid_response", "The operator result could not be confirmed; no retry was performed.");
    }
}
