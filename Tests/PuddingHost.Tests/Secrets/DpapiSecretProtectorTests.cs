using System.Security.Cryptography;
using System.Text;
using HarnessAgent.Core.Secrets;
using PuddingHost.Secrets;

namespace PuddingHost.Tests.Secrets;

/// <summary>
/// DPAPI 保护器的用例。
///
/// <para>这里测的是"保护器确实保护了"，而不是"DPAPI 实现正确"——
/// 后者不是我们的代码。所以断言集中在三件事：能还原、密文里没有明文、
/// 同一输入两次加密结果不同（证明不是恒等变换）。</para>
/// </summary>
public sealed class DpapiSecretProtectorTests
{
    private static bool SkipIfNotWindows() => !OperatingSystem.IsWindows();

    [Fact]
    public void RoundTripsAPayload()
    {
        if (SkipIfNotWindows()) return;
        var protector = new DpapiSecretProtector();
        var plaintext = Encoding.UTF8.GetBytes("authorization-code-1234567890");

        var cipher = protector.Protect(plaintext);
        var back = protector.Unprotect(cipher);

        Assert.Equal(plaintext, back);
    }

    [Fact]
    public void CiphertextDoesNotContainThePlaintext()
    {
        if (SkipIfNotWindows()) return;
        var protector = new DpapiSecretProtector();
        const string secret = "qq-auth-code-abcdefghijklmn";
        var cipher = protector.Protect(Encoding.UTF8.GetBytes(secret));

        Assert.DoesNotContain(secret, Encoding.UTF8.GetString(cipher));
        Assert.DoesNotContain(secret, Encoding.Latin1.GetString(cipher));
    }

    [Fact]
    public void EncryptingTheSameValueTwiceProducesDifferentCiphertext()
    {
        if (SkipIfNotWindows()) return;
        var protector = new DpapiSecretProtector();
        var plaintext = Encoding.UTF8.GetBytes("same-input");

        var first = protector.Protect(plaintext);
        var second = protector.Protect(plaintext);

        // 若恒等（或确定性），说明保护器没有真正参与。
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void TamperedCiphertextThrows()
    {
        if (SkipIfNotWindows()) return;
        var protector = new DpapiSecretProtector();
        var cipher = protector.Protect(Encoding.UTF8.GetBytes("payload"));
        cipher[cipher.Length / 2] ^= 0xFF;

        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(cipher));
    }

    [Fact]
    public void ForeignPayloadThrows()
    {
        if (SkipIfNotWindows()) return;
        var protector = new DpapiSecretProtector();

        Assert.ThrowsAny<CryptographicException>(
            () => protector.Unprotect(Encoding.UTF8.GetBytes("not-a-dpapi-blob")));
    }
}
