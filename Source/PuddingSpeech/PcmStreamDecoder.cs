using System.Buffers.Binary;

namespace PuddingSpeech;

/// <summary>Normalizes a single PCM stream, including Qwen's streaming WAV header with placeholder lengths.</summary>
internal sealed class PcmStreamDecoder(int sampleRate)
{
    private byte[] pending = [];
    private bool passThrough;
    private bool wave;

    public byte[] Push(byte[] bytes)
    {
        if (passThrough) return bytes;
        var data = new byte[pending.Length + bytes.Length];
        pending.CopyTo(data, 0); bytes.CopyTo(data, pending.Length);
        pending = data;
        if (data.Length < 4) return [];
        wave = data.AsSpan(0, 4).SequenceEqual("RIFF"u8);
        if (!wave) { passThrough = true; pending = []; return data; }
        if (data.Length < 12) return [];
        if (!data.AsSpan(8, 4).SequenceEqual("WAVE"u8)) throw SpeechFailure.Protocol();
        var validFormat = false;
        for (var offset = 12; offset <= data.Length - 8;)
        {
            var size = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 4, 4));
            if (data.AsSpan(offset, 4).SequenceEqual("data"u8))
            {
                if (!validFormat) throw SpeechFailure.Protocol();
                // Streaming Qwen uses placeholder RIFF/data lengths; completion is the SSE terminal.
                passThrough = true; pending = [];
                return data[(offset + 8)..];
            }
            if (size > 65536 || offset + 8L + size + (size & 1) > 65536) throw SpeechFailure.Protocol();
            if (size > data.Length - offset - 8) return [];
            if (data.AsSpan(offset, 4).SequenceEqual("fmt "u8))
            {
                var format = data.AsSpan(offset + 8, (int)size);
                if (size < 16 || BinaryPrimitives.ReadUInt16LittleEndian(format) != 1
                    || BinaryPrimitives.ReadUInt16LittleEndian(format[2..]) != 1
                    || BinaryPrimitives.ReadInt32LittleEndian(format[4..]) != sampleRate
                    || BinaryPrimitives.ReadUInt16LittleEndian(format[12..]) != 2
                    || BinaryPrimitives.ReadUInt16LittleEndian(format[14..]) != 16)
                    throw SpeechFailure.Protocol();
                validFormat = true;
            }
            offset += 8 + (int)size + ((int)size & 1);
        }
        return [];
    }

    public byte[] Complete()
    {
        if (passThrough) return [];
        if (wave || pending.Length == 0 || "RIFF"u8.StartsWith(pending)) throw SpeechFailure.Protocol();
        return pending; // Short raw PCM stream, never a WAV header.
    }
}
