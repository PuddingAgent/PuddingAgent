using System.Security.Cryptography;
using System.Text.Json;
using Pudding.Api.Contracts;
using PuddingAgentWork.Contracts;

namespace PuddingAgentWork;

/// <summary>Validation, caller scope and stable operation identity. Storage, scheduling and LLMs belong to adapters.</summary>
public sealed class AgentWorkService(IAgentWorkBackend backend, IAgentWorkAuthorizer authorizer)
{
    public IAgentWorkApi ForCaller(WorkCaller caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (string.IsNullOrWhiteSpace(caller.ActorId) || caller.ActorId.Length > 64)
            throw new ArgumentException("A trusted actor ID of at most 64 characters is required.", nameof(caller));
        return new BoundApi(backend, authorizer, caller);
    }

    private sealed class BoundApi(IAgentWorkBackend backend, IAgentWorkAuthorizer authorizer, WorkCaller caller) : IAgentWorkApi
    {
        public async Task<AgentWorkReceipt> SubmitAsync(AgentRef agent, AgentWorkRequest request, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(request);
            var key = await Key(agent, request.OperationId, WorkAccess.Submit, ct);
            if (string.IsNullOrWhiteSpace(request.Text) || request.Text.Length > 32_768)
                throw new AgentWorkException("request.invalid", "Text must contain 1 to 32768 characters.");
            ct.ThrowIfCancellationRequested();
            try
            {
                var receipt = await backend.SubmitAsync(new(key, request.Text, Hash(new[] { "text-turn-v1", request.Text })), ct);
                ValidateReceipt(key, receipt);
                return receipt;
            }
            catch (AgentWorkException) { throw; }
            catch (Exception)
            {
                // Once handed to an adapter, cancellation/IO failure cannot prove whether acceptance committed.
                throw new AgentWorkException("submission.unknown", "Acceptance could not be confirmed. Query or retry the same operation and text.", "unknown");
            }
        }

        public async Task<AgentWorkSnapshot?> GetAsync(AgentRef agent, string operationId, CancellationToken ct)
        {
            var key = await Key(agent, operationId, WorkAccess.Read, ct);
            var snapshot = await backend.ReadAsync(key, ct);
            if (snapshot is not null) ValidateReceipt(key, snapshot.Receipt);
            return snapshot;
        }

        private async Task<WorkKey> Key(AgentRef agent, string operationId, WorkAccess access, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(agent);
            if (!Identifier(agent.WorkspaceId, 64) || !Identifier(agent.AgentId, 128) || !Identifier(operationId, 128))
                throw new AgentWorkException("request.invalid", "Workspace, Agent and operation IDs must be safe non-empty identifiers.");
            ct.ThrowIfCancellationRequested();
            if (!await authorizer.IsAllowedAsync(caller, agent, access, ct))
                throw new AgentWorkException("permission.denied", "This caller does not have access to this Agent operation.");
            return new(caller, agent, operationId, "work-" + Hash(new[] { "agent-work-v1", caller.ActorId, agent.WorkspaceId, agent.AgentId, operationId })[..59]);
        }
    }

    private static bool Identifier(string? value, int max) => value is { Length: > 0 } && value.Length <= max
        && value is not "." and not ".." && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
    private static string Hash(string[] values) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(values)));
    private static void ValidateReceipt(WorkKey key, AgentWorkReceipt receipt)
    {
        if (receipt.Agent != key.Agent || receipt.OperationId != key.OperationId || receipt.WorkId != key.WorkId
            || string.IsNullOrEmpty(receipt.ConversationId) || string.IsNullOrEmpty(receipt.InputMessageId)
            || string.IsNullOrEmpty(receipt.CommandId) || string.IsNullOrEmpty(receipt.TurnId) || receipt.AcceptedSequence <= 0)
            throw new AgentWorkException("receipt.invalid", "The adapter returned a receipt for an unconfirmed operation.", "unknown");
    }
}
