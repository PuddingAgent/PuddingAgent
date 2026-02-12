using PuddingCode.Models;

namespace PuddingCode.Abstractions;

/// <summary>
/// Provider-level ASR adapter. Implementations encapsulate vendor WebSocket protocols,
/// audio frame upload, VAD/manual turn control, and provider error mapping.
/// </summary>
public interface IAsrProvider
{
    string Provider { get; }

    Task StartAsync(VoiceRecognitionRequest request, CancellationToken ct = default);

    Task SendAudioAsync(VoiceAudioFrame frame, CancellationToken ct = default);

    Task CommitAsync(string sessionId, CancellationToken ct = default);

    Task FinishAsync(string sessionId, CancellationToken ct = default);

    IAsyncEnumerable<VoiceRecognitionStreamEvent> ReadEventsAsync(
        string sessionId,
        CancellationToken ct = default);
}
