using PuddingCode.Models;

namespace PuddingCode.Abstractions;

/// <summary>High-level message fabric entrypoint used by users, agents, connectors, and system jobs.</summary>
public interface IMessageSystem
{
    Task<MessageSendResult> SendAsync(MessageEnvelope envelope, CancellationToken ct = default);
}
