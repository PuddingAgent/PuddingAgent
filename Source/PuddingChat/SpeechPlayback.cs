namespace PuddingChat;

public sealed record SpeechRequest(RoleKey Role, string MessageId, string Text)
{
    public const int MaxCharacters = 10_000;
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Role);
        ArgumentException.ThrowIfNullOrWhiteSpace(Role.WorkspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(Role.AgentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(MessageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(Text);
        if (Text.Length > MaxCharacters) throw new ArgumentException("朗读内容不能超过 10,000 个字符。", nameof(Text));
    }
}

public sealed record SpeechAudio(byte[] Bytes, string MimeType)
{
    public const int MaxBytes = 30 * 1024 * 1024;
    public void Validate()
    {
        if (Bytes is not { Length: > 0 and <= MaxBytes }) throw new InvalidDataException("语音内容为空或超过 30 MiB。");
        if (MimeType is not ("audio/wav" or "audio/mpeg")) throw new InvalidDataException("不支持的语音格式。");
    }
}

public interface IChatSpeechClient
{
    Task<SpeechAudio> SynthesizeAsync(SpeechRequest request, CancellationToken ct);
}

/// <summary>Play completes on media end. Cancellation must stop playback and release audio resources.</summary>
public interface ISpeechAudioPlayer : IDisposable
{
    Task PlayAsync(SpeechAudio audio, CancellationToken ct);
}

public enum SpeechPlaybackPhase { Idle, Synthesizing, Playing, Failed }
public sealed record SpeechPlaybackState(SpeechPlaybackPhase Phase, SpeechRequest? Request = null, string? Error = null);

/// <summary>
/// One playback lane for a chat workspace. Call Speak/Stop/Dispose from the owning UI context;
/// Core synthesis and device callbacks complete asynchronously without owning presentation state.
/// </summary>
public sealed class SpeechPlaybackSession(IChatSpeechClient client, ISpeechAudioPlayer player) : IDisposable
{
    private CancellationTokenSource? _current;
    private long _generation;
    private bool _disposed;
    public SpeechPlaybackState State { get; private set; } = new(SpeechPlaybackPhase.Idle);
    public event EventHandler? Changed;

    public async Task SpeakAsync(SpeechRequest request, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        request.Validate();
        Stop();
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _current = operation;
        var generation = _generation;
        SetState(new(SpeechPlaybackPhase.Synthesizing, request));
        try
        {
            operation.Token.ThrowIfCancellationRequested();
            var audio = await client.SynthesizeAsync(request, operation.Token).WaitAsync(operation.Token);
            if (generation != _generation || _disposed) return;
            operation.Token.ThrowIfCancellationRequested();
            audio.Validate();
            SetState(new(SpeechPlaybackPhase.Playing, request));
            await player.PlayAsync(audio, operation.Token).WaitAsync(operation.Token);
            if (generation == _generation && !_disposed) SetState(new(SpeechPlaybackPhase.Idle));
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        { if (generation == _generation && !_disposed) SetState(new(SpeechPlaybackPhase.Idle)); }
        catch (Exception)
        {
            if (generation == _generation && !_disposed)
                SetState(new(SpeechPlaybackPhase.Failed, request, "朗读失败，请检查语音设置后重试。"));
        }
        finally { if (ReferenceEquals(_current, operation)) _current = null; }
    }

    public void Stop()
    {
        _generation++;
        var current = _current; _current = null;
        current?.Cancel();
        SetState(new(SpeechPlaybackPhase.Idle));
    }
    private void SetState(SpeechPlaybackState state) { State = state; Changed?.Invoke(this, EventArgs.Empty); }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; Stop(); player.Dispose(); Changed = null;
    }
}
