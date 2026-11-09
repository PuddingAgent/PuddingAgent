using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Pudding.Api.Contracts;

namespace Pudding.Api.Sdk;

/// <summary>Uses a caller-owned authenticated HttpClient; never automatically retries submissions.</summary>
public sealed class HttpAgentWorkApi(HttpClient client) : IAgentWorkApi
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { RespectRequiredConstructorParameters = true };
    private static string Route(AgentRef agent) => $"api/workspaces/{Segment(agent.WorkspaceId, 64)}/agents/{Segment(agent.AgentId, 128)}/work";
    private static string Segment(string value, int max)
    {
        // HttpClient normalizes dot segments before sending. Reject unsafe IDs before URI construction.
        if (string.IsNullOrEmpty(value) || value.Length > max || value is "." or ".."
            || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.')))
            throw new AgentWorkException("request.invalid", "Workspace, Agent and operation IDs must be safe non-empty identifiers.");
        return value;
    }
    public async Task<AgentWorkReceipt> SubmitAsync(AgentRef agent, AgentWorkRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var message = new HttpRequestMessage(HttpMethod.Post, Route(agent)) { Content = JsonContent.Create(request) };
        var receipt = await SendAsync<AgentWorkReceipt>(message, true, ct) ?? throw Invalid(true);
        Validate(receipt, agent, request.OperationId, true);
        return receipt;
    }
    public async Task<AgentWorkSnapshot?> GetAsync(AgentRef agent, string operationId, CancellationToken ct = default)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, $"{Route(agent)}/{Segment(operationId, 128)}");
        var snapshot = await SendAsync<AgentWorkSnapshot>(message, false, ct);
        if (snapshot is null) return null;
        Validate(snapshot.Receipt, agent, operationId, false);
        if (!Enum.IsDefined(snapshot.Status) || snapshot.IsTerminal != (snapshot.Terminal is not null)
            || snapshot.Terminal is { } terminal && (terminal.Sequence <= snapshot.Receipt.AcceptedSequence
                || string.IsNullOrEmpty(terminal.EventId) || string.IsNullOrEmpty(terminal.RunId))) throw Invalid(false);
        return snapshot;
    }

    private async Task<T?> SendAsync<T>(HttpRequestMessage request, bool mutation, CancellationToken ct) where T : class
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            using var response = await client.SendAsync(request, ct);
            if (!mutation && response.StatusCode == HttpStatusCode.NoContent) return null;
            if (!response.IsSuccessStatusCode)
            {
                AgentWorkError? error = null;
                try { error = await response.Content.ReadFromJsonAsync<AgentWorkError>(Json, ct); }
                catch (JsonException) { }
                if (!string.IsNullOrEmpty(error?.Code) && error.Message is not null && error.EffectOutcome is "unknown" or "not_applied")
                    throw new AgentWorkException(error.Code, error.Message, error.EffectOutcome);
                var status = (int)response.StatusCode;
                throw new AgentWorkException(status is 401 or 403 ? "permission.denied" : "transport.failed",
                    $"Core returned HTTP {status}.", mutation && status >= 500 ? "unknown" : "not_applied");
            }
            return await response.Content.ReadFromJsonAsync<T>(Json, ct) ?? throw Invalid(mutation);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or OperationCanceledException)
        {
            if (!mutation && ex is OperationCanceledException) throw;
            throw Invalid(mutation);
        }
    }

    private static void Validate(AgentWorkReceipt? receipt, AgentRef agent, string operation, bool mutation)
    {
        if (receipt is null || receipt.Agent != agent || receipt.OperationId != operation
            || string.IsNullOrEmpty(receipt.WorkId) || string.IsNullOrEmpty(receipt.ConversationId)
            || string.IsNullOrEmpty(receipt.InputMessageId) || string.IsNullOrEmpty(receipt.TurnId)
            || string.IsNullOrEmpty(receipt.CommandId) || receipt.AcceptedSequence <= 0) throw Invalid(mutation);
    }
    private static AgentWorkException Invalid(bool mutation) => new(mutation ? "submission.unknown" : "transport.invalid_response",
        mutation ? "The response could not be confirmed. Query or retry the same operation ID and text." : "Core returned an invalid work response.",
        mutation ? "unknown" : "not_applied");
}
