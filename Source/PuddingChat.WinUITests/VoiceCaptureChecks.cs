using System.Runtime.InteropServices.WindowsRuntime;
using PuddingChat;
using PuddingChat.WinUI;

namespace PuddingChat.WinUITests;

public partial class App
{
    private static async Task VerifyVoiceCaptureContractAsync()
    {
        var profile = NativeVoiceCapture.CreateProfile();
        Check(profile.Audio.SampleRate == 16_000 && profile.Audio.ChannelCount == 1 && profile.Audio.BitsPerSample == 16
            && profile.Audio.Subtype == "PCM" && profile.Video is null, "native capture uses audio-only PCM 16k mono WAV profile");
        using var buffer = NativeVoiceCapture.CreateBuffer();
        using var stream = buffer.AsRandomAccessStream();
        var wav = SilentWave(100).Bytes;
        await stream.WriteAsync(wav.AsBuffer());
        Check(buffer.ToArray().SequenceEqual(wav), "WinRT recording stream preserves finalized WAV bytes");
        stream.Seek((ulong)RecordedSpeech.MaxBytes - 1); await stream.WriteAsync(new byte[] { 1 }.AsBuffer());
        var refused = false;
        try { await stream.WriteAsync(new byte[] { 2 }.AsBuffer()); } catch (Exception) { refused = true; }
        Check(refused && buffer.Length == RecordedSpeech.MaxBytes && buffer.Capacity == RecordedSpeech.MaxBytes,
            "native asynchronous writes cannot expand recording beyond eight MiB");
        var cancelled = false;
        try { await new NativeVoiceCapture().OpenAsync(new CancellationToken(true)); } catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled, "pre-cancelled capture never initializes microphone");
    }
}
