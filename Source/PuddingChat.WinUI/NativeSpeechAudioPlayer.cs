using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace PuddingChat.WinUI;

/// <summary>UI-owned WinRT audio playback. No files, browser, or Core dependency.</summary>
public sealed class NativeSpeechAudioPlayer(bool muted = false) : ISpeechAudioPlayer
{
    private CancellationTokenSource? _current;
    private MediaPlayer? _active;
    private bool _disposed;
    public bool HasActivePlayback => _active is not null;

    public async Task PlayAsync(SpeechAudio audio, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        audio.Validate(); ct.ThrowIfCancellationRequested();
        _current?.Cancel();
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _current = operation;
        try { await PlayCoreAsync(audio, operation); }
        finally { if (ReferenceEquals(_current, operation)) _current = null; }
    }
    private async Task PlayCoreAsync(SpeechAudio audio, CancellationTokenSource operation)
    {
        using var bytes = new MemoryStream(audio.Bytes, writable: false);
        using var stream = bytes.AsRandomAccessStream();
        using var source = MediaSource.CreateFromStream(stream, audio.MimeType);
        using var player = new MediaPlayer { AutoPlay = false, IsMuted = muted };
        _active = player;
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        player.MediaEnded += OnEnded;
        player.MediaFailed += OnFailed;
        void OnEnded(MediaPlayer sender, object args) => finished.TrySetResult();
        void OnFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args) =>
            finished.TrySetException(new InvalidOperationException("无法播放语音内容。"));
        using var registration = operation.Token.Register(() =>
        {
            try { player.Pause(); }
            catch (Exception) { /* Media failure/disposal can race cancellation; completion still releases the player. */ }
            finished.TrySetCanceled(operation.Token);
        });
        try
        {
            operation.Token.ThrowIfCancellationRequested();
            player.Source = source;
            player.Play();
            await finished.Task;
        }
        finally
        {
            player.MediaEnded -= OnEnded; player.MediaFailed -= OnFailed;
            if (ReferenceEquals(_active, player)) _active = null;
        }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _current?.Cancel(); _active?.Dispose(); _active = null;
    }
}
