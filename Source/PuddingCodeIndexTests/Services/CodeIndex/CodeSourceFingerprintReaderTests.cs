using System.Security.Cryptography;
using System.Text;

using PuddingCodeIndex.Services.CodeIndex;

namespace PuddingCodeIndexTests.Services.CodeIndex;

/// <summary>
/// 稳定读门禁：指纹里的 hash 必须是**真正那份内容**的 hash，且读前读后 stat 必须一致。
/// 读不到 / 超大 / 目录都不是「不稳定」而是带原因的失败（调用方据此决定退避还是忽略）。
/// </summary>
[TestClass]
public sealed class CodeSourceFingerprintReaderTests : IDisposable
{
    private string _root = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "pudding-d4-fingerprint-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestMethod]
    public async Task StableFileProducesTheHashOfItsExactContent()
    {
        var path = Path.Combine(_root, "A.cs");
        var content = "class A { }\n";
        await File.WriteAllTextAsync(path, content);

        var result = await new CodeSourceFingerprintReader().ReadAsync(path);

        Assert.IsTrue(result.Stable, result.Reason);
        Assert.IsNotNull(result.Fingerprint);
        Assert.AreEqual(
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant(),
            result.Fingerprint!.ContentHash);
        Assert.AreEqual(new FileInfo(path).Length, result.Fingerprint.Length);
    }

    [TestMethod]
    public async Task ContentChangeChangesTheHash()
    {
        var path = Path.Combine(_root, "A.cs");
        await File.WriteAllTextAsync(path, "class A { }");
        var first = await new CodeSourceFingerprintReader().ReadAsync(path);

        await File.WriteAllTextAsync(path, "class A { int X; }");
        var second = await new CodeSourceFingerprintReader().ReadAsync(path);

        Assert.IsTrue(first.Stable);
        Assert.IsTrue(second.Stable);
        Assert.AreNotEqual(first.Fingerprint!.ContentHash, second.Fingerprint!.ContentHash);
    }

    [TestMethod]
    public async Task AMissingFileIsReportedAsAFailureNotAsInstability()
    {
        var result = await new CodeSourceFingerprintReader().ReadAsync(Path.Combine(_root, "gone.cs"));

        Assert.IsFalse(result.Stable);
        Assert.IsNull(result.Fingerprint);
        StringAssert.Contains(result.Reason!, "disappeared");
    }

    [TestMethod]
    public async Task FilesAboveTheLimitAreRefusedInsteadOfBeingReadIntoMemory()
    {
        var path = Path.Combine(_root, "big.cs");
        await File.WriteAllTextAsync(path, new string('x', 4096));

        var result = await new CodeSourceFingerprintReader(maxFileBytes: 1024).ReadAsync(path);

        Assert.IsFalse(result.Stable);
        Assert.IsNull(result.Fingerprint);
        StringAssert.Contains(result.Reason!, "limit");
    }

    [TestMethod]
    public void AnInvalidLimitIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CodeSourceFingerprintReader(maxFileBytes: 0));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // temp 目录清理是 best-effort
        }
    }
}
