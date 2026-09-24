using PuddingVectorIndex;

namespace PuddingVectorIndexTests;

/// <summary>
/// Contract tests for int8 quantisation (task book U4-3 §5, assertions A2/A3/A4).
/// <para>
/// The point of these tests is not "the numbers look close". It is that the <b>documented</b> error
/// bound is the thing being asserted, that every degenerate input is refused rather than repaired, and
/// that the scale is the adaptive absmax value rather than any constant — the three ways a quantiser can
/// look fine on a friendly sample while being wrong.
/// </para>
/// </summary>
[TestClass]
public sealed class VectorQuantizationTests
{
    /// <summary>Magnitudes chosen so a fixed scale cannot masquerade as absmax (see <see cref="A2_Round_Trip_Error_Never_Exceeds_Half_A_Scale_Step"/>).</summary>
    private static readonly double[] Magnitudes = [0.001, 0.5, 2.5, 127.0, 128.0, 1000.0];

    [TestMethod]
    public void A2_Round_Trip_Error_Never_Exceeds_Half_A_Scale_Step()
    {
        Assert.AreEqual(
            0.5,
            VectorQuantizer.MaxComponentErrorInScaleUnits,
            "the documented bound is what the test asserts; changing it must change this test");

        var random = new Random(20260924);

        foreach (var magnitude in Magnitudes)
        {
            for (var trial = 0; trial < 40; trial++)
            {
                var vector = new float[37];
                for (var i = 0; i < vector.Length; i++)
                    vector[i] = (float)(((random.NextDouble() * 2) - 1) * magnitude);

                // Pin the exact absmax so the expected scale is known rather than sampled.
                vector[trial % vector.Length] = (float)-magnitude;

                var quantised = VectorQuantizer.Quantize(vector);
                var expectedScale = (float)(magnitude / VectorQuantizer.MaxCode);

                Assert.AreEqual(
                    expectedScale,
                    quantised.Scale,
                    (float)(magnitude * 1e-6),
                    $"absmax scale for magnitude {magnitude} must be max|x|/{VectorQuantizer.MaxCode}; a fixed "
                    + "scale would pass a small-magnitude sample and fail here");

                Assert.AreEqual(
                    (sbyte)-VectorQuantizer.MaxCode,
                    quantised.Codes.Span[trial % vector.Length],
                    "the largest magnitude must map onto the largest code, not into saturation");

                var back = quantised.Dequantize();
                var bound = quantised.Scale * VectorQuantizer.MaxComponentErrorInScaleUnits;

                // The analytic bound holds exactly in real arithmetic; the factor below is the float
                // division's own relative rounding (scale = max/127 rounded to float32), stated rather
                // than hidden.
                var allowance = bound * (1 + 1e-6);

                for (var i = 0; i < vector.Length; i++)
                {
                    var error = Math.Abs((double)vector[i] - back[i]);
                    Assert.IsTrue(
                        error <= allowance,
                        $"component {i} of magnitude-{magnitude} vector: |{vector[i]} - {back[i]}| = {error} "
                        + $"exceeds scale/2 = {bound}");
                }
            }
        }
    }

    [TestMethod]
    public void A2_Round_Trip_Scales_With_The_Data_Not_With_A_Constant()
    {
        // Two vectors with the same shape but different magnitudes must get different scales and the same
        // relative reconstruction error — the property absmax exists for.
        var small = new[] { 0.001f, -0.002f, 0.0015f };
        var large = new[] { 100f, -200f, 150f };

        var smallQuantised = VectorQuantizer.Quantize(small);
        var largeQuantised = VectorQuantizer.Quantize(large);

        Assert.AreNotEqual(smallQuantised.Scale, largeQuantised.Scale);
        Assert.AreEqual(100000d, largeQuantised.Scale / smallQuantised.Scale, 1e-2);

        // scale * 127 recovers the largest magnitude of each input, exactly the absmax property.
        Assert.AreEqual(0.002f, smallQuantised.Scale * VectorQuantizer.MaxCode, 1e-9f);
        Assert.AreEqual(200f, largeQuantised.Scale * VectorQuantizer.MaxCode, 1e-3f);
    }

