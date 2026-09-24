namespace PuddingVectorIndex;

/// <summary>
/// Vector primitives for the brute-force cosine index. Pure functions, no IO, no thresholds.
/// <para>
/// <b>Fail-closed, not silent.</b> A length mismatch is an error rather than a truncation (a truncated
/// cosine would return a plausible-looking number for two incomparable vectors — the worst possible
/// failure mode for a retrieval comparison), and a zero vector is an error rather than a similarity of
/// 0 (a zero vector carries no direction, so any score assigned to it would be invented).
/// </para>
/// </summary>
public static class VectorMath
{
    /// <summary>Inner product. Throws when the operands have different lengths.</summary>
    public static double Dot(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        RequireSameLength(left, right, nameof(right));

        double sum = 0d;
        for (var i = 0; i < left.Length; i++)
            sum += (double)left[i] * right[i];

        return sum;
    }

    /// <summary>Euclidean (L2) norm.</summary>
    public static double Norm(ReadOnlySpan<float> vector)
    {
        double sum = 0d;
        foreach (var value in vector)
            sum += (double)value * value;

        return Math.Sqrt(sum);
    }

    /// <summary>
    /// Cosine similarity in [-1, 1].
    /// <para>
    /// Throws <see cref="ArgumentException"/> for a length mismatch and
    /// <see cref="InvalidOperationException"/> when either operand is a zero vector. The accumulated
    /// sum uses <see cref="double"/> deliberately: summing 1024 float products in float loses enough
    /// precision to reorder near-tied hits, which would make the ranking of two runs differ.
    /// </para>
    /// </summary>
    public static double Cosine(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        RequireSameLength(left, right, nameof(right));

        var leftNorm = Norm(left);
        var rightNorm = Norm(right);

        if (leftNorm == 0d || rightNorm == 0d)
            throw new InvalidOperationException(
                "cosine similarity is undefined for a zero vector (it has no direction); "
                + "the provider or the caller must not produce one");

        return Dot(left, right) / (leftNorm * rightNorm);
    }

    /// <summary>Unit vector in the same direction. Throws for a zero vector (see <see cref="Cosine"/>).</summary>
    public static float[] Normalize(ReadOnlySpan<float> vector)
    {
        var norm = Norm(vector);
        if (norm == 0d)
            throw new InvalidOperationException(
                "cannot normalise a zero vector (it has no direction)");

        var result = new float[vector.Length];
        for (var i = 0; i < vector.Length; i++)
            result[i] = (float)(vector[i] / norm);

        return result;
    }

    private static void RequireSameLength(ReadOnlySpan<float> left, ReadOnlySpan<float> right, string parameterName)
    {
        if (left.Length != right.Length)
            throw new ArgumentException(
                $"vector dimension mismatch: {left.Length} vs {right.Length}; vectors of different lengths are "
                + "not comparable and must never be silently truncated",
                parameterName);
    }
}
