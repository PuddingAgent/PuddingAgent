using PuddingVectorIndex;

namespace PuddingVectorIndexTests;

/// <summary>
/// Cosine / normalisation correctness against hand-computed expected values. These are the numbers a
/// mutation of the formula must break (see the U4-1b report's A3 section: dropping the denominator
/// turns every one of these red).
/// </summary>
[TestClass]
public sealed class VectorMathTests
{
    [TestMethod]
    public void Dot_Matches_Hand_Computed_Value()
    {
        Assert.AreEqual(32d, VectorMath.Dot([1f, 2f, 3f], [4f, 5f, 6f]), 1e-9);
    }

    [TestMethod]
    public void Norm_Of_Pythagorean_Triple_Is_Five()
    {
        Assert.AreEqual(5d, VectorMath.Norm([3f, 4f]), 1e-9);
    }

    [TestMethod]
    public void Cosine_Of_Identical_Direction_Is_One_And_Opposite_Is_Minus_One()
    {
        Assert.AreEqual(1d, VectorMath.Cosine([1f, 2f, 3f], [2f, 4f, 6f]), 1e-9);
        Assert.AreEqual(-1d, VectorMath.Cosine([1f, 2f, 3f], [-1f, -2f, -3f]), 1e-9);
    }

    [TestMethod]
    public void Cosine_Of_Orthogonal_Vectors_Is_Zero()
    {
        Assert.AreEqual(0d, VectorMath.Cosine([1f, 0f, 0f], [0f, 1f, 0f]), 1e-9);
    }

    [TestMethod]
    public void Cosine_Matches_Hand_Computed_Value()
    {
        // 32 / (sqrt(14) * sqrt(77)) = 0.9746318461970762
        Assert.AreEqual(0.9746318461970762d, VectorMath.Cosine([1f, 2f, 3f], [4f, 5f, 6f]), 1e-9);
    }

    [TestMethod]
    public void Cosine_Is_Scale_Invariant()
    {
        // Only exactly-representable scalings are used here: 0.4f is not 0.4, so a 10x-scaling check
        // would fail on float32 input rounding (2.5e-9) rather than on the formula — which would make
        // this a flaky assertion instead of a correctness one.
        var baseline = VectorMath.Cosine([1f, 2f, 3f], [4f, 5f, 6f]);

        Assert.AreEqual(baseline, VectorMath.Cosine([2f, 4f, 6f], [4f, 5f, 6f]), 1e-12);
        Assert.AreEqual(baseline, VectorMath.Cosine([1f, 2f, 3f], [8f, 10f, 12f]), 1e-12);
        Assert.AreEqual(-baseline, VectorMath.Cosine([1f, 2f, 3f], [-8f, -10f, -12f]), 1e-12);
    }

    [TestMethod]
    public void Normalize_Produces_Unit_Vector()
    {
        var normalized = VectorMath.Normalize([3f, 4f]);

        Assert.AreEqual(0.6f, normalized[0], 1e-6f);
        Assert.AreEqual(0.8f, normalized[1], 1e-6f);
        Assert.AreEqual(1d, VectorMath.Norm(normalized), 1e-6);
    }

    [TestMethod]
    public void Dimension_Mismatch_Throws_Instead_Of_Truncating()
    {
        Assert.ThrowsExactly<ArgumentException>(() => VectorMath.Dot([1f, 2f, 3f], [1f, 2f]));
        Assert.ThrowsExactly<ArgumentException>(() => VectorMath.Cosine([1f, 2f, 3f], [1f, 2f]));
    }

    [TestMethod]
    public void Zero_Vector_Throws_Instead_Of_Returning_An_Invented_Score()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() => VectorMath.Cosine([0f, 0f], [1f, 1f]));
        Assert.ThrowsExactly<InvalidOperationException>(() => VectorMath.Cosine([1f, 1f], [0f, 0f]));
        Assert.ThrowsExactly<InvalidOperationException>(() => VectorMath.Normalize([0f, 0f]));
        // Empty input has no direction either, so it follows the same rule rather than scoring 0.
        Assert.ThrowsExactly<InvalidOperationException>(() => VectorMath.Cosine([], []));
    }
}
