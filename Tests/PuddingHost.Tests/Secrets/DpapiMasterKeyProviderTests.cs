using PuddingHost.Secrets;

namespace PuddingHost.Tests.Secrets;

/// <summary>
/// 主密钥持久化的用例。
///
/// <para>这一层的价值全在"重启还能解开"上，所以断言围绕三件事：
/// 写入后能读回同一把密钥；文件里不是裸密钥；坏文件降级为 null 而不是让启动崩掉。</para>
/// </summary>
public sealed class DpapiMasterKeyProviderTests
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "pudding-master-key", Guid.NewGuid().ToString("N"));

    private string KeyPath => Path.Combine(_directory, "keyvault.master");

    private static bool SkipIfNotWindows() => !OperatingSystem.IsWindows();

    [Fact]
    public void ResolveReturnsNullWhenNoFileExists()
    {
        var provider = new DpapiMasterKeyProvider(KeyPath, new DpapiSecretProtector());

        Assert.Null(provider.TryResolve());
    }

    [Fact]
    public void PersistedKeyCanBeResolvedBack()
    {
        if (SkipIfNotWindows()) return;
        var provider = new DpapiMasterKeyProvider(KeyPath, new DpapiSecretProtector());
        var key = Enumerable.Range(0, DpapiMasterKeyProvider.MasterKeySizeBytes)
            .Select(i => (byte)i).ToArray();

        provider.Persist(key);

        Assert.Equal(key, provider.TryResolve());
    }

    [Fact]
    public void FileDoesNotContainTheRawKey()
    {
        if (SkipIfNotWindows()) return;
        var provider = new DpapiMasterKeyProvider(KeyPath, new DpapiSecretProtector());
        var key = Enumerable.Repeat((byte)0x42, DpapiMasterKeyProvider.MasterKeySizeBytes).ToArray();

        provider.Persist(key);

        var bytes = File.ReadAllBytes(KeyPath);
        // 封装后必须比裸密钥长，且不能恰好等于它。
        Assert.True(bytes.Length > key.Length);
        Assert.False(bytes.SequenceEqual(key));
        // 连续 32 个 0x42 不应作为子串出现在文件里。
        Assert.Equal(-1, IndexOf(bytes, key));
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
        {
            var hit = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) { hit = false; break; }
            }

            if (hit) return i;
        }

        return -1;
    }

    [Fact]
    public void CorruptFileFailsClosedWithoutOverwritingAndWarns()
    {
        if (SkipIfNotWindows()) return;
        Directory.CreateDirectory(_directory);
        File.WriteAllBytes(KeyPath, [1, 2, 3, 4, 5, 6, 7, 8]);
        var warnings = new List<string>();
        var provider = new DpapiMasterKeyProvider(KeyPath, new DpapiSecretProtector(), warnings.Add);

        Assert.Throws<InvalidOperationException>(() => provider.TryResolve());
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, File.ReadAllBytes(KeyPath));
        Assert.Single(warnings);
    }

    [Fact]
    public void WrongLengthKeyIsRejectedBeforeWriting()
    {
        var provider = new DpapiMasterKeyProvider(KeyPath, new DpapiSecretProtector());

        Assert.Throws<ArgumentException>(() => provider.Persist(new byte[16]));
        Assert.False(File.Exists(KeyPath));
    }

    [Fact]
    public void ResolvingTwiceReturnsTheSameKey()
    {
        if (SkipIfNotWindows()) return;
        var first = new DpapiMasterKeyProvider(KeyPath, new DpapiSecretProtector());
        first.Persist(Enumerable.Repeat((byte)7, DpapiMasterKeyProvider.MasterKeySizeBytes).ToArray());

        // 新建一个实例，模拟"重启后重新解析"。
        var second = new DpapiMasterKeyProvider(KeyPath, new DpapiSecretProtector());

        Assert.Equal(first.TryResolve(), second.TryResolve());
    }
}
