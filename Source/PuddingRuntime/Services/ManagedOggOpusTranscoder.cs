using Concentus;
using Concentus.Enums;
using Concentus.Oggfile;
using Concentus.Structs;
using NAudio.Wave;
using PuddingCode.Abstractions;
using PuddingCode.Models;

namespace PuddingRuntime.Services;

/// <summary>
/// Pure managed WAV to Ogg/Opus transcoder for short channel audio. It uses
/// NAudio.Core for WAV parsing/downmixing and Concentus for resampling, Opus
/// encoding, and Ogg encapsulation; no ffmpeg or native codec is required.
/// </summary>
public sealed class ManagedOggOpusTranscoder : IAudioTranscoder
{
    private const int MaxInputBytes = 30 * 1024 * 1024;
    private const int DefaultBitrate = 24_000;

    public Task<AudioTranscodingResult> TranscodeAsync(
        AudioTranscodingRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Content.Length == 0)
            throw new InvalidDataException("Audio input is empty.");
        if (request.Content.Length > MaxInputBytes)
        {
            throw new InvalidDataException(
                $"Audio input exceeds {MaxInputBytes} bytes.");
        }
        if (!string.Equals(
                request.SourceFormat,
                VoiceAudioFormats.Wav,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(
                $"Managed audio transcoder only accepts WAV input, not '{request.SourceFormat}'.");
        }
        if (!string.Equals(
                request.TargetFormat,
                VoiceAudioFormats.Opus,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(
                $"Managed audio transcoder only produces Ogg/Opus, not '{request.TargetFormat}'.");
        }
        if (request.TargetSampleRate != 16_000 || request.TargetChannels != 1)
        {
            throw new NotSupportedException(
                "Feishu Opus output must be mono at 16000 Hz.");
        }

        using var input = new MemoryStream(request.Content, writable: false);
        using var reader = new WaveFileReader(input);
        if (reader.WaveFormat.Channels is < 1 or > 2)
        {
            throw new NotSupportedException(
                $"Only mono or stereo WAV input is supported, not {reader.WaveFormat.Channels} channels.");
        }
        ISampleProvider samples = reader.ToSampleProvider();
        if (samples.WaveFormat.Channels == 2)
            samples = samples.ToMono();
        var inputSampleRate = samples.WaveFormat.SampleRate;

        var encoder = OpusCodecFactory.CreateEncoder(
            request.TargetSampleRate,
            request.TargetChannels,
            OpusApplication.OPUS_APPLICATION_VOIP);
        encoder.Bitrate = request.TargetBitrate ?? DefaultBitrate;

        using var output = new MemoryStream();
        var writer = new OpusOggWriteStream(
            encoder,
            output,
            inputSampleRate: inputSampleRate,
            leaveOpen: true);
        var buffer = new float[4_096];
        long samplesRead = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var read = samples.Read(buffer, 0, buffer.Length);
            if (read == 0)
                break;
            samplesRead += read;
            writer.WriteSamples(buffer, 0, read);
        }
        writer.Finish();
        if (samplesRead == 0)
            throw new InvalidDataException("WAV input contains no audio frames.");

        var encoded = output.ToArray();
        if (encoded.Length == 0)
            throw new InvalidDataException("Opus encoder produced an empty payload.");

        return Task.FromResult(new AudioTranscodingResult
        {
            Content = encoded,
            Format = VoiceAudioFormats.Opus,
            MediaType = "audio/ogg",
            SampleRate = request.TargetSampleRate,
            Channels = request.TargetChannels,
            // Some streaming TTS providers emit an intentionally oversized
            // RIFF/data length sentinel because the final length is unknown
            // when the header is written. Count the PCM samples actually read
            // instead of trusting that header-derived TotalTime.
            DurationMs = (long)Math.Ceiling(
                samplesRead * 1_000d / inputSampleRate),
        });
    }
}
