using PuddingCode.Core;

namespace PuddingCoreTests.Vision;

/// <summary>
/// V5-T4：视觉合同「只能收紧、不得放宽」投影语义测试（ADR-088 / 设计 §4.1 交集语义，产品策略为天花板）。
/// 覆盖 7 个上限维度（maxImagesPerRequest / inlineMaxBytesPerImage（解码后字节）/ inlineMaxTotalBytes
/// （解码后字节）/ inlineMaxTotalWireBytes（wire 字节）/ filesMaxBytesPerImage（上传原始编码字节）/
/// filesMaxTotalBytes（wire 字节）/ estimatedTokensPerImageUpperBound）的
/// 更紧（保留）/ 更松（钳制回产品默认）/ 相等 / 缺失（产品默认）四情形，
/// 以及 Version 保持与逐维度独立钳制。
/// </summary>
[TestClass]
public sealed class VisionCapabilityContractTests
{
    /// <summary>特意不同于 VisionRequestPolicy.Default.ImageTokenEstimatorVersion，验证版本只来自合同。</summary>
    private const string ContractVersion = "contract-t4-version-check";

    [TestMethod]
    public void ToPolicy_TighterThanDefault_KeepsConfiguredValues_And_PreservesVersion()
    {
        var contract = new VisionCapabilityContract
        {
            Version = ContractVersion,
            MaxImagesPerRequest = 4,
            InlineMaxBytesPerImage = 1_000_000L,
            InlineMaxTotalBytes = 20L * 1024 * 1024,
            InlineMaxTotalWireBytes = 32L * 1024 * 1024,
            FilesMaxBytesPerImage = 32L * 1024 * 1024,
            FilesMaxTotalBytes = 100L * 1024 * 1024,
            EstimatedTokensPerImageUpperBound = 512,
        };

        var policy = contract.ToPolicy();

        Assert.AreEqual(4, policy.MaxImagesPerRequest);
        Assert.AreEqual(1_000_000L, policy.InlineMaxBytesPerImage);
        Assert.AreEqual(20L * 1024 * 1024, policy.InlineMaxTotalBytes);
        Assert.AreEqual(32L * 1024 * 1024, policy.InlineMaxTotalWireBytes);
        Assert.AreEqual(32L * 1024 * 1024, policy.FilesMaxBytesPerImage);
        Assert.AreEqual(100L * 1024 * 1024, policy.FilesMaxTotalBytes);
        Assert.AreEqual(512, policy.EstimatedTokensPerImageUpperBound);
        Assert.AreEqual(ContractVersion, policy.ImageTokenEstimatorVersion);
    }

    [TestMethod]
    public void ToPolicy_LooserThanDefault_IsClampedToProductDefault_EveryDimension()
    {
        var d = VisionRequestPolicy.Default;
        var contract = new VisionCapabilityContract
        {
            Version = ContractVersion,
            MaxImagesPerRequest = 600,                    // 官方允许 600，产品收口 8
            InlineMaxBytesPerImage = 3_000_000L,          // 解码后字节，产品门槛 2,000,000
            InlineMaxTotalBytes = 80L * 1024 * 1024,      // 解码后字节，产品门槛 40 MiB
            InlineMaxTotalWireBytes = 128L * 1024 * 1024, // wire 字节，产品门槛 64 MiB
            FilesMaxBytesPerImage = 128L * 1024 * 1024,   // 上传原始编码字节，官方硬限 64 MiB
            FilesMaxTotalBytes = 256L * 1024 * 1024,      // wire 字节，官方硬限 200 MiB
            EstimatedTokensPerImageUpperBound = 2048,
        };

        var policy = contract.ToPolicy();

        Assert.AreEqual(d.MaxImagesPerRequest, policy.MaxImagesPerRequest);
        Assert.AreEqual(d.InlineMaxBytesPerImage, policy.InlineMaxBytesPerImage);
        Assert.AreEqual(d.InlineMaxTotalBytes, policy.InlineMaxTotalBytes);
        Assert.AreEqual(d.InlineMaxTotalWireBytes, policy.InlineMaxTotalWireBytes);
        Assert.AreEqual(d.FilesMaxBytesPerImage, policy.FilesMaxBytesPerImage);
        Assert.AreEqual(d.FilesMaxTotalBytes, policy.FilesMaxTotalBytes);
        Assert.AreEqual(d.EstimatedTokensPerImageUpperBound, policy.EstimatedTokensPerImageUpperBound);
        // 钳制只作用于数值维度；Version 仍取合同 Version。
        Assert.AreEqual(ContractVersion, policy.ImageTokenEstimatorVersion);
    }

