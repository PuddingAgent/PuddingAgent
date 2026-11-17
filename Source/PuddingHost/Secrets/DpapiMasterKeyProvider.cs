using System.Security.Cryptography;
using PuddingCode.Abstractions;

namespace PuddingHost.Secrets;

/// <summary>
/// 用 DPAPI 把 KeyVault 主密钥封装成文件，解决"重启即失联"。
///
/// <para>为什么不是直接存 32 字节：那等于把主密钥摊在磁盘上，任何能读文件的进程都能解密整个保管箱。
/// 经 <see cref="ISecretProtector"/> 封装后，只有<b>同一 Windows 用户</b>能解开——
/// 换机、换用户都失效，而这正是想要的性质。</para>
///
/// <para>只有文件不存在才返回 null。已有文件损坏必须拒绝启动，避免生成新密钥覆盖恢复证据。</para>
/// </summary>
public sealed class DpapiMasterKeyProvider : IMasterKeyProvider
{
    /// <summary>主密钥固定长度，与 KeyVaultService 的 AES-256 一致。</summary>
    public const int MasterKeySizeBytes = 32;

    private readonly string _path;
    private readonly ISecretProtector _protector;
    private readonly Action<string>? _warn;

    /// <param name="path">封装后的主密钥文件路径。</param>
    /// <param name="protector">保护器，通常是 <see cref="DpapiSecretProtector"/>。</param>
    /// <param name="warn">告警回调；测试里可以收集它。</param>
    public DpapiMasterKeyProvider(
        string path,
        ISecretProtector protector,
        Action<string>? warn = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(protector);
        _path = Path.GetFullPath(path);
        _protector = protector;
        _warn = warn;
    }

    /// <summary>主密钥文件路径（绝对）。</summary>
    public string FilePath => _path;

    /// <inheritdoc />
    public byte[]? TryResolve()
    {
        if (!File.Exists(_path))
            return null;

        try
        {
            var envelope = File.ReadAllBytes(_path);
            var key = _protector.Unprotect(envelope);
            if (key.Length == MasterKeySizeBytes)
                return key;

            _warn?.Invoke(
                $"[KeyVault] master key file has {key.Length} bytes, expected {MasterKeySizeBytes}.");
            throw new InvalidOperationException("Persisted KeyVault master key has an invalid length. Restore the original protected key file.");
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            _warn?.Invoke(
                $"[KeyVault] master key file could not be read ({ex.GetType().Name}); existing secrets will be unreadable.");
            throw new InvalidOperationException("Persisted KeyVault master key cannot be decrypted. Restore it for the original Windows account.");
        }
    }

    /// <inheritdoc />
    public void Persist(ReadOnlySpan<byte> masterKey)
    {
        if (masterKey.Length != MasterKeySizeBytes)
        {
            throw new ArgumentException(
                $"master key must be {MasterKeySizeBytes} bytes, got {masterKey.Length}",
                nameof(masterKey));
        }

        var envelope = _protector.Protect(masterKey);
        if (envelope.Length == 0)
            throw new InvalidOperationException("master key protector returned an empty payload");

        var directory = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
            System.IO.Directory.CreateDirectory(directory);

        // 原子写：先临时文件再替换，避免崩溃留下半个主密钥（那等于全部密钥失联）。
        var temp = _path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(temp, envelope);
            File.Move(temp, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }
}
