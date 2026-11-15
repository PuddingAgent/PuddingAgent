using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace PuddingSpeech;

/// <summary>Provider-neutral PCM16 mono/16 kHz streaming port. Input completion means finish; cancellation means discard.</summary>
public interface IStreamingSpeechRecognizer
{
    IAsyncEnumerable<TranscriptSegment> RecognizePcmAsync(IAsyncEnumerable<ReadOnlyMemory<byte>> audio,
        string? language = null, CancellationToken ct = default);
}

public sealed record RealtimeSpeechInput(byte[]? Audio = null, string? Control = null);
public sealed record RealtimeSpeechEvent(string Type, int? SentenceId = null, string? Text = null,
    bool? IsFinal = null, string? Transcript = null, string? ConfirmedText = null,
    string? Code = null, string? Message = null, string? ErrorId = null);

public interface IRealtimeSpeechPeer
{
    Task<RealtimeSpeechInput?> ReceiveAsync(CancellationToken ct);
    Task SendAsync(RealtimeSpeechEvent message, CancellationToken ct);
}

/// <summary>Owns one live turn: bounded PCM, sentence replacement, final draining, cancellation and terminal failure.</summary>
public sealed class RealtimeAsrSession
{
    public async Task RunAsync(IRealtimeSpeechPeer peer, IStreamingSpeechRecognizer recognizer, CancellationToken ct = default)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lifetime.CancelAfter(TimeSpan.FromSeconds(150));
        var frames = Channel.CreateBounded<ReadOnlyMemory<byte>>(new BoundedChannelOptions(8)
        { SingleReader = true, SingleWriter = true });
        var sentences = new SortedDictionary<int, TranscriptSegment>();
        var ready = 0;
        var finished = false;
        var discarded = false;
        Exception? inputFailure = null;
        var receiver = ReceiveInputAsync();
        try
        {
            await foreach (var segment in recognizer.RecognizePcmAsync(ReadFrames(), ct: lifetime.Token))
            {
                if (segment.Id < 0 || segment.Text.Length > 65536) throw SpeechFailure.Protocol();
                if (sentences.TryGetValue(segment.Id, out var previous) && (previous.IsFinal || previous == segment)) continue;
                sentences[segment.Id] = segment;
                await peer.SendAsync(new("transcript", segment.Id, segment.Text, segment.IsFinal,
                    string.Concat(sentences.Values.Select(s => s.Text)), ConfirmedText()), lifetime.Token);
            }
            if (!finished) throw SpeechFailure.Protocol();
            var text = ConfirmedText();
            if (string.IsNullOrWhiteSpace(text)) throw new SpeechFailure("voice_no_speech", "没有识别到声音，请重新录音。", 400);
            await peer.SendAsync(new("completed", Text: text), lifetime.Token);
        }
        catch (Exception error) when (!ct.IsCancellationRequested && !discarded)
        {
            var failure = inputFailure as SpeechFailure ?? error as SpeechFailure
                ?? (error is OperationCanceledException
                    ? new SpeechFailure("voice_timeout", "实时识别超时，请保留草稿后重试。", 504, true)
                    : new SpeechFailure("voice_connection_failed", "实时语音连接中断，请重试。", 503, true));
            // A failed stream retains confirmed text only; cancellation never commits any text.
            await peer.SendAsync(new("failed", ConfirmedText: ConfirmedText(), Code: failure.Code,
                Message: failure.Message, ErrorId: Guid.NewGuid().ToString("N")[..12]), ct);
        }
        catch (OperationCanceledException) when (discarded || ct.IsCancellationRequested) { }
        finally
        {
            await lifetime.CancelAsync();
            frames.Writer.TryComplete();
            try { await receiver; } catch (Exception) when (lifetime.IsCancellationRequested) { }
        }

        string ConfirmedText() => string.Concat(sentences.Values.Where(s => s.IsFinal).Select(s => s.Text));

        async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadFrames([EnumeratorCancellation] CancellationToken token = default)
        {
            Interlocked.Exchange(ref ready, 1);
            await peer.SendAsync(new("ready"), token);
            await foreach (var frame in frames.Reader.ReadAllAsync(token)) yield return frame;
        }

        async Task ReceiveInputAsync()
        {
            var total = 0;
            try
            {
                while (await peer.ReceiveAsync(lifetime.Token) is { } input)
                {
                    if (input.Control == "cancel") { discarded = true; await lifetime.CancelAsync(); return; }
                    if (input.Control == "finish" && !finished && Volatile.Read(ref ready) == 1)
                    {
                        finished = true; frames.Writer.TryComplete();
                        lifetime.CancelAfter(TimeSpan.FromSeconds(15));
                        continue; // Still receive cancel/disconnect while final results are draining.
                    }
                    if (input.Audio is not { Length: > 0 } audio || input.Control is not null || finished || Volatile.Read(ref ready) == 0
                        || audio.Length > 32000 || audio.Length % 2 != 0)
                        throw new SpeechFailure("voice_invalid_frame", "实时音频或控制消息无效，请重新录音。", 400);
                    total = checked(total + audio.Length);
                    if (total > 16000 * 2 * 120)
                        throw new SpeechFailure("voice_input_too_large", "录音超过 120 秒，请分段输入。", 413);
                    if (!frames.Writer.TryWrite(audio))
                        throw new SpeechFailure("voice_backpressure", "网络传输跟不上录音，请重新录音。", 503, true);
                }
                discarded = true; await lifetime.CancelAsync();
            }
            catch (Exception error) when (!lifetime.IsCancellationRequested)
            { inputFailure = error; frames.Writer.TryComplete(error); await lifetime.CancelAsync(); }
        }
    }
}
