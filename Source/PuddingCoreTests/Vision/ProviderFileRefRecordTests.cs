using PuddingCode.Core;

namespace PuddingCoreTests.Vision;

/// <summary>ADR-077 V3-S2b-1：<see cref="ProviderFileRefRecord"/> 值类型与状态 wire 映射单测。</summary>
[TestClass]
public sealed class ProviderFileRefRecordTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 0, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void ToReference_MapsFileIdMimeTypeExpiresAt()
    {
        var record = NewRecord();

        var reference = record.ToReference();

        Assert.AreEqual("file-abc", reference.FileId);
        Assert.AreEqual("image/png", reference.MimeType);
        Assert.AreEqual(Now.AddHours(1), reference.ExpiresAt);
    }

    [TestMethod]
    public void StatusWire_RoundTripsAllValues()
    {
        foreach (var status in Enum.GetValues<ProviderFileRefStatus>())
        {
            var wire = ProviderFileRefStatusWire.ToWire(status);
            Assert.AreEqual(status, ProviderFileRefStatusWire.FromWire(wire));
        }
    }

    [TestMethod]
    public void StatusWire_UsesAdrCompatibleSnakeCase()
    {
        Assert.AreEqual("uploading", ProviderFileRefStatusWire.ToWire(ProviderFileRefStatus.Uploading));
        Assert.AreEqual("ready", ProviderFileRefStatusWire.ToWire(ProviderFileRefStatus.Ready));
        Assert.AreEqual("delete_pending", ProviderFileRefStatusWire.ToWire(ProviderFileRefStatus.DeletePending));
        Assert.AreEqual("expired", ProviderFileRefStatusWire.ToWire(ProviderFileRefStatus.Expired));
        Assert.AreEqual("failed", ProviderFileRefStatusWire.ToWire(ProviderFileRefStatus.Failed));
    }

    [TestMethod]
    public void NearExpirySkew_IsFiveMinutes()
    {
        Assert.AreEqual(300, IFileRefStore.FileRefNearExpirySkewSeconds);
    }

    private static ProviderFileRefRecord NewRecord() => new(
        ProviderId: "deepseek",
        CredentialEpoch: "epoch-7",
        ArtifactId: "artifact-1",
        ArtifactSha256: "sha-1",
        RemoteFileId: "file-abc",
        Bytes: 1024,
        MimeType: "image/png",
        ExpiresAt: Now.AddHours(1),
        LastUsedAt: null,
        Status: ProviderFileRefStatus.Ready,
        CreatedAt: Now,
        UpdatedAt: Now);
}
