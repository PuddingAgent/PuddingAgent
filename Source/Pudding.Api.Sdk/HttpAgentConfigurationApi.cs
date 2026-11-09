using System.Net.Http.Json;
using System.Text.Json;
using Pudding.Api.Contracts;

namespace Pudding.Api.Sdk;

/// <summary>Supply an authenticated HttpClient with the Core base URI. No automatic mutation retries.</summary>
public sealed class HttpAgentConfigurationApi(HttpClient client) : IAgentConfigurationApi
{
    private static readonly JsonSerializerOptions ResponseJson = new(JsonSerializerDefaults.Web)
    { RespectRequiredConstructorParameters = true };
    private static string Route(AgentRef agent) => $"api/workspaces/{Uri.EscapeDataString(agent.WorkspaceId)}/agents/{Uri.EscapeDataString(agent.AgentId)}/configuration";
    public async Task<AgentConfigurationSnapshot> GetAsync(AgentRef agent, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Route(agent));
        var snapshot = await SendAsync<AgentConfigurationSnapshot>(request, mutation: false, ct);
        if (snapshot.Agent != agent || string.IsNullOrEmpty(snapshot.Revision))
            throw new ConfigurationApiException("transport.invalid_response", "Core returned a different or incomplete configuration snapshot.");
        return snapshot;
    }
    public async Task<ConfigurationChangeReceipt> UpdateAsync(AgentRef agent, AgentConfigurationPatch patch,
        ConfigurationUpdateOptions options, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, Route(agent))
        { Content = JsonContent.Create(new ConfigurationUpdateRequest(patch, options)) };
        var receipt = await SendAsync<ConfigurationChangeReceipt>(request, mutation: true, ct);
        if (receipt.Agent != agent || receipt.OperationId != options.OperationId || receipt.PreviousRevision != options.ExpectedRevision
            || string.IsNullOrEmpty(receipt.PersistedRevision))
            throw new ConfigurationApiException("operation.unknown", "The receipt does not match this request; retry the exact request with the same OperationId.", "unknown");
        return receipt;
    }
    public async Task<ConfigurationChangePage> ReadChangesAsync(AgentRef agent, long afterSequence = 0, int limit = 100, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{Route(agent)}/changes?afterSequence={afterSequence}&limit={limit}");
        return await SendAsync<ConfigurationChangePage>(request, mutation: false, ct);
    }
    private async Task<T> SendAsync<T>(HttpRequestMessage request, bool mutation, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                ConfigurationApiError? error = null;
                try { error = await response.Content.ReadFromJsonAsync<ConfigurationApiError>(ResponseJson, ct); }
                catch (JsonException) { }
                if (error?.Code is not null)
                    throw new ConfigurationApiException(error.Code, error.Message, error.EffectOutcome);
                var code = (int)response.StatusCode switch { 401 or 403 => "permission.denied", 404 => "resource.not_found", _ => "transport.failed" };
                throw new ConfigurationApiException(code, $"Core returned HTTP {(int)response.StatusCode}.", mutation && (int)response.StatusCode >= 500 ? "unknown" : "none");
            }
            return await response.Content.ReadFromJsonAsync<T>(ResponseJson, ct)
                ?? throw new JsonException("Core returned an empty response.");
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
        {
            if (!mutation) throw;
            throw new ConfigurationApiException("operation.unknown", "The response could not be confirmed. Retry the exact request with the same OperationId.", "unknown");
        }
    }
}
