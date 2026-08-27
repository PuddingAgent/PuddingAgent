using Microsoft.Extensions.Logging;
using PuddingCode.Core;
using PuddingCode.Platform;

namespace PuddingPlatform.Services.Conversation;

/// <summary>
/// ADR-059: SubmitTurnHandler — 委托给 IConversationAcceptanceStore。
/// ADR-077: 受理 typed image content part，并在 acceptance 前验证 Artifact 属于当前 Workspace 且可读。
/// </summary>
public sealed class SubmitTurnHandler(
    IConversationAcceptanceStore acceptanceStore,
    IVisualArtifactLocalFileResolver visualArtifactLocalFileResolver,
    ILogger<SubmitTurnHandler> logger) : ISubmitTurnHandler
{
    public async Task<AcceptanceResult> HandleAsync(SubmitTurnCommand command, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command.ConversationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.WorkspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.ClientRequestId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.ClientMessageId);

        if (!string.Equals(command.Recipients.Type, "agent", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException(
                "Only explicit agent recipients are supported. Broadcast is not accepted.");
        if (command.Recipients.AgentIds is null ||
            command.Recipients.AgentIds.Count == 0 ||
            command.Recipients.AgentIds.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException(
                "At least one explicit agent ID is required.",
                nameof(command.Recipients));
        var contentError = ConversationContentValidator.Validate(command.Content);
        if (contentError is not null)
            throw new ArgumentException(contentError, nameof(command.Content));

        await ValidateImageArtifactsAsync(command, ct);

        logger.LogInformation(
            "[SubmitTurn] conv={ConvId} msg={MsgId} agents={Agents}",
            command.ConversationId, command.ClientMessageId,
            string.Join(",", command.Recipients.AgentIds ?? []));

        return await acceptanceStore.AcceptBatchAsync(
            new SubmitTurnRequest
            {
                ClientRequestId = command.ClientRequestId,
                ClientMessageId = command.ClientMessageId,
                Recipients = command.Recipients,
                Content = command.Content,
                Metadata = NormalizeMetadata(
                    command.Metadata,
                    command.IsTrustedGatewayIngress,
                    command.IsTrustedMessageFabricIngress),
            },
            command.WorkspaceId,
            command.ConversationId,
            command.UserId,
            ct);
    }

    /// <summary>SubmitTurn 受理前验证全部图片 Artifact 可读且属于当前 Workspace（ADR-077 §5.1.3）。</summary>
    private async Task ValidateImageArtifactsAsync(SubmitTurnCommand command, CancellationToken ct)
    {
        foreach (var part in command.Content)
        {
            if (!string.Equals(part.Type, "image", StringComparison.OrdinalIgnoreCase))
                continue;

            var localFile = await visualArtifactLocalFileResolver.ResolveLocalFileAsync(
                command.WorkspaceId,
                part.ArtifactId!,
                ct);
            if (localFile is null)
                throw new VisionPipelineException(
                    VisionErrorCodes.ArtifactMissing,
                    $"Image artifact {part.ArtifactId} does not exist or is not readable in workspace " +
                    $"{command.WorkspaceId}; upload it via the vision artifact API before submitting the turn.");
        }
    }

    private static IReadOnlyDictionary<string, string>? NormalizeMetadata(
        IReadOnlyDictionary<string, string>? metadata,
        bool isTrustedGatewayIngress,
        bool isTrustedMessageFabricIngress)
    {
        if (metadata is null)
            return metadata;

        var filtered = metadata
            .Where(pair =>
                (isTrustedGatewayIngress
                 || !pair.Key.StartsWith(
                     "gateway_",
                     StringComparison.OrdinalIgnoreCase))
                && (isTrustedMessageFabricIngress
                    || !pair.Key.StartsWith(
                        "message_fabric_",
                        StringComparison.OrdinalIgnoreCase)))
            .ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.Ordinal);
        return filtered.Count > 0 ? filtered : null;
    }
}
