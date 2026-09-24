namespace PuddingVectorIndex;

/// <summary>
/// Symmetric (absmax) int8 quantisation of one vector, as a value: the stored codes plus the single
/// scale needed to reconstruct the numbers.
/// <para>
/// <b>Why this exists.</b> The U4-1b vector store measured 5,918,577 B for 1,352 chunks — 4,096 B of raw
/// float32 per chunk — which extrapolates to roughly 4.5 GB across the repository and blows through the
/// 1 GB budget guard (ADR-089 §2.5). One byte per component is the cheapest honest way to cut that,
/// and it is the format the design already lists as mandatory ("int8 列为必做").
/// </para>
/// <para>
/// <b>Validated on construction, not on use.</b> The scale must be a positive finite number and every
/// code must lie in [-127, 127]. A zero scale would make every reconstruction zero (a vector with no
/// direction, whose similarity would have to be invented); NaN or Infinity would make every
/// reconstruction meaningless; and a code of -128 — the one value <see cref="sbyte"/> can hold that
/// symmetric absmax never produces — would break the documented error bound. All three are rejected
/// here rather than clamped, because a silently repaired vector is indistinguishable from a correctly
/// quantised one in the search results.
/// </para>
/// </summary>
public sealed class QuantizedVector
{
    private readonly sbyte[] _codes;

    /// <param name="codes">One code per component, in [-127, 127]. Copied on construction.</param>
    /// <param name="scale">Positive finite step size: one code unit is worth <paramref name="scale"/>.</param>
    public QuantizedVector(sbyte[] codes, float scale)
    {
        ArgumentNullException.ThrowIfNull(codes);

        if (codes.Length == 0)
            throw new ArgumentException("a quantised vector must have at least one component", nameof(codes));

        if (!float.IsFinite(scale) || scale <= 0f)
            throw new ArgumentOutOfRangeException(
                nameof(scale),
                scale,
                "the quantisation scale must be a positive finite number: zero would make every "
                + "reconstruction zero (no direction, so no similarity), and NaN/Infinity would make "
                + "every reconstruction meaningless");

        var copy = (sbyte[])codes.Clone();
        for (var i = 0; i < copy.Length; i++)
        {
            if (copy[i] < VectorQuantizer.MinCode)
                throw new ArgumentOutOfRangeException(
                    nameof(codes),
                    copy[i],
                    $"quantised code at index {i} is outside the symmetric int8 range "
                    + $"[{VectorQuantizer.MinCode}, {VectorQuantizer.MaxCode}]; it must be rejected, never clamped");
        }

        _codes = copy;
        Scale = scale;
    }

    /// <summary>Number of components — the vector's dimension.</summary>
    public int Dimensions => _codes.Length;

    /// <summary>The stored codes, as a defensive copy of what the caller supplied.</summary>
    public ReadOnlyMemory<sbyte> Codes => _codes;

    /// <summary>Step size: one code unit is worth <see cref="Scale"/>.</summary>
    public float Scale { get; }

    /// <summary>
    /// Bytes one stored vector of this dimension occupies: one byte per component plus the single
    /// float32 scale. This is a property of the format, not of a file, so it can be asserted without IO.
    /// </summary>
    public int StorageBytes => _codes.Length * sizeof(sbyte) + sizeof(float);

    /// <summary>Reconstructs the float32 vector. The error bound is documented on <see cref="VectorQuantizer"/>.</summary>
    public float[] Dequantize() => VectorQuantizer.Dequantize(_codes, Scale);

    /// <summary>Read-only view for a search loop that wants to dequantise in place.</summary>
    internal ReadOnlySpan<sbyte> Span => _codes;
}

/// <summary>
/// The quantiser itself: <c>scale = max|x| / 127</c>, <c>code = round(x / scale)</c>.
/// <para>
/// <b>The contract (asserted by the test suite, not observed from a sample):</b> for every component i
/// of an input vector <c>x</c>, dequantising the codes produced by <see cref="Quantize"/> reconstructs
/// <c>x'</c> with <c>|x[i] - x'[i]| &lt;= Scale / 2</c>.
/// </para>
/// <para>
/// <b>Proof.</b> Let <c>m = max|x[i]|</c> and <c>s = m / 127</c> (the largest magnitude is mapped onto
/// the largest code). For every i, <c>|x[i]/s| &lt;= 127</c>, so no code ever needs to saturate; and
/// rounding to the nearest integer gives <c>|x[i]/s - code| &lt;= 0.5</c>, hence
/// <c>|x[i] - code * s| &lt;= 0.5 * s</c>. The bound therefore holds for <b>every</b> component,
/// including the extremes, and it is a property of absmax rather than of the data: a fixed scale (the
/// M1 mutation) breaks it as soon as a component exceeds the fixed step by half a step.
/// </para>
/// <para>
/// Working in <see cref="double"/> for the division and the rounding keeps the inequality exact up to a
/// relative float-rounding term; the test allows that term explicitly rather than hiding it.
/// </para>
/// </summary>
public static class VectorQuantizer
{
    /// <summary>Most negative code symmetric absmax produces. <see cref="sbyte"/> also holds -128, which is invalid here.</summary>
    public const int MinCode = -127;

