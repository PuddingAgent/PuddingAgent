using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PuddingSlack;

/// <summary>Single-owner durable inbox and send journal. Paths and retention are owned by the caller.</summary>
public sealed class SlackStore : IDisposable
{
    private readonly string _root;
    private readonly FileStream _lease;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly object _inboxGate = new();
    private readonly int _pendingLimit;
    private long _suppressedBotMessages;
    public long SuppressedBotMessages => Interlocked.Read(ref _suppressedBotMessages);
    public SlackStore(string root, int pendingLimit = 10000)
    {
        _root = Path.GetFullPath(root);
        _pendingLimit = pendingLimit;
        Directory.CreateDirectory(_root);
        _lease = new FileStream(Path.Combine(_root, ".owner.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        foreach (var name in new[] { "pending", "completed", "outgoing", "bot-threads", "bot-rate" }) Directory.CreateDirectory(Path.Combine(_root, name));
    }

    // No ACK is allowed until this returns. Failure (disk full, unavailable, capacity) must propagate.
    public void Enqueue(SlackInboundMessage message)
    {
        lock (_inboxGate)
        {
            var pending = Entry("pending", message.MessageKey);
            if (File.Exists(pending) || File.Exists(Entry("completed", message.MessageKey))) return;
            if (Directory.EnumerateFiles(Path.Combine(_root, "pending"), "*.json").Take(_pendingLimit).Count() >= _pendingLimit)
                throw new IOException("slack_inbox_capacity");
            if (!AllowBotTurn(message))
            {
                // An ACKed suppression must remain suppressed even if Slack redelivers after a human resets the budget.
                Write(Entry("completed", message.MessageKey), new { message.MessageKey, Suppressed = true, CompletedAt = DateTimeOffset.UtcNow });
                Interlocked.Increment(ref _suppressedBotMessages);
                return;
            }
            Write(pending, message);
        }
    }

    private bool AllowBotTurn(SlackInboundMessage message)
    {
        if (message.IsDirectMessage) return !message.IsBot;
        var path = Entry("bot-threads", message.ConversationKey);
        var state = File.Exists(path) ? Read<BotThreadState>(path) : new(0, 0);
        if (!decimal.TryParse(message.MessageTs, System.Globalization.NumberStyles.AllowDecimalPoint,
            System.Globalization.CultureInfo.InvariantCulture, out var timestamp)) return !message.IsBot;
        if (!message.IsBot)
        {
            if (timestamp > state.LastHumanTimestamp) Write(path, new BotThreadState(timestamp, 0));
            return true;
        }
        if (state.BotTurns >= 1) return false;
        var ratePath = Entry("bot-rate", $"{message.TeamId}/{message.ChannelId}");
        var now = DateTimeOffset.UtcNow;
        var rate = File.Exists(ratePath) ? Read<BotRateState>(ratePath) : new(now, 0);
        if (now >= rate.WindowStarted.AddMinutes(1)) rate = new(now, 0);
        if (rate.Count >= 3) return false;
        // Persist budgets before enqueue: a crash can consume credit, but cannot permit an extra bot turn.
        Write(ratePath, rate with { Count = rate.Count + 1 });
        Write(path, state with { BotTurns = state.BotTurns + 1 });
        return true;
    }
    public IReadOnlyList<SlackInboundMessage> Pending(int count = 50, IEnumerable<string>? deferredMessageKeys = null)
    {
        var deferred = deferredMessageKeys?.Select(Hash).ToHashSet(StringComparer.Ordinal);
        return Directory.EnumerateFiles(Path.Combine(_root, "pending"), "*.json")
            .Where(path => !File.Exists(Path.Combine(_root, "completed", Path.GetFileName(path)))
                && deferred?.Contains(Path.GetFileNameWithoutExtension(path)) != true)
            .OrderBy(File.GetLastWriteTimeUtc).Take(count)
            .Select(path => Read<SlackInboundMessage>(path)).ToArray();
    }
    public void Complete(SlackInboundMessage message)
    {
        lock (_inboxGate)
        {
            // Keep only a tombstone, not user text. Write tombstone before removing the pending body.
            Write(Entry("completed", message.MessageKey), new { message.MessageKey, CompletedAt = DateTimeOffset.UtcNow });
            File.Delete(Entry("pending", message.MessageKey));
        }
    }

    public async Task<SlackSendReceipt> SendAsync(string operationId, SlackPost post,
        Func<SlackPost, CancellationToken, Task<SlackSendReceipt>> send, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        await _sendGate.WaitAsync(ct);
        try
        {
            var path = Entry("outgoing", operationId);
            var fingerprint = Hash(JsonSerializer.Serialize(post));
            if (File.Exists(path))
            {
                var old = Read<SendEntry>(path);
                if (old.Fingerprint != fingerprint) return new(SlackSendStatus.Rejected, "operation_payload_conflict");
                // A crash between starting HTTP and saving its response is an unknown outcome, never an automatic resend.
                if (old.Receipt.Status != SlackSendStatus.Retryable || old.Receipt.RetryAt > DateTimeOffset.UtcNow)
                    return old.Receipt;
            }
            ct.ThrowIfCancellationRequested();
            Write(path, new SendEntry(fingerprint, new(SlackSendStatus.Unknown, "send_started_without_receipt")));
            SlackSendReceipt receipt;
            try { receipt = await send(post, ct); }
            catch (Exception) { receipt = new(SlackSendStatus.Unknown, "send_interrupted"); }
            Write(path, new SendEntry(fingerprint, receipt));
            return receipt;
        }
        finally { _sendGate.Release(); }
    }
    public SlackSendReceipt? Receipt(string operationId)
    {
        var path = Entry("outgoing", operationId);
        return File.Exists(path) ? Read<SendEntry>(path).Receipt : null;
    }
    private string Entry(string directory, string key) => Path.Combine(_root, directory, Hash(key) + ".json");
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllBytes(path))
        ?? throw new IOException("slack_store_invalid_record");
    private static void Write<T>(string path, T value)
    {
        var temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            JsonSerializer.Serialize(stream, value);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temp, path, overwrite: true);
    }
    public void Dispose() { _lease.Dispose(); _sendGate.Dispose(); }
    private sealed record SendEntry(string Fingerprint, SlackSendReceipt Receipt);
    private sealed record BotThreadState(decimal LastHumanTimestamp, int BotTurns);
    private sealed record BotRateState(DateTimeOffset WindowStarted, int Count);
}
