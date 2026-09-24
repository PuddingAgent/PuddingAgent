namespace PuddingVectorIndex;

/// <summary>
/// Similarity against a quantised store.
/// <para>
/// <b>One path only: dequantise, then call the existing float cosine.</b> A fused integer dot product
/// would be faster, but it would be a <i>second</i> similarity definition: its score would differ from
/// <see cref="VectorMath.Cosine"/> by the quantisation error <i>and</i> the integer arithmetic's own
/// rounding, so a measured quality change could no longer be attributed to the storage format. This
/// method answers exactly the question the measurement asks — "what does the data cost once the codes
/// are read back?" — and nothing else, which is what keeps "int8 changes storage, not the ranking rule"
/// an assertion instead of a claim.
/// </para>
/// </summary>
public static class QuantizedVectorMath
{
    /// <summary>
    /// Cosine similarity between a quantised vector and a float query.
    /// <para>
    /// A dimension mismatch and an all-zero operand are refused by <see cref="VectorMath.Cosine"/>
    /// itself; this method deliberately does not restate those rules, so there is exactly one place
    /// where "vectors of different lengths are not comparable" is decided.
    /// </para>
    /// </summary>
    public static double Cosine(QuantizedVector vector, ReadOnlySpan<float> query)
    {
        ArgumentNullException.ThrowIfNull(vector);
        return VectorMath.Cosine(vector.Dequantize(), query);
    }
}