    /// <summary>Most positive code.</summary>
    public const int MaxCode = 127;

    /// <summary>
    /// The analytic round-trip error bound, expressed in units of <c>Scale</c>:
    /// <c>|x[i] - dequantise(quantise(x))[i]| &lt;= MaxComponentErrorInScaleUnits * Scale</c>.
    /// </summary>
    public const double MaxComponentErrorInScaleUnits = 0.5;

    /// <summary>
    /// Quantises one vector. Fail-closed: an empty vector, a vector containing NaN or Infinity, and an
    /// all-zero vector are all rejected — the last because its scale would be zero and there is no
    /// direction to scale.
    /// </summary>
    public static QuantizedVector Quantize(ReadOnlySpan<float> vector)
    {
        if (vector.Length == 0)
            throw new ArgumentException("a vector must have at least one component", nameof(vector));

        var maxMagnitude = 0d;
        for (var i = 0; i < vector.Length; i++)
        {
            var value = vector[i];
            if (!float.IsFinite(value))
                throw new ArgumentException(
                    $"vector component {i} is {value}; NaN and Infinity cannot be quantised, and clamping "
                    + "them to the extremes would invent a direction the embedding never had",
                    nameof(vector));

            var magnitude = Math.Abs((double)value);
            if (magnitude > maxMagnitude)
                maxMagnitude = magnitude;
        }

        if (maxMagnitude == 0d)
            throw new InvalidOperationException(
                "cannot quantise an all-zero vector: its absmax scale would be zero, so every code would "
                + "be zero and the resulting vector would have no direction");

        var scale = (float)(maxMagnitude / MaxCode);
        var codes = new sbyte[vector.Length];

        for (var i = 0; i < vector.Length; i++)
        {
            var code = (int)Math.Round((double)vector[i] / scale, MidpointRounding.AwayFromZero);

            // Defensive only: |x/s| <= 127 by construction, so this clamp cannot change a value that the
            // bound above relies on. It exists so that float rounding at the extreme cannot produce 128.
            if (code < MinCode)
                code = MinCode;
            else if (code > MaxCode)
                code = MaxCode;

            codes[i] = (sbyte)code;
        }

        return new QuantizedVector(codes, scale);
    }

    /// <summary>
    /// Reconstructs <c>code * scale</c> for every component. Fail-closed: an invalid scale and an
    /// out-of-range code are errors, never silently clamped — a clamp here would quietly turn a corrupt
    /// row into a plausible vector.
    /// </summary>
    public static float[] Dequantize(ReadOnlySpan<sbyte> codes, float scale)
    {
        if (codes.Length == 0)
            throw new ArgumentException("a quantised vector must have at least one component", nameof(codes));

        if (!float.IsFinite(scale) || scale <= 0f)
            throw new ArgumentOutOfRangeException(
                nameof(scale),
                scale,
                "the quantisation scale must be a positive finite number; a store that reports otherwise "
                + "must be rejected rather than decoded");

        var result = new float[codes.Length];
        for (var i = 0; i < codes.Length; i++)
        {
            var code = codes[i];
            if (code < MinCode)
                throw new ArgumentOutOfRangeException(
                    nameof(codes),
                    code,
                    $"quantised code at index {i} is {code}, outside the symmetric int8 range "
                    + $"[{MinCode}, {MaxCode}] that absmax produces; clamping it would hide a corrupt store");

            result[i] = code * scale;
        }

        return result;
    }

    /// <summary>Bytes a float32 representation of one vector of this dimension occupies.</summary>
    public static long Float32StorageBytes(int dimensions)
    {
        if (dimensions <= 0)
            throw new ArgumentOutOfRangeException(nameof(dimensions), dimensions, "dimensions must be positive");

        return (long)dimensions * sizeof(float);
    }

    /// <summary>Bytes a symmetric int8 representation (codes plus the one scale) occupies.</summary>
    public static long Int8StorageBytes(int dimensions)
    {
        if (dimensions <= 0)
            throw new ArgumentOutOfRangeException(nameof(dimensions), dimensions, "dimensions must be positive");

        return (long)dimensions * sizeof(sbyte) + sizeof(float);
    }
}
