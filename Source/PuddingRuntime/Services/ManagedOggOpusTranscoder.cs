using Concentus;
using Concentus.Enums;
using Concentus.Oggfile;
using Concentus.Structs;
using NAudio.Wave;
using PuddingCode.Abstractions;
using PuddingCode.Models;

namespace PuddingRuntime.Services;

/// <summary>
/// Pure managed WAV/Ogg-Opus transcoder for short channel audio. It uses
/// NAudio.Core for WAV parsing and Concentus for resampling, Opus
/// encoding/decoding, and Ogg framing; no ffmpeg or native codec is required.
/// </summary>
public sealed class ManagedOggOpusTranscoder : IAudioTranscoder
{
    private const int MaxInputBytes = 30 * 1024 * 1024;
    private const int DefaultBitrate = 24_000;
    private const int OpusDecodeSampleRate = 48_000;
    private const int MaxDecodedDurationSeconds = 10 * 60;

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
        if (string.Equals(
                request.SourceFormat,
                VoiceAudioFormats.Opus,
                StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                request.TargetFormat,
                VoiceAudioFormats.Wav,
                StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(DecodeOggOpusToWav(request, ct));
        }
        if (!string.Equals(
                request.SourceFormat,
                VoiceAudioFormats.Wav,
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                request.TargetFormat,
                VoiceAudioFormats.Opus,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(
                $"Managed audio transcoder does not support '{request.SourceFormat}' to '{request.TargetFormat}'.");
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

    private static AudioTranscodingResult DecodeOggOpusToWav(
        AudioTranscodingRequest request,
        CancellationToken ct)
    {
        if (request.TargetSampleRate != 16_000 || request.TargetChannels != 1)
        {
            throw new NotSupportedException(
                "ASR WAV normalization must be mono at 16000 Hz.");
        }

        var channels = ReadOpusChannelCount(request.Content);
        var decoder = OpusCodecFactory.CreateDecoder(
            OpusDecodeSampleRate,
            channels);
        using var input = new MemoryStream(request.Content, writable: false);
        var reader = new OpusOggReadStream(decoder, input);
        var resampler = ResamplerFactory.CreateResampler(
            1,
            OpusDecodeSampleRate,
            request.TargetSampleRate,
            5,
            TextWriter.Null);
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, System.Text.Encoding.UTF8, leaveOpen: true);
        WritePcmWavHeader(writer, dataLength: 0, request.TargetSampleRate);

        long sourceFrames = 0;
        long outputSamples = 0;
        try
        {
            while (reader.HasNextPacket)
            {
                ct.ThrowIfCancellationRequested();
                var decoded = reader.DecodeNextPacket();
                if (decoded is null || decoded.Length == 0)
                    continue;
                if (decoded.Length % channels != 0)
                    throw new InvalidDataException("Decoded Opus packet has invalid channel framing.");

                var frameCount = decoded.Length / channels;
                sourceFrames += frameCount;
                if (sourceFrames > (long)OpusDecodeSampleRate * MaxDecodedDurationSeconds)
                {
                    throw new InvalidDataException(
                        $"Decoded Opus audio exceeds {MaxDecodedDurationSeconds} seconds.");
                }

                var mono = channels == 1
                    ? decoded
                    : DownmixStereo(decoded);
                var inputOffset = 0;
                while (inputOffset < mono.Length)
                {
                    var inputLength = mono.Length - inputOffset;
                    var resampled = new short[Math.Max(
                        256,
                        (int)Math.Ceiling(
                            inputLength
                            * (double)request.TargetSampleRate
                            / OpusDecodeSampleRate)
                        + 256)];
                    var outputLength = resampled.Length;
                    resampler.Process(
                        0,
                        mono.AsSpan(inputOffset),
                        ref inputLength,
                        resampled,
                        ref outputLength);
                    if (inputLength == 0 && outputLength == 0)
                        throw new InvalidDataException("Opus resampler made no progress.");

                    inputOffset += inputLength;
                    for (var i = 0; i < outputLength; i++)
                        writer.Write(resampled[i]);
                    outputSamples += outputLength;
                }
            }
        }
        finally
        {
            reader.Close();
        }

        if (sourceFrames == 0 || outputSamples == 0)
            throw new InvalidDataException("Ogg/Opus input contains no decodable audio frames.");

        var dataLength = checked((int)(outputSamples * sizeof(short)));
        output.Position = 0;
        WritePcmWavHeader(writer, dataLength, request.TargetSampleRate);
        writer.Flush();
        return new AudioTranscodingResult
        {
            Content = output.ToArray(),
            Format = VoiceAudioFormats.Wav,
            MediaType = "audio/wav",
            SampleRate = request.TargetSampleRate,
            Channels = request.TargetChannels,
            DurationMs = (long)Math.Ceiling(
                sourceFrames * 1_000d / OpusDecodeSampleRate),
        };
    }

    private static int ReadOpusChannelCount(ReadOnlySpan<byte> content)
    {
        var index = content.IndexOf("OpusHead"u8);
        if (index < 0 || content.Length <= index + 9)
            throw new InvalidDataException("Audio input is not an Ogg/Opus stream.");
        var channels = content[index + 9];
        return channels is 1 or 2
            ? channels
            : throw new NotSupportedException(
                $"Only mono or stereo Ogg/Opus input is supported, not {channels} channels.");
    }

    private static short[] DownmixStereo(short[] interleaved)
    {
        var mono = new short[interleaved.Length / 2];
        for (var i = 0; i < mono.Length; i++)
        {
            mono[i] = (short)(
                ((int)interleaved[i * 2] + interleaved[(i * 2) + 1])
                / 2);
        }
        return mono;
    }

    private static void WritePcmWavHeader(
        BinaryWriter writer,
        int dataLength,
        int sampleRate)
    {
        writer.Write("RIFF"u8);
        writer.Write(36 + dataLength);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * sizeof(short));
        writer.Write((short)sizeof(short));
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(dataLength);
    }
}
