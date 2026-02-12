namespace PuddingMemoryEngine.Data;

/// <summary>
/// Utility helpers for simple in-process vector similarity calculations.
/// </summary>
public static class VectorSimilarity
{
    public static float[] BytesToFloats(byte[] bytes)
    {
        if (bytes.Length == 0)
            return [];

        var count = bytes.Length / sizeof(float);
        var values = new float[count];
        Buffer.BlockCopy(bytes, 0, values, 0, count * sizeof(float));
        return values;
    }

    public static byte[] FloatsToBytes(float[] floats)
    {
        if (floats.Length == 0)
            return [];

        var bytes = new byte[floats.Length * sizeof(float)];
        Buffer.BlockCopy(floats, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    public static double CosineSimilarity(IReadOnlyList<float> left, IReadOnlyList<float> right)
    {
        var count = Math.Min(left.Count, right.Count);
        if (count == 0)
            return 0d;

        double dot = 0d;
        double leftNorm = 0d;
        double rightNorm = 0d;

        for (var i = 0; i < count; i++)
        {
            var l = left[i];
            var r = right[i];
            dot += l * r;
            leftNorm += l * l;
            rightNorm += r * r;
        }

        if (leftNorm <= 0d || rightNorm <= 0d)
            return 0d;

        return dot / (Math.Sqrt(leftNorm) * Math.Sqrt(rightNorm));
    }
}