    [TestMethod]
    public void A3_Dimension_Mismatch_And_Degenerate_Inputs_Fail_Closed()
    {
        // Empty vectors: there is no absmax and no direction.
        Assert.ThrowsExactly<ArgumentException>(() => VectorQuantizer.Quantize(ReadOnlySpan<float>.Empty));
        Assert.ThrowsExactly<ArgumentException>(() => VectorQuantizer.Dequantize(ReadOnlySpan<sbyte>.Empty, 1f));
        Assert.ThrowsExactly<ArgumentException>(() => new QuantizedVector([], 1f));

        // NaN / Infinity: clamping them to the extremes would invent a direction the embedding never had.
        Assert.ThrowsExactly<ArgumentException>(() => VectorQuantizer.Quantize(new[] { 1f, float.NaN }));
        Assert.ThrowsExactly<ArgumentException>(() => VectorQuantizer.Quantize(new[] { float.PositiveInfinity }));
        Assert.ThrowsExactly<ArgumentException>(() => VectorQuantizer.Quantize(new[] { -1f, float.NegativeInfinity }));

        // All-zero: rejected rather than accepted as "scale 0, all codes 0".
        Assert.ThrowsExactly<InvalidOperationException>(() => VectorQuantizer.Quantize(new float[4]));

        // A dimension mismatch is refused, never truncated (three components against a two-component query).
        var quantised = VectorQuantizer.Quantize(new[] { 3f, -1f, 2f });
        Assert.ThrowsExactly<ArgumentException>(() => QuantizedVectorMath.Cosine(quantised, new float[2]));
        Assert.ThrowsExactly<ArgumentException>(() => QuantizedVectorMath.Cosine(quantised, new float[4]));

        // A zero query is not scored either: the same rule as the float path, not a second, weaker one.
        Assert.ThrowsExactly<InvalidOperationException>(() => QuantizedVectorMath.Cosine(quantised, new float[3]));

        // Codes outside the symmetric range are rejected, never silently clamped into it. -128 is the one
        // value a signed byte can hold that absmax never produces.
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => VectorQuantizer.Dequantize(new sbyte[] { 0, -128, 1 }, 0.5f));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new QuantizedVector(new sbyte[] { -128 }, 0.5f));

        // Control: the same calls with in-range inputs must not throw, so the assertions above are about
        // the invalid input and not about a method that always throws.
        Assert.AreEqual(-1.5f, VectorQuantizer.Dequantize(new sbyte[] { -3 }, 0.5f)[0]);
        var fine = new QuantizedVector(new sbyte[] { -127, 127 }, 0.5f);
        Assert.AreEqual(2, fine.Dimensions);
    }

    [TestMethod]
    public void A4_Scale_Must_Be_Positive_And_Finite()
    {
        var codes = new sbyte[] { 1, -2, 3 };

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new QuantizedVector(codes, 0f));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new QuantizedVector(codes, -0.5f));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new QuantizedVector(codes, float.NaN));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new QuantizedVector(codes, float.PositiveInfinity));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new QuantizedVector(codes, float.NegativeInfinity));

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => VectorQuantizer.Dequantize(codes, 0f));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => VectorQuantizer.Dequantize(codes, float.NaN));

        // A zero-scale vector cannot be constructed through the quantiser either: the all-zero input is
        // refused before a scale is computed, so "scale = 0" never becomes a legal value anywhere.
        Assert.ThrowsExactly<InvalidOperationException>(() => VectorQuantizer.Quantize(new float[8]));

        var legal = new QuantizedVector(codes, 0.25f);
        Assert.AreEqual(0.25f, legal.Scale);
        Assert.AreEqual(3, legal.Dimensions);
        Assert.AreEqual(7, legal.StorageBytes, "three int8 components plus one float32 scale");
    }

    [TestMethod]
    public void Quantised_Vectors_Are_Copied_And_Scored_Through_The_Float_Cosine()
    {
        var codes = new sbyte[] { 100, -50, 25 };
        var entry = new QuantizedVector(codes, 0.01f);

        codes[0] = 0; // a caller mutating its own array must not change the stored vector

        Assert.AreEqual((sbyte)100, entry.Codes.Span[0]);

        var query = entry.Dequantize();
        Assert.AreEqual(1d, QuantizedVectorMath.Cosine(entry, query), 1e-9, "a vector against itself is 1");

        var opposite = query.Select(value => -value).ToArray();
        Assert.AreEqual(-1d, QuantizedVectorMath.Cosine(entry, opposite), 1e-9);
    }
}
