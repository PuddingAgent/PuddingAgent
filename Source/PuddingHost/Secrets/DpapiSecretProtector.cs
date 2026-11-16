using System.Security.Cryptography;
using System.Text;
using PuddingCode.Abstractions;

namespace PuddingHost.Secrets;

/// <summary>
/// 用 Windows DPAPI（当前用户范围）实现 <see cref="ISecretProtector"/>。
///
/// <para>为什么是 DPAPI 而不是自己用 AES：自己加密就要自己管密钥，密钥放哪又成了同一个问题。
/// DPAPI 把主密钥交给操作系统按登录用户派生，进程不需要任何额外秘密即可解密——
/// 这正是"本机本用户才能读"的语义。</para>
///
/// <para>附加熵（entropy）把密文绑定到这个应用：同一用户下的其他程序即使找到文件，
/// 不知道这串也解不开。注意熵只提高门槛，不是密钥。</para>
///
/// <para>只能运行在 Windows；其他平台显式抛 <see cref="PlatformNotSupportedException"/>，
/// 不要静默降级成"不加密"。</para>
/// </summary>
public sealed class DpapiSecretProtector : ISecretProtector
{
    /// <summary>应用级附加熵。改动它会让已有密文全部解不开，等同销毁。</summary>
    private static readonly byte[] Entropy =
        Encoding.UTF8.GetBytes("PuddingAgent.SecretStore.v1");

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("DPAPI secret protection requires Windows.");
    }

    /// <inheritdoc />
    public byte[] Protect(ReadOnlySpan<byte> plaintext)
    {
        EnsureWindows();
        return ProtectedData.Protect(
            plaintext.ToArray(),
            Entropy,
            DataProtectionScope.CurrentUser);
    }

    /// <inheritdoc />
    public byte[] Unprotect(ReadOnlySpan<byte> protectedPayload)
    {
        EnsureWindows();
        return ProtectedData.Unprotect(
            protectedPayload.ToArray(),
            Entropy,
            DataProtectionScope.CurrentUser);
    }
}
