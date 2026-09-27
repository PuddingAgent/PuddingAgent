using PuddingChat;
using PuddingChat.WinUI;

namespace PuddingChat.WinUITests;

public partial class App
{
    private static SpeechAudio SilentWave(int milliseconds)
    {
        const int rate = 16_000;
        var size = rate * milliseconds / 1000 * 2;
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8); writer.Write(36 + size); writer.Write("WAVEfmt "u8);
        writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(rate);
        writer.Write(rate * 2); writer.Write((short)2); writer.Write((short)16);
        writer.Write("data"u8); writer.Write(size); writer.Write(new byte[size]);
        return new(stream.ToArray(), "audio/wav");
    }
    private static async Task VerifyNativeSpeechPlayerAsync()
    {
        using var player = new NativeSpeechAudioPlayer(muted: true);
        await player.PlayAsync(SilentWave(150), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Check(!player.HasActivePlayback, "native muted WAV completes and releases its media player");
        using var cancellation = new CancellationTokenSource();
        var playing = player.PlayAsync(SilentWave(10_000), cancellation.Token);
        await UntilAsync(() => player.HasActivePlayback); cancellation.Cancel();
        try { await playing; throw new InvalidOperationException("Expected cancellation"); }
        catch (OperationCanceledException) { }
        Check(!player.HasActivePlayback, "native playback cancellation releases resources before task completion");
        var failed = false;
        try { await player.PlayAsync(new([1, 2, 3], "audio/wav"), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (InvalidOperationException) { failed = true; }
        Check(failed && !player.HasActivePlayback, "invalid native media reports failure and releases resources");
        var pending = player.PlayAsync(SilentWave(10_000), CancellationToken.None);
        player.Dispose();
        try { await pending; throw new InvalidOperationException("Expected disposal cancellation"); }
        catch (OperationCanceledException) { }
        Check(!player.HasActivePlayback, "disposing native player cancels pending playback");
    }
}
