namespace HarnessAgent.Core.Secrets;

/// <summary>
/// 密钥保护器：把明文变成只能由本机本用户解开的字节。
///
/// <para>抽成接口不是为了将来换算法，而是为了<strong>可测</strong>：
/// 真实实现依赖 Windows DPAPI，在测试里无法断言"文件里没有明文"，
/// 用可控的替身才能把那条断言写成真的。</para>
/// </summary>
public interface ISecretProtector
{
    /// <summary>加密。失败应抛异常，不要返回空数组——静默降级会把明文写进文件。</summary>
    byte[] Protect(ReadOnlySpan<byte> plaintext);

    /// <summary>解密。密文损坏或被换机时应抛异常。</summary>
    byte[] Unprotect(ReadOnlySpan<byte> protectedPayload);
}
