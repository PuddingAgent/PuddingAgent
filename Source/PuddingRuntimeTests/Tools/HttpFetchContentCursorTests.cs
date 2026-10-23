using System.Text;
using PuddingRuntime.Services.Skills;

namespace PuddingRuntimeTests.Tools;

[TestClass]
public sealed class HttpFetchContentCursorTests
{
    [TestMethod]
    public void RoundTrips_Through_Encode_And_Decode()
    {
        var original = new HttpFetchContentCursor("markdown", 4096, "abc123def456");

        var wire = original.Encode();

        Assert.IsTrue(HttpFetchContentCursor.TryDecode(wire, out var decoded));
        Assert.AreEqual(original, decoded);
    }

    [TestMethod]
    public void Wire_Form_Is_Url_Safe_And_Unpadded()
    {
        var wire = new HttpFetchContentCursor("markdown", 1, "000000000000").Encode();

        Assert.IsFalse(wire.Contains('+'), wire);
        Assert.IsFalse(wire.Contains('/'), wire);
        Assert.IsFalse(wire.Contains('='), wire);
    }

    [TestMethod]
    [DataRow("not-base64!!!")]
    [DataRow("")]
    [DataRow("   ")]
    public void Rejects_Malformed_Wire_Values(string wire)
    {
        Assert.IsFalse(HttpFetchContentCursor.TryDecode(wire, out _));
    }

    [TestMethod]
    public void Rejects_An_Unknown_Version()
    {
        var raw = Convert.ToBase64String(Encoding.UTF8.GetBytes("v9|markdown|10|abc"));

        Assert.IsFalse(HttpFetchContentCursor.TryDecode(raw, out _));
    }

    [TestMethod]
    public void Rejects_A_Negative_Offset()
    {
        var raw = Convert.ToBase64String(Encoding.UTF8.GetBytes("v1|markdown|-5|abc"));

        Assert.IsFalse(HttpFetchContentCursor.TryDecode(raw, out _));
    }

    [TestMethod]
    public void Fingerprint_Is_Stable_And_Content_Sensitive()
    {
        Assert.AreEqual(
            HttpFetchContentCursor.FingerprintOf("hello"),
            HttpFetchContentCursor.FingerprintOf("hello"));
        Assert.AreNotEqual(
            HttpFetchContentCursor.FingerprintOf("hello"),
            HttpFetchContentCursor.FingerprintOf("hello!"));
    }
}
