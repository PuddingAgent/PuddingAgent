using System.Buffers.Binary;

namespace PuddingSpeech;

/// <summary>Checks actual RIFF chunks; labels are never used as evidence of encoding.</summary>
public static class PcmWave
{
    public const int SampleRate = 16000;
    public const int MaxSeconds = 120;
    public const int MaxFileBytes = 8 * 1024 * 1024;

    public static byte[] ReadPcm16KhzMono(byte[] bytes)
    {
        if (bytes.Length > MaxFileBytes)
            throw new SpeechFailure("voice_input_too_large", "录音过大，请将录音控制在 120 秒内。", 413);
        if (bytes.Length < 44 || !bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !bytes.AsSpan(8, 4).SequenceEqual("WAVE"u8))
            throw Unsupported();
        var validFormat = false;
        byte[]? pcm = null;
        for (var offset = 12; offset <= bytes.Length - 8;)
        {
            var size = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 4, 4));
            if (size > bytes.Length - offset - 8) throw Unsupported();
            var data = bytes.AsSpan(offset + 8, (int)size);
            if (bytes.AsSpan(offset, 4).SequenceEqual("fmt "u8))
            {
                if (size < 16) throw Unsupported();
                validFormat = BinaryPrimitives.ReadUInt16LittleEndian(data) == 1
                    && BinaryPrimitives.ReadUInt16LittleEndian(data[2..]) == 1
                    && BinaryPrimitives.ReadInt32LittleEndian(data[4..]) == SampleRate
                    && BinaryPrimitives.ReadUInt16LittleEndian(data[12..]) == 2
                    && BinaryPrimitives.ReadUInt16LittleEndian(data[14..]) == 16;
            }
            if (bytes.AsSpan(offset, 4).SequenceEqual("data"u8)) pcm = data.ToArray();
            offset = checked(offset + 8 + (int)size + ((int)size & 1));
        }
        if (!validFormat || pcm is null || pcm.Length % 2 != 0) throw Unsupported();
        if (pcm.Length == 0) throw new SpeechFailure("voice_no_speech", "没有录到声音，请靠近麦克风再试。", 400);
        if (pcm.Length > SampleRate * 2 * MaxSeconds)
            throw new SpeechFailure("voice_input_too_large", "录音超过 120 秒，请分段输入。", 413);
        return pcm;
    }

    private static SpeechFailure Unsupported() => new("voice_unsupported_audio", "录音需为 16 kHz、单声道 PCM WAV，请重新录音。", 415);
}
