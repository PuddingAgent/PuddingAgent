using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Pudding.Api.Contracts;

namespace Pudding.Api.Sdk;

/// <summary>Observe/Cancel over a caller-owned authenticated HttpClient. Cancellation reports Requested, never stopped.</summary>
public sealed class HttpAgentWorkControlApi(HttpClient client) : IAgentWorkControlApi
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
        { RespectRequiredConstructorParameters = true };

    public async Task<WorkObservation> ObserveAsync(ObserveWorkRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var message = new HttpRequestMessage(HttpMethod.Get,
            $"{Route(request.Agent)}/{Segment(request.OperationId, 128)}?afterSequence={request.AfterSequence}&maxCount={request.MaxCount}");
        return await SendAsync<WorkObservation>(message, ct, mutation: false).ConfigureAwait(false)
            ?? throw Invalid(false);
    }

    public async Task<CancelWorkReceipt> CancelAsync(CancelWorkRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var message = new HttpRequestMessage(HttpMethod.Post,
            $"{Route(request.Agent)}/{Segment(request.TargetOperationId, 128)}/cancel")
            { Content = JsonContent.Create(request) };
        var receipt = await SendAsync<CancelWorkReceipt>(message, ct, mutation: true).ConfigureAwait(false)
            ?? throw Invalid(true);
        if (receipt.OperationId != request.OperationId || receipt.TargetOperationId != request.TargetOperationId)
            throw Invalid(true);
        return receipt;
    }

    private async Task<T?> SendAsync<T>(HttpRequestMessage request, CancellationToken ct, bool mutation)
        where T : class
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                var code = (int)response.StatusCode is 401 or 403 ? "permission.denied" : "transport.failed";
                throw new AgentWorkControlException(code, $"Core returned HTTP {(int)response.StatusCode}.");
            }
            return await response.Content.ReadFromJsonAsync<T>(Json, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException)
        {
            throw mutation
                ? new AgentWorkControlException("cancel.unknown", "The cancel effect could not be confirmed.")
                : new AgentWorkControlException("transport.failed", "Core returned an invalid observation.");
        }
    }

    private static string Route(AgentRef agent) =>
        $"api/workspaces/{Segment(agent.WorkspaceId, 64)}/agents/{Segment(agent.AgentId, 128)}/work-control";

    private static string Segment(string value, int max)
    {
        if (string.IsNullOrEmpty(value) || value.Length > max || value is "." or ".."
            || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.')))
            throw new AgentWorkControlException("request.invalid",
                "Workspace, Agent and operation IDs must be safe non-empty identifiers.");
        return value;
    }

    private static AgentWorkControlException Invalid(bool mutation) => mutation
        ? new("cancel.unknown", "The cancel response could not be confirmed.")
        : new("transport.invalid_response", "Core returned an invalid observation.");
}

/// <summary>Convenience handle for SDK consumers; binding does not create an Agent or start work.</summary>
public sealed class AgentWorkControlClient(IAgentWorkControlApi api)
{
    public AgentWorkControl For(AgentRef agent) => new(api, agent);
}

public sealed class AgentWorkControl(IAgentWorkControlApi api, AgentRef agent)
{
    public Task<WorkObservation> ObserveAsync(string operationId, long afterSequence = 0, int maxCount = 100,
        CancellationToken ct = default)
        => api.ObserveAsync(new(agent, operationId, afterSequence, maxCount), ct);

    public Task<CancelWorkReceipt> CancelAsync(string operationId, string targetOperationId, long expectedRevision,
        string reason = "user.cancelled", CancellationToken ct = default)
        => api.CancelAsync(new(operationId, agent, targetOperationId, expectedRevision, reason), ct);
}
