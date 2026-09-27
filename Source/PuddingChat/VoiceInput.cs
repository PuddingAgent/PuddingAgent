namespace PuddingChat;

public sealed record RecordedSpeech(byte[] Bytes)
{
    public const int MaxBytes = 8 * 1024 * 1024;
    public static readonly TimeSpan MaxDuration = TimeSpan.FromMinutes(2);
    public void Validate()
    {
        if (Bytes is not { Length: > 0 and <= MaxBytes })
            throw new InvalidDataException("录音为空或超过 8 MiB。");
    }
}

/// <summary>Open only on explicit user action. Implementations enforce duration/size limits and return WAV.</summary>
public interface IVoiceCapture
{
    Task<IVoiceRecording> OpenAsync(CancellationToken ct);
}

/// <summary>Finish returns finalized WAV bytes. Dispose releases the microphone, including after cancellation.</summary>
public interface IVoiceRecording : IAsyncDisposable
{
    Task<RecordedSpeech> FinishAsync(CancellationToken ct);
}

public interface IChatTranscriptionClient
{
    Task<string> TranscribeAsync(RoleKey role, RecordedSpeech audio, CancellationToken ct);
}

public sealed record VoiceDraftAnchor(RoleKey Role, long SelectionGeneration, string OriginalText)
{
    public static VoiceDraftAnchor Capture(ChatSelection selection) => new(
        selection.Role ?? throw new InvalidOperationException("请先选择角色。"), selection.Generation, selection.Draft);
}

public sealed record VoiceDraftResult(VoiceDraftAnchor Anchor, string Text)
{
    // The caller can offer Text for explicit insertion when this guard rejects a changed draft.
    public bool TryAppendTo(ChatSelection selection)
    {
        if (selection.Role != Anchor.Role || selection.Generation != Anchor.SelectionGeneration
            || selection.Draft != Anchor.OriginalText || string.IsNullOrWhiteSpace(Text)) return false;
        selection.Draft = string.IsNullOrEmpty(Anchor.OriginalText) ? Text : Anchor.OriginalText + Environment.NewLine + Text;
        return true;
    }
}

public enum VoiceInputPhase { Idle, Opening, Recording, Finalizing, Transcribing, Cancelling, Completed, Failed }
public sealed record VoiceInputState(VoiceInputPhase Phase, VoiceDraftAnchor? Anchor = null,
    VoiceDraftResult? Result = null, string? Error = null);

/// <summary>UI-context owned capture/transcription lane, independent of the Agent execution state machine.</summary>
public sealed class VoiceInputSession(IVoiceCapture capture, IChatTranscriptionClient client) : IAsyncDisposable
{
    private CancellationTokenSource? _operation;
    private TaskCompletionSource? _finish;
    private Task? _running;
    private bool _disposed;
    public VoiceInputState State { get; private set; } = new(VoiceInputPhase.Idle);
    public event EventHandler? Changed;

    public Task StartAsync(VoiceDraftAnchor anchor, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(anchor);
        ArgumentNullException.ThrowIfNull(anchor.Role);
        ArgumentNullException.ThrowIfNull(anchor.OriginalText);
        ArgumentException.ThrowIfNullOrWhiteSpace(anchor.Role.WorkspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(anchor.Role.AgentId);
        if (_operation is not null) throw new InvalidOperationException("请等待当前录音或转写结束。");
        _operation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
        return _running = RunAsync(anchor, _operation, _finish.Task);
    }

    public void FinishRecording() => _finish?.TrySetResult();

    public void Cancel()
    {
        if (_operation is not { } operation) return;
        SetState(new(VoiceInputPhase.Cancelling, State.Anchor));
        operation.Cancel();
    }

    private async Task RunAsync(VoiceDraftAnchor anchor, CancellationTokenSource operation, Task finish)
    {
        try
        {
            SetState(new(VoiceInputPhase.Opening, anchor));
            operation.Token.ThrowIfCancellationRequested();
            RecordedSpeech audio;
            // Do not abandon Open/Dispose with WaitAsync: a late device handle must still be released,
            // and a second capture must not start until the previous microphone is closed.
            await using (var recording = await capture.OpenAsync(operation.Token))
            {
                operation.Token.ThrowIfCancellationRequested();
                SetState(new(VoiceInputPhase.Recording, anchor));
                try { await finish.WaitAsync(RecordedSpeech.MaxDuration, operation.Token); }
                catch (TimeoutException) { /* At the duration limit, finalize the captured speech. */ }
                SetState(new(VoiceInputPhase.Finalizing, anchor));
                audio = await recording.FinishAsync(operation.Token);
            }
            operation.Token.ThrowIfCancellationRequested();
            audio.Validate();
            SetState(new(VoiceInputPhase.Transcribing, anchor));
            var text = await client.TranscribeAsync(anchor.Role, audio, operation.Token).WaitAsync(operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("未识别到语音。");
            SetState(new(VoiceInputPhase.Completed, anchor, new(anchor, text.Trim())));
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        { SetState(new(VoiceInputPhase.Idle)); }
        catch (Exception)
        { SetState(new(VoiceInputPhase.Failed, anchor, Error: "录音或转写失败，请检查麦克风权限与语音设置后重试。")); }
        finally
        {
            if (ReferenceEquals(_operation, operation)) { _operation = null; _finish = null; }
            operation.Dispose();
        }
    }

    private void SetState(VoiceInputState state) { State = state; Changed?.Invoke(this, EventArgs.Empty); }
    public async ValueTask DisposeAsync()
    {
        if (!_disposed) { _disposed = true; Cancel(); }
        if (_running is not null) await _running;
        Changed = null;
    }
}
