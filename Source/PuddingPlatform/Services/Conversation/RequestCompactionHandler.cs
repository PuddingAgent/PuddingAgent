using Microsoft.Extensions.Logging;
using PuddingCode.Platform;

namespace PuddingPlatform.Services.Conversation;

public sealed class RequestCompactionHandler(
    ILogger<RequestCompactionHandler> logger) : IRequestCompactionHandler
{
    public Task<CompactionResult> HandleAsync(
        RequestCompactionCommand command, CancellationToken ct)
    {
        var newId = Guid.NewGuid().ToString("N");
        logger.LogInformation("[Compact] conv={ConvId} → {NewId}", command.ConversationId, newId);
        return Task.FromResult(new CompactionResult(newId));
    }
}
