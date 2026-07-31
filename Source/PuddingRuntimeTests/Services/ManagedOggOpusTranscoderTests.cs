using System.Text;
using PuddingCode.Models;
using PuddingRuntime.Services;

namespace PuddingRuntimeTests.Services;

[TestClass]
public sealed class ManagedOggOpusTranscoderTests
{
    [TestMethod]
    public async Task TranscodeAsync_StereoPcmWav_ProducesMono16KhzOggOpus()
    {
        var source = CreateStereoPcm16Wav(
            sampleRate: 24_000,
            duration: TimeSpan.FromMilliseconds(250));
        var transcoder = new ManagedOggOpusTranscoder();

        var result = await transcoder.TranscodeAsync(
            new AudioTranscodingRequest
            {
                Content = source,
                SourceFormat = VoiceAudioFormats.Wav,
                TargetFormat = VoiceAudioFormats.Opus,
                TargetSampleRate = 16_000,
                TargetChannels = 1,
                TargetBitrate = 24_000,
            });

        Assert.AreEqual(VoiceAudioFormats.Opus, result.Format);
        Assert.AreEqual("audio/ogg", result.MediaType);
        Assert.AreEqual(16_000, result.SampleRate);
        Assert.AreEqual(1, result.Channels);
        Assert.IsTrue(result.DurationMs is >= 249 and <= 251);
        CollectionAssert.AreEqual(
            Encoding.ASCII.GetBytes("OggS"),
            result.Content[..4]);
        StringAssert.Contains(
            Encoding.Latin1.GetString(result.Content),
            "OpusHead");
    }

    [TestMethod]
    public async Task TranscodeAsync_NonWavInput_IsRejected()
    {
        var transcoder = new ManagedOggOpusTranscoder();

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            transcoder.TranscodeAsync(
                new AudioTranscodingRequest
                {
                    Content = [1, 2, 3],
                    SourceFormat = VoiceAudioFormats.Mp3,
                    TargetFormat = VoiceAudioFormats.Opus,
                }));
    }

    [TestMethod]
    public async Task TranscodeAsync_StreamingWavLengthSentinel_UsesActualSamplesForDuration()
    {
        var source = CreateStereoPcm16Wav(
            sampleRate: 24_000,
            duration: TimeSpan.FromMilliseconds(250));
        // DashScope streaming WAV responses use a large placeholder length
        // because the final RIFF/data size is unknown when the header is sent.
        BitConverter.GetBytes(int.MaxValue - 64).CopyTo(source, 4);
        BitConverter.GetBytes(int.MaxValue - 100).CopyTo(source, 40);
        var transcoder = new ManagedOggOpusTranscoder();

        var result = await transcoder.TranscodeAsync(
            new AudioTranscodingRequest
            {
                Content = source,
                SourceFormat = VoiceAudioFormats.Wav,
                TargetFormat = VoiceAudioFormats.Opus,
                TargetSampleRate = 16_000,
                TargetChannels = 1,
                TargetBitrate = 24_000,
            });

        Assert.IsTrue(result.DurationMs is >= 249 and <= 251);
    }

    [TestMethod]
    public async Task TranscodeAsync_OggOpus_ProducesMono16KhzPcmWav()
    {
        var transcoder = new ManagedOggOpusTranscoder();
        var sourceWav = CreateStereoPcm16Wav(
            sampleRate: 24_000,
            duration: TimeSpan.FromMilliseconds(500));
        var opus = await transcoder.TranscodeAsync(
            new AudioTranscodingRequest
            {
                Content = sourceWav,
                SourceFormat = VoiceAudioFormats.Wav,
                TargetFormat = VoiceAudioFormats.Opus,
                TargetSampleRate = 16_000,
                TargetChannels = 1,
            });

        var wav = await transcoder.TranscodeAsync(
            new AudioTranscodingRequest
            {
                Content = opus.Content,
                SourceFormat = VoiceAudioFormats.Opus,
                TargetFormat = VoiceAudioFormats.Wav,
                TargetSampleRate = 16_000,
                TargetChannels = 1,
            });

        Assert.AreEqual(VoiceAudioFormats.Wav, wav.Format);
        Assert.AreEqual("audio/wav", wav.MediaType);
        Assert.AreEqual(16_000, wav.SampleRate);
        Assert.AreEqual(1, wav.Channels);
        Assert.IsTrue(wav.DurationMs is >= 490 and <= 510);
        CollectionAssert.AreEqual(
            Encoding.ASCII.GetBytes("RIFF"),
            wav.Content[..4]);
        CollectionAssert.AreEqual(
            Encoding.ASCII.GetBytes("WAVE"),
            wav.Content[8..12]);
        Assert.IsGreaterThan(44, wav.Content.Length);
    }

    private static byte[] CreateStereoPcm16Wav(
        int sampleRate,
        TimeSpan duration)
    {
        const short channels = 2;
        const short bitsPerSample = 16;
        var frameCount = (int)Math.Round(sampleRate * duration.TotalSeconds);
        var blockAlign = (short)(channels * bitsPerSample / 8);
        var dataLength = frameCount * blockAlign;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataLength);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * blockAlign);
        writer.Write(blockAlign);
        writer.Write(bitsPerSample);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataLength);

        for (var index = 0; index < frameCount; index++)
        {
            var value = (short)(Math.Sin(
                    2 * Math.PI * 440 * index / sampleRate)
                * short.MaxValue
                * 0.2);
            writer.Write(value);
            writer.Write(value);
        }
        writer.Flush();
        return stream.ToArray();
    }
}
