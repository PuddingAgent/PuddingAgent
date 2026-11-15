using System.Collections.Concurrent;
using System.Security.Cryptography;
using PuddingSpeech;

namespace PuddingPlatform.Services;

/// <summary>Short-lived, single-use authentication for browser sockets; credentials never enter a URL.</summary>
public sealed class VoiceStreamTicketStore(TimeProvider? clock = null)
{
    private readonly ConcurrentDictionary<string, (string User, DateTimeOffset Expires)> tickets = new();
    private readonly TimeProvider time = clock ?? TimeProvider.System;

    public string Issue(string user)
    {
        if (string.IsNullOrWhiteSpace(user)) throw new SpeechFailure("voice_unauthorized", "请先登录后使用语音输入。", 401);
        var now = time.GetUtcNow();
        foreach (var entry in tickets) if (entry.Value.Expires <= now) tickets.TryRemove(entry.Key, out _);
        if (tickets.Count >= 1024) throw new SpeechFailure("voice_busy", "语音连接繁忙，请稍后重试。", 503, true);
        var ticket = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        tickets[ticket] = (user, now.AddSeconds(30));
        return ticket;
    }

    public bool TryConsume(string? ticket) => ticket is { Length: 64 }
        && tickets.TryRemove(ticket, out var entry) && entry.Expires > time.GetUtcNow();
}
