using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Storage.Streams;

namespace PuddingChat.WinUI;

/// <summary>UI/STA-owned audio-only capture. No device is opened until OpenAsync is explicitly called.</summary>
public sealed class NativeVoiceCapture : IVoiceCapture
{
    internal static MemoryStream CreateBuffer()
    {
        // A non-expandable buffer bounds encoder writes, including native asynchronous writes.
        var buffer = new MemoryStream(new byte[RecordedSpeech.MaxBytes], writable: true);
        buffer.SetLength(0); return buffer;
    }
    internal static MediaEncodingProfile CreateProfile()
    {
        var profile = MediaEncodingProfile.CreateWav(AudioEncodingQuality.Low);
        profile.Audio = AudioEncodingProperties.CreatePcm(16_000, 1, 16);
        return profile;
    }
    public async Task<IVoiceRecording> OpenAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw new InvalidOperationException("请从界面线程打开麦克风。");
        var capture = new MediaCapture(); var buffer = CreateBuffer();
        var stream = buffer.AsRandomAccessStream();
        try
        {
            await capture.InitializeAsync(new MediaCaptureInitializationSettings
            { StreamingCaptureMode = StreamingCaptureMode.Audio, MediaCategory = MediaCategory.Speech }).AsTask(ct);
            ct.ThrowIfCancellationRequested();
            await capture.StartRecordToStreamAsync(CreateProfile(), stream).AsTask(ct);
            ct.ThrowIfCancellationRequested();
            return new Recording(capture, buffer, stream);
        }
        catch { capture.Dispose(); stream.Dispose(); buffer.Dispose(); throw; }
    }

    private sealed class Recording : IVoiceRecording
    {
        private readonly MediaCapture _capture;
        private readonly MemoryStream _buffer;
        private readonly IRandomAccessStream _stream;
        private readonly CancellationTokenSource _lifetime = new();
        private readonly TaskCompletionSource _failed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task _watch;
        private Task? _stop, _dispose;
        private Exception? _error;
        private bool _finished;
        public Recording(MediaCapture capture, MemoryStream buffer, IRandomAccessStream stream)
        {
            _capture = capture; _buffer = buffer; _stream = stream;
            _capture.Failed += OnFailed;
            _watch = WatchLimitAsync();
        }
        private void OnFailed(MediaCapture sender, MediaCaptureFailedEventArgs args) => _failed.TrySetResult();
        private async Task WatchLimitAsync()
        {
            try
            {
                var delay = Task.Delay(RecordedSpeech.MaxDuration, _lifetime.Token);
                var completed = await Task.WhenAny(delay, _failed.Task);
                _lifetime.Token.ThrowIfCancellationRequested();
                if (completed == _failed.Task) _error = new IOException("麦克风采集失败。");
                await StopOnceAsync();
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (Exception error) { _error = error; }
        }
        private Task StopOnceAsync() => _stop ??= StopCoreAsync();
        private async Task StopCoreAsync()
        {
            try { await _capture.StopRecordAsync(); }
            finally { _capture.Failed -= OnFailed; _capture.Dispose(); }
        }
        public async Task<RecordedSpeech> FinishAsync(CancellationToken ct)
        {
            ObjectDisposedException.ThrowIf(_dispose is not null, this);
            if (_finished) throw new InvalidOperationException("录音已结束。");
            _finished = true;
            _lifetime.Cancel();
            await _watch;
            await StopOnceAsync();
            ct.ThrowIfCancellationRequested();
            // A failed device event can arrive immediately before Finish cancels the watcher.
            if (_error is not null || _failed.Task.IsCompleted) throw new IOException("麦克风采集失败。", _error);
            var audio = new RecordedSpeech(_buffer.ToArray()); audio.Validate(); return audio;
        }
        public ValueTask DisposeAsync() => new(_dispose ??= DisposeCoreAsync());
        private async Task DisposeCoreAsync()
        {
            _lifetime.Cancel();
            try { await _watch; await StopOnceAsync(); }
            finally { _stream.Dispose(); _buffer.Dispose(); _lifetime.Dispose(); }
        }
    }
}
