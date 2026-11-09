using System.Text.Json;
using PuddingCode.Configuration;
using PuddingCode.Platform;
using PuddingModels.Contracts;
using PuddingPlatform.Services.Configuration;

namespace PuddingPlatform.Services;

/// <summary>
/// PA-06 host authorization for the purpose model policy. The Host supplies a principal-backed implementation; when it
/// is absent the adapter fails closed instead of accepting a bare ActorId (AgentId is never a substitute for the caller).
/// </summary>
public interface IAgentModelPolicyAuthorization
{
    ValueTask<bool> AuthorizeReadAsync(AgentProfileScope scope, CancellationToken ct = default);
    ValueTask<bool> AuthorizeWriteAsync(AgentProfileScope scope, long expectedRevision, CancellationToken ct = default);
}

/// <summary>
/// PA-06 06A/06B configuration adapter: reuses the existing Agent manifest as the only authority (same write lock and
/// atomic writer, no second policy file). Reads/writes are denied unless the host registers IAgentModelPolicyAuthorization.
/// </summary>
public sealed partial class WorkspaceAgentFileService : IAgentModelPolicyStore
{
    async ValueTask<AgentConfigurationSnapshot> IAgentModelPolicyStore.ReadAsync(AgentProfileScope scope,
        CancellationToken ct)
    {
        ValidatePolicyScope(scope);
        var authorization = await ResolvePolicyAuthorizationAsync(scope, ct).ConfigureAwait(false);
        if (authorization is null || !await authorization.AuthorizeReadAsync(scope, ct).ConfigureAwait(false))
            throw PolicyForbidden("Reading the model policy requires a host-authorized caller.");
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var manifest = await LoadPolicyManifestAsync(scope, ct).ConfigureAwait(false);
            return AgentModelPolicyMapper.ToSnapshot(manifest.ModelPolicy);
        }
        finally { _writeLock.Release(); }
    }

    async ValueTask<ModelPolicyWriteResult> IAgentModelPolicyStore.WriteAsync(AgentProfileScope scope,
        UpdateModelPolicyCommand command, CancellationToken ct)
    {
        ValidatePolicyScope(scope);
        if (command is null)
            throw new ModelException(ModelErrorCodes.RequestInvalid, "A policy update command is required.");
        var authorization = await ResolvePolicyAuthorizationAsync(scope, ct).ConfigureAwait(false);
        if (authorization is null
            || !await authorization.AuthorizeWriteAsync(scope, command.ExpectedRevision, ct).ConfigureAwait(false))
            throw PolicyForbidden("Writing the model policy requires a host-authorized caller.");
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var manifest = await LoadPolicyManifestAsync(scope, ct).ConfigureAwait(false);
            AgentModelPolicy policy;
            ModelPolicyWriteResult result;
            try
            {
                policy = AgentModelPolicyMapper.Apply(manifest.ModelPolicy, command, out result);
            }
            catch (ModelException ex) when (ex.Code == ModelErrorCodes.ConfigRevisionConflict)
            {
                throw new ModelException(ModelErrorCodes.ConfigRevisionConflict,
                    "The agent configuration revision changed; retry with the current revision.");
            }
            var updated = manifest with
            {
                // Journal/receipt fields stay owned by the configuration adapter; only the policy subtree changes.
                ModelPolicy = policy,
            };
            ct.ThrowIfCancellationRequested();
            try
            {
                await AtomicFileWriter.WriteJsonAsync(
                    Path.Combine(ResolveAgentInstanceRoot(scope.AgentId), "manifest.json"), updated, JsonOptions, ct)
                    .ConfigureAwait(false);
            }
            catch (IOException)
            {
                throw new ModelException(ModelErrorCodes.Unavailable,
                    "The policy write could not be confirmed; retry with the same OperationId and revision.");
            }
            return result;
        }
        finally { _writeLock.Release(); }
    }

    private async ValueTask<AgentInstanceManifest> LoadPolicyManifestAsync(AgentProfileScope scope,
        CancellationToken ct)
    {
        var manifest = await LoadInstanceManifestAsync(scope.AgentId, ct).ConfigureAwait(false)
            ?? throw PolicyForbidden("The agent instance is missing; no model policy can be read or written.");
        if (!string.Equals(manifest.WorkspaceId, scope.WorkspaceId, StringComparison.Ordinal))
            throw PolicyForbidden("The agent belongs to another workspace.");
        return manifest;
    }

    private async ValueTask<IAgentModelPolicyAuthorization?> ResolvePolicyAuthorizationAsync(
        AgentProfileScope scope, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await using var serviceScope = _serviceScopeFactory.CreateAsyncScope();
        return serviceScope.ServiceProvider.GetService(typeof(IAgentModelPolicyAuthorization))
            as IAgentModelPolicyAuthorization;
    }

    private static void ValidatePolicyScope(AgentProfileScope scope)
    {
        if (scope is null || !scope.IsValid)
            throw new ModelException(ModelErrorCodes.RequestInvalid, "A bounded agent profile scope is required.");
    }

    private static ModelException PolicyForbidden(string message) => new(ModelErrorCodes.ScopeForbidden, message);
}
