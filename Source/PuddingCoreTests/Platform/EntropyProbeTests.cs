using PuddingCode.Platform;

namespace PuddingCoreTests.Platform;

[TestClass]
public sealed class EntropyProbeTests
{
    [TestMethod]
    public void Measure_ReturnsByteCountsWithoutRetainingPayload()
    {
        var metrics = EntropyProbe.Measure("重复内容 重复内容 重复内容");

        Assert.IsGreaterThan(0L, metrics.RawUtf8Bytes);
        Assert.IsGreaterThan(0L, metrics.GzipBytes);
        Assert.IsGreaterThanOrEqualTo(1.0, metrics.GzipRatio);
        Assert.AreEqual(metrics.GzipRatio, EntropyProbe.ComputeGzipRatio("重复内容 重复内容 重复内容"));
    }

    [TestMethod]
    public void Measure_EmptyText_HasNoBytePayload()
    {
        var metrics = EntropyProbe.Measure(null);

        Assert.AreEqual(0L, metrics.RawUtf8Bytes);
        Assert.AreEqual(0L, metrics.GzipBytes);
        Assert.AreEqual(1.0, metrics.GzipRatio);
    }
}
