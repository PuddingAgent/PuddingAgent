using PuddingCode.Models;

namespace PuddingCode.Abstractions;

public interface ISwarmMessageHub
{
    Task PostAsync(
        string from,
        string to,
        string type,
        string content,
        SwarmMessagePriority priority = SwarmMessagePriority.Normal,
        Dictionary<string, string>? metadata = null,
        CancellationToken ct = default);

    Task BroadcastAsync(
        string from,
        string type,
        string content,
        SwarmMessagePriority priority = SwarmMessagePriority.Normal,
        Dictionary<string, string>? metadata = null,
        CancellationToken ct = default);

    Task<IReadOnlyList<SwarmMessage>> ReadInboxAsync(bool clear = true, CancellationToken ct = default);
}