    [TestMethod]
    public void ToPolicy_MissingFields_FallBackToProductDefault_And_PreserveVersion()
    {
        // 夹具约定：合同版本必须不同于产品默认版本，否则本用例无法证明版本来源是合同。
        Assert.AreNotEqual(VisionRequestPolicy.Default.ImageTokenEstimatorVersion, ContractVersion);

        var contract = new VisionCapabilityContract { Version = ContractVersion };

        var policy = contract.ToPolicy();
        var d = VisionRequestPolicy.Default;

        Assert.AreEqual(d.MaxImagesPerRequest, policy.MaxImagesPerRequest);
        Assert.AreEqual(d.InlineMaxBytesPerImage, policy.InlineMaxBytesPerImage);
        Assert.AreEqual(d.InlineMaxTotalBytes, policy.InlineMaxTotalBytes);
        Assert.AreEqual(d.InlineMaxTotalWireBytes, policy.InlineMaxTotalWireBytes);
        Assert.AreEqual(d.FilesMaxBytesPerImage, policy.FilesMaxBytesPerImage);
        Assert.AreEqual(d.FilesMaxTotalBytes, policy.FilesMaxTotalBytes);
        Assert.AreEqual(d.EstimatedTokensPerImageUpperBound, policy.EstimatedTokensPerImageUpperBound);
        Assert.AreEqual(ContractVersion, policy.ImageTokenEstimatorVersion);
    }

    [TestMethod]
    public void ToPolicy_EqualToDefault_PassesThroughUnchanged()
    {
        var d = VisionRequestPolicy.Default;
        var contract = new VisionCapabilityContract
        {
            Version = ContractVersion,
            MaxImagesPerRequest = d.MaxImagesPerRequest,
            InlineMaxBytesPerImage = d.InlineMaxBytesPerImage,
            InlineMaxTotalBytes = d.InlineMaxTotalBytes,
            InlineMaxTotalWireBytes = d.InlineMaxTotalWireBytes,
            FilesMaxBytesPerImage = d.FilesMaxBytesPerImage,
            FilesMaxTotalBytes = d.FilesMaxTotalBytes,
            EstimatedTokensPerImageUpperBound = d.EstimatedTokensPerImageUpperBound,
        };

        var policy = contract.ToPolicy();

        Assert.AreEqual(d.MaxImagesPerRequest, policy.MaxImagesPerRequest);
        Assert.AreEqual(d.InlineMaxBytesPerImage, policy.InlineMaxBytesPerImage);
        Assert.AreEqual(d.InlineMaxTotalBytes, policy.InlineMaxTotalBytes);
        Assert.AreEqual(d.InlineMaxTotalWireBytes, policy.InlineMaxTotalWireBytes);
        Assert.AreEqual(d.FilesMaxBytesPerImage, policy.FilesMaxBytesPerImage);
        Assert.AreEqual(d.FilesMaxTotalBytes, policy.FilesMaxTotalBytes);
        Assert.AreEqual(d.EstimatedTokensPerImageUpperBound, policy.EstimatedTokensPerImageUpperBound);
        Assert.AreEqual(ContractVersion, policy.ImageTokenEstimatorVersion);
    }

    [TestMethod]
    public void ToPolicy_Clamping_IsIndependent_PerDimension()
    {
        var d = VisionRequestPolicy.Default;
        var contract = new VisionCapabilityContract
        {
            Version = ContractVersion,
            MaxImagesPerRequest = 4,                      // 更紧 → 保留
            InlineMaxBytesPerImage = 3_000_000L,          // 更松 → 钳制回产品门槛（解码后字节）
            InlineMaxTotalBytes = null,                   // 缺失 → 产品默认
            InlineMaxTotalWireBytes = 32L * 1024 * 1024,  // 更紧 → 保留（wire 字节）
            FilesMaxBytesPerImage = 128L * 1024 * 1024,   // 更松 → 钳制回官方硬限（上传原始编码字节）
            FilesMaxTotalBytes = null,                    // 缺失 → 产品默认
            EstimatedTokensPerImageUpperBound = 512,      // 更紧 → 保留
        };

        var policy = contract.ToPolicy();

        Assert.AreEqual(4, policy.MaxImagesPerRequest);
        Assert.AreEqual(d.InlineMaxBytesPerImage, policy.InlineMaxBytesPerImage);
        Assert.AreEqual(d.InlineMaxTotalBytes, policy.InlineMaxTotalBytes);
        Assert.AreEqual(32L * 1024 * 1024, policy.InlineMaxTotalWireBytes);
        Assert.AreEqual(d.FilesMaxBytesPerImage, policy.FilesMaxBytesPerImage);
        Assert.AreEqual(d.FilesMaxTotalBytes, policy.FilesMaxTotalBytes);
        Assert.AreEqual(512, policy.EstimatedTokensPerImageUpperBound);
    }
}
